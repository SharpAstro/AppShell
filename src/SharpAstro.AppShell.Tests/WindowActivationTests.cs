using Shouldly;
using Xunit;

namespace SharpAstro.AppShell.Tests;

/// <summary>
/// Activation has to work for a minimised window and for a maximised one, and the fix for either
/// case on its own breaks the other -- so both directions are pinned here.
///
/// <para>The maximised case is the one that shipped broken: every hand-off restored unconditionally,
/// which un-maximised the window it had just brought forward. Nothing could catch it, because the
/// decision lived inline in two host executables where the only way to observe it was to maximise a
/// real window and double-click a real file. Behind <see cref="IActivatableWindow"/> it is three
/// lines of fake and an assertion about which verbs were called.</para>
/// </summary>
public class WindowActivationTests
{
    [Fact]
    public void A_maximized_window_is_raised_without_being_restored()
    {
        // The shipped bug: Restore un-maximises, so the window came forward at its floating size.
        var window = new FakeWindow { IsMinimized = false };

        window.Activate();

        window.Calls.ShouldBe(["Raise"]);
    }

    [Fact]
    public void A_minimized_window_is_restored_before_it_is_raised()
    {
        // Raise alone would make it the foreground window while still parked off-screen, so the
        // restore is required here -- and the ORDER is the point, not just the pair of calls.
        var window = new FakeWindow { IsMinimized = true };

        window.Activate();

        window.Calls.ShouldBe(["Restore", "Raise"]);
    }

    [Fact]
    public void A_window_that_is_merely_behind_another_one_is_only_raised()
    {
        // Same path as maximised, and worth stating separately: this is the common case, and a
        // restore here would move a normal window that the user had already sized and placed.
        var window = new FakeWindow { IsMinimized = false };

        window.Activate();

        window.Calls.ShouldBe(["Raise"]);
    }

    /// <summary>
    /// Records the verbs in order. <see cref="Restore"/> clears the minimised flag the way a real
    /// window manager would, so a test cannot pass by accident on a fake that stayed minimised.
    /// </summary>
    private sealed class FakeWindow : IActivatableWindow
    {
        public List<string> Calls { get; } = [];

        public bool IsMinimized { get; set; }

        public void Restore()
        {
            Calls.Add("Restore");
            IsMinimized = false;
        }

        public void Raise() => Calls.Add("Raise");
    }
}
