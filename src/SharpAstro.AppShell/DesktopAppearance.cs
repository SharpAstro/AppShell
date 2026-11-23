using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace SharpAstro.AppShell;

/// <summary>The desktop's app mode: whether apps are meant to draw light or dark.</summary>
public enum AppMode
{
    /// <summary>The desktop publishes no answer here; see <see cref="DesktopAppearance.Reason"/>.</summary>
    Unknown,

    /// <summary>The desktop asks apps to be light.</summary>
    Light,

    /// <summary>The desktop asks apps to be dark.</summary>
    Dark,
}

/// <summary>Where the raw AppsUseLightTheme value comes from: the value, or the reason there is none. A test fakes it.</summary>
internal interface IAppModeSource
{
    (object? Value, string? Reason) Read();
}

/// <summary>The Windows registry's AppsUseLightTheme; a reason, never an exception, elsewhere or when it cannot be read.</summary>
internal sealed class RegistryAppModeSource : IAppModeSource
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public (object? Value, string? Reason) Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, "the desktop publishes no light/dark mode here (not Windows)");
        }
        try
        {
            return ReadWindows();
        }
        catch (Exception ex)
        {
            return (null, $"the app mode could not be read ({ex.GetType().Name}: {ex.Message})");
        }
    }

    [SupportedOSPlatform("windows")]
    private static (object? Value, string? Reason) ReadWindows()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue("AppsUseLightTheme") is { } value
            ? (value, null)
            : (null, @"HKCU Themes\Personalize\AppsUseLightTheme has no value");
    }
}

/// <summary>Turns a source's answer into a mode or a reason. No static constructor, so a test calling it reads no registry.</summary>
internal static class AppModeResolver
{
    /// <summary>1 is light, 0 is dark; Unknown with a reason for a missing value, any other type or number, or a throwing source.</summary>
    internal static (AppMode Mode, string? Reason) Resolve(IAppModeSource source)
    {
        try
        {
            var (value, reason) = source.Read();
            if (value is null)
            {
                return (AppMode.Unknown, reason ?? "no app mode value");
            }
            return value is int n
                ? n switch
                {
                    1 => (AppMode.Light, null),
                    0 => (AppMode.Dark, null),
                    _ => (AppMode.Unknown, $"the app mode value is {n}, expected 0 or 1"),
                }
                : (AppMode.Unknown, $"the app mode value is a {value.GetType().Name}, expected a DWORD");
        }
        catch (Exception ex)
        {
            return (AppMode.Unknown, $"the app mode could not be read ({ex.GetType().Name}: {ex.Message})");
        }
    }
}

/// <summary>
/// The desktop's light/dark app mode, opt-in: nothing here applies it. An app follows the desktop by default and may
/// add its own override on top. Read once, in the static initialiser, on first use and on whatever thread touches it first (touch it at startup,
/// e.g. with <see cref="LogOnce(Action{string})"/>, so that is not the render thread), so a change made in Windows
/// settings during the session shows at the next launch. <see cref="Mode"/> is
/// <see cref="AppMode.Unknown"/> where none is published and <see cref="Reason"/> says why. Pair with
/// <c>DesktopAccent.For(darkChrome: DesktopAppearance.IsDark)</c> to pick the matching accent step.
/// </summary>
public static class DesktopAppearance
{
    private static readonly (AppMode Mode, string? Reason) Resolved = AppModeResolver.Resolve(new RegistryAppModeSource());

    private static int _logged;

    /// <summary>The desktop's app mode, read once on first use.</summary>
    public static AppMode Mode => Resolved.Mode;

    /// <summary>Why the mode is <see cref="AppMode.Unknown"/>; null when it is known.</summary>
    public static string? Reason => Resolved.Reason;

    /// <summary>True only when the desktop asks for dark; an unknown mode is not dark (see <see cref="Reason"/>).</summary>
    public static bool IsDark => Resolved.Mode == AppMode.Dark;

    /// <summary>Says once, why the app mode is unknown; nothing when it is known.</summary>
    /// <param name="log">Where the line goes (an app's own log).</param>
    public static void LogOnce(Action<string> log)
    {
        LogSink.Require(log);
        if (Resolved.Mode == AppMode.Unknown && Interlocked.Exchange(ref _logged, 1) == 0)
        {
            log($"no desktop light/dark mode: {Resolved.Reason}");
        }
    }

    /// <summary>Says once, why the app mode is unknown; nothing when it is known.</summary>
    /// <param name="logger">The logger the line goes to, at information level.</param>
    public static void LogOnce(ILogger logger)
    {
        LogOnce(LogSink.From(logger));
    }
}
