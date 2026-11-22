using Shouldly;
using Xunit;

namespace SharpAstro.AppShell.Tests;

/// <summary>Parsing, the Light2/Dark1 pick and the failure reasons, all against a fake source: no registry read.</summary>
public class DesktopAccentTests
{
    private sealed class FakeSource(byte[]? bytes, string? reason = null, Exception? throws = null) : IAccentSource
    {
        public (byte[]? Bytes, string? Reason) Read() => throws is null ? (bytes, reason) : throw throws;
    }

    // Entry i is (10i+1, 10i+2, 10i+3) with a nonzero fourth byte, which must be ignored.
    private static byte[] Palette(int length = AccentRamp.PaletteLength)
    {
        var b = new byte[length];
        for (var i = 0; i < length; i++)
        {
            b[i] = (byte)((i / 4) * 10 + (i % 4 + 1));
        }
        return b;
    }

    [Fact]
    public void Parse_reads_eight_opaque_entries_in_order()
    {
        var ramp = AccentRamp.Parse(Palette()).ShouldNotBeNull();
        ramp.Light3.ShouldBe(new AccentColor(1, 2, 3));
        ramp.Light2.ShouldBe(new AccentColor(11, 12, 13));
        ramp.Light1.ShouldBe(new AccentColor(21, 22, 23));
        ramp.Base.ShouldBe(new AccentColor(31, 32, 33));
        ramp.Dark1.ShouldBe(new AccentColor(41, 42, 43));
        ramp.Dark2.ShouldBe(new AccentColor(51, 52, 53));
        ramp.Dark3.ShouldBe(new AccentColor(61, 62, 63));
        ramp.Extra.ShouldBe(new AccentColor(71, 72, 73));
    }

    [Fact]
    public void LogOnce_with_a_null_logger_throws_at_once()
    {
        Should.Throw<ArgumentNullException>(() => DesktopAccent.LogOnce((Microsoft.Extensions.Logging.ILogger)null!));
        Should.Throw<ArgumentNullException>(() => DesktopAccent.LogOnce((Action<string>)null!));
    }

    [Fact]
    public void Parse_ignores_the_fourth_byte()
    {
        var a = Palette();
        var b = Palette();
        for (var i = 3; i < b.Length; i += 4) b[i] = 0xAB;
        AccentRamp.Parse(a).ShouldBe(AccentRamp.Parse(b));
    }

    [Fact]
    public void Parse_of_a_short_value_is_null()
    {
        AccentRamp.Parse(Palette(31)).ShouldBeNull();
        AccentRamp.Parse([]).ShouldBeNull();
    }

    [Fact]
    public void Parse_of_a_longer_value_reads_from_the_front()
    {
        AccentRamp.Parse(Palette(40)).ShouldBe(AccentRamp.Parse(Palette()));
    }

    [Fact]
    public void Hex_and_packed_forms()
    {
        var c = new AccentColor(0x12, 0xAB, 0xEF);
        c.Hex.ShouldBe("#12ABEF");
        c.Rgb.ShouldBe(0x12ABEF);
    }

    [Fact]
    public void Pick_is_Light2_on_dark_chrome_and_Dark1_on_light()
    {
        var ramp = AccentRamp.Parse(Palette()).ShouldNotBeNull();
        ramp.Pick(true).ShouldBe(ramp.Light2);
        ramp.Pick(false).ShouldBe(ramp.Dark1);
    }

    [Fact]
    public void Resolve_a_good_value_gives_a_ramp_and_no_reason()
    {
        var (ramp, reason) = AccentResolver.Resolve(new FakeSource(Palette()));
        ramp.ShouldNotBeNull();
        reason.ShouldBeNull();
    }

    [Fact]
    public void Resolve_a_short_value_gives_null_and_a_reason()
    {
        var (ramp, reason) = AccentResolver.Resolve(new FakeSource(Palette(8)));
        ramp.ShouldBeNull();
        reason.ShouldNotBeNull().ShouldContain("8 bytes");
    }

    [Fact]
    public void Resolve_a_source_with_nothing_gives_null_and_its_reason()
    {
        var (ramp, reason) = AccentResolver.Resolve(new FakeSource(null, "no value"));
        ramp.ShouldBeNull();
        reason.ShouldBe("no value");
        AccentResolver.Resolve(new FakeSource(null)).Reason.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Resolve_a_throwing_source_gives_null_and_a_reason()
    {
        var (ramp, reason) = AccentResolver.Resolve(new FakeSource(null, throws: new InvalidOperationException("boom")));
        ramp.ShouldBeNull();
        reason.ShouldNotBeNull().ShouldContain("boom");
    }
}
