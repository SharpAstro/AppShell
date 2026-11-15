using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Shouldly;
using Xunit;

namespace SharpAstro.AppShell.Tests;

/// <summary>
/// The trace reaches every sink, a failing sink does not stop the others, and a faulted task nobody awaited is
/// reported. CrashTrace is process-wide (so are the events it hooks), so each test installs its own sink and looks
/// only for its own marker. An unhandled exception that terminates the process cannot be raised inside a test run;
/// <see cref="CrashTrace.Write"/> is what that hook calls, so it is tested directly.
/// </summary>
public class CrashTraceTests
{
    [Fact]
    public void Write_reaches_every_installed_sink_with_the_whole_exception()
    {
        var marker = Guid.NewGuid().ToString("N");
        var first = new ConcurrentQueue<string>();
        var second = new ConcurrentQueue<string>();
        CrashTrace.Install(first.Enqueue);
        CrashTrace.Install(second.Enqueue);

        CrashTrace.Write("test failure", new InvalidOperationException(marker, new TimeoutException("inner")));

        foreach (var sink in new[] { first, second })
        {
            var text = sink.Single(t => t.Contains(marker));
            text.ShouldContain("test failure:");
            text.ShouldContain(nameof(InvalidOperationException));
            text.ShouldContain("inner");
        }
    }

    [Fact]
    public void A_sink_that_throws_does_not_stop_the_next_one()
    {
        var marker = Guid.NewGuid().ToString("N");
        var after = new ConcurrentQueue<string>();
        CrashTrace.Install(_ => throw new IOException("disk full"));
        CrashTrace.Install(after.Enqueue);

        Should.NotThrow(() => CrashTrace.Write("test failure", marker));

        after.ShouldContain(t => t.Contains(marker));
    }

    [Fact]
    public void A_faulted_task_nobody_awaited_is_reported()
    {
        var marker = Guid.NewGuid().ToString("N");
        var seen = new ConcurrentQueue<string>();
        CrashTrace.Install(seen.Enqueue);

        // The event is raised by the finalizer of a faulted task that was never observed, so the task must be
        // unreachable: created in a method of its own, then collected.
        for (var attempt = 0; attempt < 10 && !seen.Any(t => t.Contains(marker)); attempt++)
        {
            FaultAndForget(marker);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        seen.ShouldContain(t => t.Contains(marker) && t.Contains("unobserved task exception"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FaultAndForget(string marker)
    {
        var task = Task.Run(() => throw new InvalidOperationException(marker));
        ((IAsyncResult)task).AsyncWaitHandle.WaitOne(); // let it fault, without observing the exception
    }
}
