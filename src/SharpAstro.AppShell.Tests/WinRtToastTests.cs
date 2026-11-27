using Shouldly;
using Xunit;

namespace SharpAstro.AppShell.Tests;

/// <summary>
/// The WinRT calls, proved in milliseconds with no toast shown: every XML the builder makes loads into a real
/// XmlDocument and becomes a ToastNotification whose tag and group read back, and an app id nothing registers is
/// refused when the notifier's setting is read, named as a missing registration or, where Windows refuses its own
/// reference app id too (a remote desktop session), as a session without toast delivery. No registry is written and the Start menu is not touched. Windows only.
/// </summary>
public class WinRtToastTests
{
    public static TheoryData<ToastUrgency> Urgencies => new() { ToastUrgency.Normal, ToastUrgency.Reminder, ToastUrgency.Urgent };

    [Theory]
    [MemberData(nameof(Urgencies))]
    public void Every_urgency_builds_a_notification_whose_tag_and_group_read_back(ToastUrgency urgency)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var xml = ToastXml.Build("Build finished", "3 files", urgency);
        var (result, tag, group) = WinRtToast.BuildAndReadBack(xml, "tag-1", "group-1");
        result.Status.ShouldBe(DesktopNotificationStatus.Succeeded, result.Reason);
        tag.ShouldBe("tag-1");
        group.ShouldBe("group-1");
    }

    [Fact]
    public void A_toast_with_a_launch_link_loads_and_reads_back()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var link = LaunchScheme.Link("viewer", "job & <1> \"x\" Grüße 😀");
        var xml = ToastXml.Build("Build finished", "3 files", ToastUrgency.Normal, link);
        var (result, tag, group) = WinRtToast.BuildAndReadBack(xml, "tag-1", "group-1");
        result.Status.ShouldBe(DesktopNotificationStatus.Succeeded, result.Reason);
        tag.ShouldBe("tag-1");
        group.ShouldBe("group-1");
    }

    [Fact]
    public void Hostile_text_and_a_long_tag_still_build_and_read_back()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var hostile = "<b>&\"'</b>\u001b[31m \ud83d lone 😀 " + new string('x', 400);
        var xml = ToastXml.Build(hostile, hostile, ToastUrgency.Urgent);
        var tag = ToastXml.TagOf(new string('t', 200));
        var group = ToastXml.TagOf(new string('g', 200));
        var (result, readTag, readGroup) = WinRtToast.BuildAndReadBack(xml, tag, group);
        result.Status.ShouldBe(DesktopNotificationStatus.Succeeded, result.Reason);
        readTag.ShouldBe(tag);
        readGroup.ShouldBe(group);
    }

    [Fact]
    public void An_unmapped_tag_over_the_limit_fails_as_a_result_and_not_as_an_exception()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var xml = ToastXml.Build("t", null, ToastUrgency.Normal);
        var (result, _, _) = WinRtToast.BuildAndReadBack(xml, new string('t', 200), "g");
        result.Status.ShouldBe(DesktopNotificationStatus.Failed);
        result.Reason.ShouldNotBeNull().ShouldContain("HRESULT 0x");
    }

    [Fact]
    public void Malformed_xml_fails_as_a_result_naming_the_call()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (result, _, _) = WinRtToast.BuildAndReadBack("<toast><unclosed>", "t", "g");
        result.Status.ShouldBe(DesktopNotificationStatus.Failed);
        result.Reason.ShouldNotBeNull().ShouldContain("IXmlDocumentIO.LoadXml failed: HRESULT 0x");
    }

    [Fact]
    public void An_app_id_nothing_registers_is_refused_when_its_setting_is_read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // In an interactive session Windows knows the reference app id, so the refusal names the missing
        // registration; in a remote desktop session it refuses every app id and the refusal names the session.
        var reference = WinRtToast.ReadSetting(WinRtToast.ReferenceAppId);
        var result = WinRtToast.ReadSetting("SharpAstro.AppShell.Tests." + Guid.NewGuid().ToString("N"));
        result.Status.ShouldBe(
            reference.Status == DesktopNotificationStatus.SessionUnavailable
                ? DesktopNotificationStatus.SessionUnavailable
                : DesktopNotificationStatus.NoIdentity,
            result.Reason);
        result.Reason.ShouldNotBeNull().ShouldContain("0x80070490");
    }

    [Fact]
    public void Not_found_while_the_reference_app_id_is_known_names_the_missing_registration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = WinRtToast.NotFound("Some.App", "IToastNotifier.get_Setting", WinRtToast.ErrorNotFound, referenceAlsoNotFound: false);

        result.Status.ShouldBe(DesktopNotificationStatus.NoIdentity);
        result.Reason.ShouldNotBeNull().ShouldContain("'Some.App' is registered nowhere");
        result.Reason.ShouldContain("0x80070490");
    }

    [Fact]
    public void Not_found_for_the_reference_app_id_too_is_a_session_without_toast_delivery()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = WinRtToast.NotFound("Some.App", "IToastNotifier.get_Setting", WinRtToast.ErrorNotFound, referenceAlsoNotFound: true);

        result.Status.ShouldBe(DesktopNotificationStatus.SessionUnavailable);
        result.IsSuccess.ShouldBeFalse();
        result.Reason.ShouldNotBeNull().ShouldStartWith("toasts unavailable in this session (0x80070490, ERROR_NOT_FOUND), RDP or no interactive desktop");
        result.Reason.ShouldContain("'Some.App'");
    }
}
