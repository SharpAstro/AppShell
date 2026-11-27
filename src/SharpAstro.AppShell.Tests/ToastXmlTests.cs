using System.Xml;
using Shouldly;
using Xunit;

namespace SharpAstro.AppShell.Tests;

/// <summary>The pure toast XML builder: it runs on every OS and shows nothing.</summary>
public class ToastXmlTests
{
    private static XmlDocument Load(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc;
    }

    [Fact]
    public void Normal_has_a_title_and_a_body_and_neither_scenario_nor_actions()
    {
        var doc = Load(ToastXml.Build("Done", "All 3 files", ToastUrgency.Normal));
        var texts = doc.SelectNodes("/toast/visual/binding/text")!;
        texts.Count.ShouldBe(2);
        texts[0]!.InnerText.ShouldBe("Done");
        texts[1]!.InnerText.ShouldBe("All 3 files");
        doc.SelectSingleNode("/toast/visual/binding")!.Attributes!["template"]!.Value.ShouldBe("ToastGeneric");
        doc.DocumentElement!.HasAttribute("scenario").ShouldBeFalse();
        doc.SelectSingleNode("/toast/actions").ShouldBeNull();
    }

    [Fact]
    public void A_launch_link_sets_protocol_activation_and_is_escaped()
    {
        var link = "viewer:a%20b&c\"<d>'";
        var doc = Load(ToastXml.Build("Done", null, ToastUrgency.Normal, link));
        doc.DocumentElement!.GetAttribute("activationType").ShouldBe("protocol");
        doc.DocumentElement.GetAttribute("launch").ShouldBe(link);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_launch_link_leaves_the_toast_without_activation(string? link)
    {
        var doc = Load(ToastXml.Build("Done", null, ToastUrgency.Normal, link));
        doc.DocumentElement!.HasAttribute("launch").ShouldBeFalse();
        doc.DocumentElement.HasAttribute("activationType").ShouldBeFalse();
    }

    [Fact]
    public void A_launch_link_goes_with_a_reminder_and_its_dismiss_action()
    {
        var doc = Load(ToastXml.Build("T", "B", ToastUrgency.Reminder, "viewer:x"));
        doc.DocumentElement!.GetAttribute("launch").ShouldBe("viewer:x");
        doc.DocumentElement.GetAttribute("scenario").ShouldBe("reminder");
        doc.SelectSingleNode("/toast/actions/action")!.Attributes!["arguments"]!.Value.ShouldBe("dismiss");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n ")]
    public void A_missing_or_blank_body_leaves_one_line(string? body)
        => Load(ToastXml.Build("Done", body, ToastUrgency.Normal)).SelectNodes("/toast/visual/binding/text")!.Count.ShouldBe(1);

    [Theory]
    [InlineData(ToastUrgency.Reminder, "reminder")]
    [InlineData(ToastUrgency.Urgent, "urgent")]
    public void Reminder_and_urgent_set_the_scenario_and_carry_the_system_dismiss_action(ToastUrgency urgency, string scenario)
    {
        var doc = Load(ToastXml.Build("T", "B", urgency));
        doc.DocumentElement!.GetAttribute("scenario").ShouldBe(scenario);
        var action = doc.SelectSingleNode("/toast/actions/action")!;
        action.Attributes!["activationType"]!.Value.ShouldBe("system");
        action.Attributes["arguments"]!.Value.ShouldBe("dismiss");
        action.Attributes["content"]!.Value.ShouldBe(string.Empty);
    }

    [Fact]
    public void Markup_characters_are_escaped_and_come_back_as_text()
    {
        const string hostile = "<b>&\"'</b> ]]> <!-- x -->";
        var doc = Load(ToastXml.Build(hostile, hostile, ToastUrgency.Normal));
        var texts = doc.SelectNodes("/toast/visual/binding/text")!;
        texts[0]!.InnerText.ShouldBe(hostile);
        texts[1]!.InnerText.ShouldBe(hostile);
    }

    [Fact]
    public void Escape_covers_the_five_characters()
        => ToastXml.Escape("&<>\"'").ShouldBe("&amp;&lt;&gt;&quot;&apos;");

    [Fact]
    public void A_whitespace_run_becomes_one_space()
        => ToastXml.Cut("  a \t\r\n\n b   c  ", 50).ShouldBe("a b c");

    [Fact]
    public void Control_characters_are_dropped()
    {
        ToastXml.Cut("ab\u001b[31mc\u0000d\u0008", 50).ShouldBe("ab[31mcd");
        Should.NotThrow(() => Load(ToastXml.Build("x\u001b[0m", "y\u0007￾", ToastUrgency.Normal)));
    }

    [Fact]
    public void Lone_surrogates_are_dropped_and_pairs_kept()
    {
        ToastXml.Cut("a\ud83dz", 50).ShouldBe("az");
        ToastXml.Cut("a\ude00z", 50).ShouldBe("az");
        ToastXml.Cut("a😀z", 50).ShouldBe("a😀z");
        Should.NotThrow(() => Load(ToastXml.Build("\ud83d", "\ude00\ud83d", ToastUrgency.Normal)));
    }

    [Fact]
    public void Text_at_the_limit_is_kept_and_one_over_is_cut_with_an_ellipsis()
    {
        var atLimit = new string('a', DesktopNotification.TitleLimit);
        ToastXml.Cut(atLimit, DesktopNotification.TitleLimit).ShouldBe(atLimit);
        var cut = ToastXml.Cut(atLimit + "a", DesktopNotification.TitleLimit);
        cut.Length.ShouldBe(DesktopNotification.TitleLimit);
        cut.ShouldBe(new string('a', DesktopNotification.TitleLimit - 1) + "…");
    }

    [Fact]
    public void The_cut_never_splits_a_surrogate_pair()
    {
        // The pair would straddle the cut: 58 letters, then a pair at characters 59 and 60, then more.
        var text = new string('a', 58) + "😀" + "tail";
        var cut = ToastXml.Cut(text, 60);
        cut.ShouldBe(new string('a', 58) + "…");
        Should.NotThrow(() => Load(ToastXml.Build(text, text, ToastUrgency.Normal)));
    }

    [Fact]
    public void Build_cuts_the_title_and_the_body_to_the_public_limits()
    {
        var doc = Load(ToastXml.Build(new string('t', 500), new string('b', 500), ToastUrgency.Normal));
        var texts = doc.SelectNodes("/toast/visual/binding/text")!;
        texts[0]!.InnerText.Length.ShouldBe(DesktopNotification.TitleLimit);
        texts[1]!.InnerText.Length.ShouldBe(DesktopNotification.BodyLimit);
        texts[0]!.InnerText.ShouldEndWith("…");
    }

    [Fact]
    public void A_short_tag_is_unchanged()
    {
        ToastXml.TagOf("build").ShouldBe("build");
        var atLimit = new string('x', DesktopNotification.TagLimit);
        ToastXml.TagOf(atLimit).ShouldBe(atLimit);
    }

    [Fact]
    public void A_long_tag_maps_to_a_stable_short_form()
    {
        var longTag = new string('x', DesktopNotification.TagLimit + 1);
        var mapped = ToastXml.TagOf(longTag);
        mapped.Length.ShouldBeLessThanOrEqualTo(DesktopNotification.TagLimit);
        mapped.ShouldStartWith("s:");
        mapped.Length.ShouldBe(42);
        ToastXml.TagOf(longTag).ShouldBe(mapped);
        ToastXml.TagOf(longTag + "y").ShouldNotBe(mapped);
    }
}
