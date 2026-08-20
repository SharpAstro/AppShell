using System.IO.Pipes;
using Shouldly;
using Xunit;

namespace SharpAstro.AppShell.Tests;

/// <summary>
/// These drive real named pipes rather than a seam, because every property worth asserting here is
/// a property of the OS primitive: that a second claim on one name fails, that two names do not
/// collide, and that a hand-off to nothing returns instead of throwing. A fake would assert the
/// test double.
///
/// <para>Each test builds its channel from its own identity string so the suite can run in
/// parallel without two tests fighting over one pipe name.</para>
/// </summary>
public class InstanceGateTests
{
    private const string Scope = "appshell-tests";

    private static string UniqueIdentity(string name) => $"{name}-{Guid.NewGuid():N}";

    [Fact]
    public void A_channel_is_stable_for_one_identity_and_differs_between_two()
    {
        var identity = UniqueIdentity("stable");

        InstanceGate.ChannelFor(Scope, identity).ShouldBe(InstanceGate.ChannelFor(Scope, identity));
        InstanceGate.ChannelFor(Scope, identity).ShouldNotBe(InstanceGate.ChannelFor(Scope, UniqueIdentity("other")));
    }

    [Fact]
    public void The_scope_separates_two_apps_using_the_same_identity()
    {
        var identity = UniqueIdentity("shared");

        InstanceGate.ChannelFor("viewer-a", identity).ShouldNotBe(InstanceGate.ChannelFor("viewer-b", identity));
    }

    [Fact]
    public void The_readable_prefix_survives_but_illegal_characters_do_not()
    {
        // The prefix exists so a stuck pipe can be recognised by eye; a path separator in it would
        // make the name invalid rather than merely ugly.
        InstanceGate.ChannelFor("my app/v2", "x").ShouldStartWith("my-app-v2.");
    }

    [Fact]
    public void The_first_claim_wins_and_the_second_is_refused()
    {
        var channel = InstanceGate.ChannelFor(Scope, UniqueIdentity("exclusive"));

        using var first = InstanceGate.TryClaim(channel);
        first.ShouldNotBeNull();

        // The refusal IS the signal to hand off, so it must be a null rather than an exception.
        InstanceGate.TryClaim(channel).ShouldBeNull();
    }

    [Fact]
    public void Releasing_a_claim_lets_the_next_process_take_it()
    {
        var channel = InstanceGate.ChannelFor(Scope, UniqueIdentity("rebind"));

        var first = InstanceGate.TryClaim(channel);
        first.ShouldNotBeNull();
        first.Dispose();

        // This is what makes re-binding on a folder change possible: a disposed gate must not leave
        // the name reserved, or the app could never move to a different folder and back.
        using var second = InstanceGate.TryClaim(channel);
        second.ShouldNotBeNull();
    }

    [Fact]
    public void Two_identities_are_claimed_independently()
    {
        var a = InstanceGate.ChannelFor(Scope, UniqueIdentity("folder-a"));
        var b = InstanceGate.ChannelFor(Scope, UniqueIdentity("folder-b"));

        using var first = InstanceGate.TryClaim(a);
        using var second = InstanceGate.TryClaim(b);

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_payload_reaches_the_holder()
    {
        var channel = InstanceGate.ChannelFor(Scope, UniqueIdentity("handoff"));
        using var gate = InstanceGate.TryClaim(channel);
        gate.ShouldNotBeNull();

        InstanceGate.TryHandOff(channel, @"C:\lights\m42.fits", TimeSpan.FromSeconds(5)).ShouldBeTrue();

        // The accept loop runs on its own thread, so the queue is filled a moment after the client
        // returns. Poll on the queue rather than sleeping a guessed interval.
        var request = await Dequeue(gate);
        request.Payload.ShouldBe(@"C:\lights\m42.fits");
    }

    [Fact]
    public async Task The_holder_receives_every_payload_in_order()
    {
        var channel = InstanceGate.ChannelFor(Scope, UniqueIdentity("ordered"));
        using var gate = InstanceGate.TryClaim(channel);
        gate.ShouldNotBeNull();

        foreach (var n in new[] { "one", "two", "three" })
        {
            InstanceGate.TryHandOff(channel, n, TimeSpan.FromSeconds(5)).ShouldBeTrue();
        }

        (await Dequeue(gate)).Payload.ShouldBe("one");
        (await Dequeue(gate)).Payload.ShouldBe("two");
        (await Dequeue(gate)).Payload.ShouldBe("three");
    }

    [Fact]
    public async Task An_empty_payload_arrives_as_an_activate_only_request()
    {
        // What an app with no file associations needs: a second launch has nothing to open but
        // still wants the running window in front. Dropping it would let a whole-app gate claim
        // primacy and then do nothing with it, which looks exactly like the gate not working.
        var channel = InstanceGate.ChannelFor(Scope, UniqueIdentity("activate-only"));
        using var gate = InstanceGate.TryClaim(channel);
        gate.ShouldNotBeNull();

        InstanceGate.TryHandOff(channel, string.Empty, TimeSpan.FromSeconds(5)).ShouldBeTrue();

        (await Dequeue(gate)).Payload.ShouldBe(string.Empty);
    }

    [Fact]
    public void A_handoff_to_nobody_returns_false_rather_than_throwing()
    {
        // The caller's next move is to open the document itself, so this must be a return value.
        // A throw here would mean a double-click that does nothing.
        var channel = InstanceGate.ChannelFor(Scope, UniqueIdentity("nobody"));

        InstanceGate.TryHandOff(channel, "whatever", TimeSpan.FromMilliseconds(250)).ShouldBeFalse();
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        var gate = InstanceGate.TryClaim(InstanceGate.ChannelFor(Scope, UniqueIdentity("double-dispose")));
        gate.ShouldNotBeNull();

        gate.Dispose();
        Should.NotThrow(() => gate.Dispose());
    }

    [Fact]
    public void Disposing_while_a_hand_off_is_in_flight_does_not_take_the_process_down()
    {
        // The accept loop must survive its own shutdown. It used to catch through a filter on the
        // stopping flag, and a filter that declines does not swallow: the exception left AcceptLoop,
        // went unhandled on that thread, and killed the PROCESS -- so this does not fail as a red
        // test, it fails by taking the run with it. That is also why it is a loop rather than one
        // shot: the window is between a client connecting and the accept thread reading, and on a
        // busy machine it takes a handful of attempts to land in it.
        //
        // Cross-platform on purpose. It was found on Linux, where a named pipe is a Unix-domain
        // socket and the timing differs enough to hit the window far more often, but nothing about
        // the defect was Linux-specific.
        for (var i = 0; i < 40; i++)
        {
            var channel = InstanceGate.ChannelFor(Scope, UniqueIdentity($"dispose-race-{i}"));
            var gate = InstanceGate.TryClaim(channel);
            gate.ShouldNotBeNull();

            // Do not wait for it to arrive: the point is to dispose WHILE it is in flight.
            InstanceGate.TryHandOff(channel, "in-flight", TimeSpan.FromSeconds(5));
            Should.NotThrow(gate.Dispose);
        }
    }

    [Fact]
    public async Task A_client_that_gives_up_before_writing_does_not_deafen_the_holder()
    {
        // A hand-off can die between the holder accepting and the payload arriving -- the launcher is
        // killed, or the pipe breaks. Releasing the connection used to be gated on IsConnected, which
        // tracks the LOCAL handle and is already false by then, so the instance was never disconnected
        // and every later hand-off met a listener that could only throw. One lost double-click is
        // acceptable; a holder that is deaf from then on is not.
        var channel = InstanceGate.ChannelFor(Scope, UniqueIdentity("abandoned"));
        using var gate = InstanceGate.TryClaim(channel);
        gate.ShouldNotBeNull();

        using (var abandoned = new NamedPipeClientStream(".", channel, PipeDirection.InOut))
        {
            abandoned.Connect(5_000);
            // Connected, and now gone without ever sending a length.
        }

        InstanceGate.TryHandOff(channel, "after", TimeSpan.FromSeconds(5)).ShouldBeTrue();
        (await Dequeue(gate)).Payload.ShouldBe("after");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_scope_is_rejected(string scope)
        => Should.Throw<ArgumentException>(() => InstanceGate.ChannelFor(scope));

    private static async Task<HandoffRequest> Dequeue(InstanceGate gate)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (gate.TryDequeue(out var request))
            {
                return request;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("No hand-off arrived.");
    }
}
