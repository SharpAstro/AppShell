using Shouldly;
using Xunit;

namespace SharpAstro.AppShell.Tests;

/// <summary>
/// The identity resolution (pure, every OS), the argument errors, the Unsupported answer off Windows and the
/// LogOnce guard. Nothing here shows a toast or asks Windows for anything beyond reading this process's own app id.
/// </summary>
public class DesktopNotificationTests
{
    [Fact]
    public void The_asked_for_app_id_wins()
        => DesktopNotification.ResolveIdentity("Asked", "Claimed", "Registered", "Explicit", packaged: true).ShouldBe(("Asked", null));

    [Fact]
    public void The_claimed_id_comes_before_the_registered_one()
        => DesktopNotification.ResolveIdentity(null, "Claimed", "Registered", "Explicit", packaged: true).ShouldBe(("Claimed", null));

    [Fact]
    public void The_registered_id_comes_after_the_claimed_one_and_before_the_explicit_one()
    {
        DesktopNotification.ResolveIdentity(null, null, "Registered", "Explicit", packaged: true).ShouldBe(("Registered", null));
        DesktopNotification.ResolveIdentity(null, " ", "Registered", null, packaged: false).ShouldBe(("Registered", null));
    }

    [Fact]
    public void The_explicit_id_comes_before_the_default_notifier()
        => DesktopNotification.ResolveIdentity(" ", "", null, "Explicit", packaged: true).ShouldBe(("Explicit", null));

    [Fact]
    public void A_packaged_process_with_no_id_uses_the_default_notifier()
        => DesktopNotification.ResolveIdentity(null, null, null, null, packaged: true).ShouldBe((null, null));

    [Fact]
    public void With_nothing_the_reason_names_Claim()
    {
        var (id, reason) = DesktopNotification.ResolveIdentity(null, null, null, null, packaged: false);
        id.ShouldBeNull();
        reason.ShouldNotBeNull().ShouldContain("AppIdentity.Claim");
    }

    private const string AumidKey = @"Software\Classes\AppUserModelId\Some.App";
    private const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Some.App";

    [Fact]
    public void An_asked_for_id_that_nothing_registers_is_refused_and_says_so()
        => DesktopNotification.UnregisteredReason("Some.App", null, null, null, false, new FakeRegistry())
            .ShouldNotBeNull().ShouldContain("nothing registers");

    [Fact]
    public void An_asked_for_id_with_a_registry_key_or_an_earlier_toast_is_not_refused()
    {
        var registry = new FakeRegistry();
        registry.Write(AumidKey, "DisplayName", "Some");
        DesktopNotification.UnregisteredReason("Some.App", null, null, null, false, registry).ShouldBeNull();

        var shown = new FakeRegistry();
        shown.Write(SettingsKey, "Enabled", "1");
        DesktopNotification.UnregisteredReason("Some.App", null, null, null, false, shown).ShouldBeNull();
    }

    [Fact]
    public void The_process_own_ids_a_package_and_no_ask_are_never_refused()
    {
        var none = new FakeRegistry();
        DesktopNotification.UnregisteredReason("Some.App", "Some.App", null, null, false, none).ShouldBeNull();
        DesktopNotification.UnregisteredReason("Some.App", null, "Some.App", null, false, none).ShouldBeNull();
        DesktopNotification.UnregisteredReason("Some.App", null, null, "Some.App", false, none).ShouldBeNull();
        DesktopNotification.UnregisteredReason("Some.App", null, null, null, true, none).ShouldBeNull();
        DesktopNotification.UnregisteredReason(null, null, null, null, false, none).ShouldBeNull();
    }

    [Fact]
    public void A_launch_payload_with_no_scheme_throws()
        => Should.Throw<ArgumentException>(() => DesktopNotification.Show("t", null, new DesktopNotificationOptions { LaunchPayload = "job-1" }));

    [Theory]
    [InlineData("Bad")]
    [InlineData("1abc")]
    [InlineData("https")]
    [InlineData("ms-settings")]
    public void A_launch_payload_with_a_bad_scheme_throws(string scheme)
        => Should.Throw<ArgumentException>(() => DesktopNotification.Show("t", null,
            new DesktopNotificationOptions { LaunchPayload = "x", Scheme = scheme }));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_title_throws(string? title)
        => Should.Throw<ArgumentException>(() => DesktopNotification.Show(title!));

    [Fact]
    public void A_tag_without_a_group_throws()
    {
        Should.Throw<ArgumentException>(() => DesktopNotification.Show("t", null, new DesktopNotificationOptions { Tag = "a" }));
        Should.Throw<ArgumentException>(() => DesktopNotification.Show("t", null, new DesktopNotificationOptions { Tag = " ", Group = "g" }));
        Should.Throw<ArgumentException>(() => DesktopNotification.Show("t", null, new DesktopNotificationOptions { Group = " " }));
    }

    [Theory]
    [InlineData(null, "g")]
    [InlineData("t", null)]
    [InlineData("", "g")]
    [InlineData("t", " ")]
    public void Withdraw_needs_a_tag_and_a_group(string? tag, string? group)
        => Should.Throw<ArgumentException>(() => DesktopNotification.Withdraw(tag!, group!));

    [Fact]
    public void Off_Windows_every_call_is_unsupported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var options = new DesktopNotificationOptions { Tag = "a", Group = "g", Urgency = ToastUrgency.Urgent };
        DesktopNotification.Show("t", "b", options).Status.ShouldBe(DesktopNotificationStatus.Unsupported);
        DesktopNotification.Withdraw("a", "g").Status.ShouldBe(DesktopNotificationStatus.Unsupported);
        DesktopNotification.GetDelivered().Status.ShouldBe(DesktopNotificationStatus.Unsupported);
        DesktopNotification.Probe().Status.ShouldBe(DesktopNotificationStatus.Unsupported);
        AppIdentity.ExplicitAppId().ShouldBeNull();
        AppIdentity.Register("SharpAstro.AppShell.Tests", "Viewer").ShouldBe(AppUserModelRegistration.None);
        LaunchScheme.Register("appshell-tests", "SharpAstro.AppShell.Tests", "Viewer").ShouldBe(LaunchSchemeRegistration.None);
    }

    [Fact]
    public void LogOnce_says_why_once()
    {
        var lines = new List<string>();
        var logged = 0;
        var unavailable = DesktopNotificationResult.NoIdentity("nothing registers it");
        DesktopNotification.LogOnce(lines.Add, _ => unavailable, ref logged);
        DesktopNotification.LogOnce(lines.Add, _ => unavailable, ref logged);
        lines.Count.ShouldBe(1);
        lines[0].ShouldContain("NoIdentity");
        lines[0].ShouldContain("nothing registers it");
    }

    [Fact]
    public void LogOnce_says_nothing_when_toasts_are_available()
    {
        var lines = new List<string>();
        var logged = 0;
        DesktopNotification.LogOnce(lines.Add, _ => DesktopNotificationResult.Succeeded(), ref logged);
        lines.ShouldBeEmpty();
        logged.ShouldBe(0);
    }

    [Fact]
    public void LogOnce_with_a_null_sink_or_logger_throws_at_once()
    {
        Should.Throw<ArgumentNullException>(() => DesktopNotification.LogOnce((Action<string>)null!));
        Should.Throw<ArgumentNullException>(() => DesktopNotification.LogOnce((Microsoft.Extensions.Logging.ILogger)null!));
    }
}
