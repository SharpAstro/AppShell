using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace SharpAstro.AppShell;

/// <summary>An opaque sRGB colour picked from the desktop's accent ramp.</summary>
/// <param name="R">Red, 0 to 255.</param>
/// <param name="G">Green, 0 to 255.</param>
/// <param name="B">Blue, 0 to 255.</param>
public readonly record struct AccentColor(byte R, byte G, byte B)
{
    /// <summary>The colour packed as 0xRRGGBB.</summary>
    public int Rgb => (R << 16) | (G << 8) | B;

    /// <summary>The colour as <c>#RRGGBB</c>.</summary>
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// The desktop's accent ramp as Windows publishes it (HKCU Explorer\Accent\AccentPalette): eight RGBA entries, Light3
/// (lightest) to Dark3 (darkest), then Extra. Pure parsing and picking, no OS calls, so it is AOT-clean and a test
/// can feed it bytes. A colour is only ever picked from the ramp, never lightened or darkened here.
/// </summary>
/// <param name="Light3">The lightest step.</param>
/// <param name="Light2">A light step; what <see cref="DesktopAccent.For"/> picks on dark chrome.</param>
/// <param name="Light1">A slightly light step.</param>
/// <param name="Base">The accent itself.</param>
/// <param name="Dark1">A slightly dark step; what <see cref="DesktopAccent.For"/> picks on light chrome.</param>
/// <param name="Dark2">A dark step.</param>
/// <param name="Dark3">The darkest step.</param>
/// <param name="Extra">The eighth entry Windows publishes.</param>
public sealed record AccentRamp(
    AccentColor Light3,
    AccentColor Light2,
    AccentColor Light1,
    AccentColor Base,
    AccentColor Dark1,
    AccentColor Dark2,
    AccentColor Dark3,
    AccentColor Extra)
{
    /// <summary>Bytes in the AccentPalette value: eight entries of four bytes.</summary>
    public const int PaletteLength = 32;

    /// <summary>Parses the value; null when it is shorter than <see cref="PaletteLength"/> (longer is read from the front).</summary>
    /// <param name="palette">The raw AccentPalette bytes.</param>
    public static AccentRamp? Parse(ReadOnlySpan<byte> palette)
    {
        if (palette.Length < PaletteLength)
        {
            return null;
        }
        return new AccentRamp(At(palette, 0), At(palette, 1), At(palette, 2), At(palette, 3), At(palette, 4), At(palette, 5), At(palette, 6), At(palette, 7));
    }

    // The fourth byte of an entry is unused by Windows (0), so the colour is opaque and it is ignored.
    private static AccentColor At(ReadOnlySpan<byte> palette, int i) => new(palette[i * 4], palette[i * 4 + 1], palette[i * 4 + 2]);

    /// <summary>Light2 on dark chrome, Dark1 on light chrome: the ramp's own steps that read against each.</summary>
    /// <param name="darkChrome">True when the window chrome is dark.</param>
    public AccentColor Pick(bool darkChrome) => darkChrome ? Light2 : Dark1;
}

/// <summary>Where the raw AccentPalette bytes come from: the bytes, or the reason there are none. A test fakes it.</summary>
internal interface IAccentSource
{
    (byte[]? Bytes, string? Reason) Read();
}

/// <summary>The Windows registry's AccentPalette; a reason, never an exception, elsewhere or when it cannot be read.</summary>
internal sealed class RegistryAccentSource : IAccentSource
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    public (byte[]? Bytes, string? Reason) Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, "the desktop publishes no accent colour here (not Windows)");
        }
        try
        {
            return ReadWindows();
        }
        catch (Exception ex)
        {
            return (null, $"the accent could not be read ({ex.GetType().Name}: {ex.Message})");
        }
    }

    [SupportedOSPlatform("windows")]
    private static (byte[]? Bytes, string? Reason) ReadWindows()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue("AccentPalette") is byte[] bytes
            ? (bytes, null)
            : (null, @"HKCU Explorer\Accent\AccentPalette has no value");
    }
}

/// <summary>Turns a source's answer into a ramp or a reason. No static constructor, so a test calling it reads no registry.</summary>
internal static class AccentResolver
{
    /// <summary>Parses what a source gives; null with a reason when it gives none, a short value, or throws.</summary>
    internal static (AccentRamp? Ramp, string? Reason) Resolve(IAccentSource source)
    {
        try
        {
            var (bytes, reason) = source.Read();
            if (bytes is null)
            {
                return (null, reason ?? "no accent value");
            }
            return AccentRamp.Parse(bytes) is { } ramp
                ? (ramp, null)
                : (null, $"the accent value is {bytes.Length} bytes, expected {AccentRamp.PaletteLength}");
        }
        catch (Exception ex)
        {
            return (null, $"the accent could not be read ({ex.GetType().Name}: {ex.Message})");
        }
    }
}

/// <summary>
/// The desktop's accent, opt-in: an app chooses its own accent and nothing here applies this by default. The ramp is
/// read once, in the static initialiser (never on the render thread), so a change made in Windows settings during
/// the session shows at the next launch. <see cref="Ramp"/> is null where none is published and
/// <see cref="Reason"/> says why. A colour is picked from the ramp, never lightened or darkened here.
/// </summary>
public static class DesktopAccent
{
    private static readonly (AccentRamp? Ramp, string? Reason) Resolved = AccentResolver.Resolve(new RegistryAccentSource());

    private static int _logged;

    /// <summary>The desktop's accent ramp, read once at startup; null when none is published.</summary>
    public static AccentRamp? Ramp => Resolved.Ramp;

    /// <summary>Why there is no <see cref="Ramp"/>; null when there is one.</summary>
    public static string? Reason => Resolved.Reason;

    /// <summary>The ramp's Light2 on dark chrome and Dark1 on light chrome; null when there is no ramp (see <see cref="Reason"/>).</summary>
    /// <param name="darkChrome">True when the window chrome is dark.</param>
    public static AccentColor? For(bool darkChrome) => Resolved.Ramp?.Pick(darkChrome);

    /// <summary>Says once, at startup, why there is no desktop accent; nothing when there is one.</summary>
    /// <param name="log">Where the line goes (an app's own log).</param>
    public static void LogOnce(Action<string> log)
    {
        if (Resolved.Ramp is null && Interlocked.Exchange(ref _logged, 1) == 0)
        {
            log($"no desktop accent colour: {Resolved.Reason}");
        }
    }

    /// <summary>Says once, at startup, why there is no desktop accent; nothing when there is one.</summary>
    /// <param name="logger">The logger the line goes to, at information level.</param>
    public static void LogOnce(ILogger logger) =>
        LogOnce(message => logger.LogInformation("{Message}", message));
}
