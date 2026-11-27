using Shouldly;
using Xunit;

namespace SharpAstro.AppShell.Tests;

/// <summary>
/// The WinRT calls, proved in milliseconds with no toast shown: every XML the builder makes loads into a real
/// XmlDocument and becomes a ToastNotification whose tag and group read back, and the notifier's setting read lets
/// an app id that has never shown a toast through while a setting that turns toasts off is refused by name. No
/// registry is written and the Start menu is not touched. Windows only.
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
    public void An_app_id_that_has_never_shown_a_toast_is_not_refused_when_its_setting_is_read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A fresh app id has no notification settings yet, so Windows answers the setting read with ERROR_NOT_FOUND
        // in a console and a remote desktop session alike. Reading writes nothing.
        var result = WinRtToast.ReadSetting("SharpAstro.AppShell.Tests." + Guid.NewGuid().ToString("N"));

        result.Status.ShouldBe(DesktopNotificationStatus.Succeeded, result.Reason);
    }

    [Fact]
    public void Not_found_from_the_setting_read_lets_the_toast_be_shown()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WinRtToast.SettingRefusal(WinRtToast.ErrorNotFound, setting: 0).ShouldBeNull();
    }

    [Fact]
    public void An_enabled_setting_lets_the_toast_be_shown()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WinRtToast.SettingRefusal(hr: 0, setting: 0).ShouldBeNull();
    }

    [Theory]
    [InlineData(1, "DisabledForApplication")]
    [InlineData(2, "DisabledForUser")]
    [InlineData(3, "DisabledByGroupPolicy")]
    [InlineData(4, "DisabledByManifest")]
    [InlineData(9, "setting 9")]
    public void A_setting_that_turns_toasts_off_is_refused_by_name(int setting, string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = WinRtToast.SettingRefusal(hr: 0, setting).ShouldNotBeNull();

        result.Status.ShouldBe(DesktopNotificationStatus.Disabled);
        result.Reason.ShouldNotBeNull().ShouldContain(name);
    }

    [Fact]
    public void Another_failure_of_the_setting_read_fails_naming_the_call()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = WinRtToast.SettingRefusal(unchecked((int)0x80004005), setting: 0).ShouldNotBeNull();

        result.Status.ShouldBe(DesktopNotificationStatus.Failed);
        result.Reason.ShouldBe("IToastNotifier.get_Setting failed: HRESULT 0x80004005");
    }
}
