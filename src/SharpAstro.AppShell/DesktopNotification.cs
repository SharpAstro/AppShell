using Microsoft.Extensions.Logging;

namespace SharpAstro.AppShell;

/// <summary>How urgently a toast asks for the person's attention.</summary>
public enum ToastUrgency
{
    /// <summary>An ordinary toast: it shows, then goes to the notification centre by itself.</summary>
    Normal,

    /// <summary>The toast stays on screen until the person dismisses it (<c>scenario="reminder"</c>), with the
    /// system's own Dismiss button. For something that must not be missed, not for every message.</summary>
    Reminder,

    /// <summary>As <see cref="Reminder"/>, and Windows 11 can let it through Do Not Disturb when the person has
    /// allowed that for the app (<c>scenario="urgent"</c>). Keep it for the most serious level: an app that
    /// cries wolf gets its permission taken away.</summary>
    Urgent,
}

/// <summary>How a <see cref="DesktopNotification"/> call ended.</summary>
public enum DesktopNotificationStatus
{
    /// <summary>The call did what it was asked to.</summary>
    Succeeded,

    /// <summary>Windows has no name for the app to show the toast under: <see cref="AppIdentity.Claim(string, string)"/>
    /// and <see cref="AppIdentity.Register(string, string, string?)"/> were not called (or the shell could not name the
    /// app from them) and no app id was given, or Windows made no notifier for the app id (ERROR_NOT_FOUND,
    /// 0x80070490).</summary>
    NoIdentity,

    /// <summary>Windows is set not to show this app's toasts (turned off for the app or the person, by group
    /// policy or by the manifest); the reason names which.</summary>
    Disabled,

    /// <summary>Not Windows: nothing was done.</summary>
    Unsupported,

    /// <summary>A call to the system failed; the reason names the call and its HRESULT.</summary>
    Failed,
}

/// <summary>A toast still in the notification centre.</summary>
/// <param name="Tag">Its tag as Windows holds it (a tag longer than <see cref="DesktopNotification.TagLimit"/>
/// characters is held as a short stable form of it).</param>
/// <param name="Group">Its group, likewise.</param>
public sealed record DeliveredToast(string Tag, string Group);

/// <summary>
/// What a <see cref="DesktopNotification"/> call did. An OS failure is a result, never an exception.
/// </summary>
public sealed record DesktopNotificationResult
{
    private static readonly IReadOnlyList<DeliveredToast> None = [];

    private DesktopNotificationResult(DesktopNotificationStatus status, string? reason, IReadOnlyList<DeliveredToast> toasts)
    {
        Status = status;
        Reason = reason;
        Toasts = toasts;
    }

    /// <summary>The outcome.</summary>
    public DesktopNotificationStatus Status { get; }

    /// <summary>Why the call did not succeed, naming the call and its HRESULT where one failed; null on success.</summary>
    public string? Reason { get; }

    /// <summary>The toasts <see cref="DesktopNotification.GetDelivered(string?)"/> found; empty for every other call.</summary>
    public IReadOnlyList<DeliveredToast> Toasts { get; }

    /// <summary>True when <see cref="Status"/> is <see cref="DesktopNotificationStatus.Succeeded"/>.</summary>
    public bool IsSuccess => Status == DesktopNotificationStatus.Succeeded;

    internal static DesktopNotificationResult Succeeded(IReadOnlyList<DeliveredToast>? toasts = null) =>
        new(DesktopNotificationStatus.Succeeded, null, toasts ?? None);

    internal static DesktopNotificationResult NoIdentity(string reason) => new(DesktopNotificationStatus.NoIdentity, reason, None);

    internal static DesktopNotificationResult Disabled(string reason) => new(DesktopNotificationStatus.Disabled, reason, None);

    internal static DesktopNotificationResult Failed(string reason) => new(DesktopNotificationStatus.Failed, reason, None);

    internal static DesktopNotificationResult Unsupported() =>
        new(DesktopNotificationStatus.Unsupported, "desktop notifications are only implemented on Windows", None);
}

/// <summary>What a toast is shown with. Every property is optional.</summary>
public sealed record DesktopNotificationOptions
{
    /// <summary>How urgently it asks for attention; <see cref="ToastUrgency.Normal"/> by default.</summary>
    public ToastUrgency Urgency { get; init; } = ToastUrgency.Normal;

    /// <summary>With <see cref="Group"/>, what a later toast replaces and <see cref="DesktopNotification.Withdraw"/>
    /// names. A tag longer than <see cref="DesktopNotification.TagLimit"/> characters is mapped to a short stable
    /// form. Needs a <see cref="Group"/>.</summary>
    public string? Tag { get; init; }

    /// <summary>The group the tag is in, so one tag can stand for one thing in each group. Longer than
    /// <see cref="DesktopNotification.TagLimit"/> characters is mapped as a tag is.</summary>
    public string? Group { get; init; }

    /// <summary>The app id to show the toast under, in place of the one <see cref="AppIdentity.Claim(string, string)"/>
    /// gave the process. It must be one that a Start menu shortcut or a registry key registers.</summary>
    public string? AppId { get; init; }

    /// <summary>What a click on the toast hands to the app: an id to look up. With it the toast carries
    /// <c>activationType="protocol"</c> and a <see cref="LaunchScheme.Link"/> of it, which starts the app (or reaches the
    /// running instance through <see cref="InstanceGate"/>). Needs a scheme: <see cref="LaunchScheme"/> here, or the one
    /// <see cref="LaunchScheme.Register(string, string, string)"/> registered. Without it the toast carries no launch.
    /// Untrusted input when it comes back: see <see cref="LaunchScheme"/>.</summary>
    public string? LaunchPayload { get; init; }

    /// <summary>The scheme of the launch link, in place of the one <see cref="LaunchScheme.Register(string, string, string)"/>
    /// registered in this process.</summary>
    public string? Scheme { get; init; }
}

/// <summary>
/// Desktop notifications: OS toasts shown, replaced and withdrawn in process, managed and AOT-safe. On Windows the
/// WinRT toast API is called by vtable (a port of pilot's <c>WinRtToastRunner</c>, SharpAstro/pilot#1378), with no
/// PowerShell, no .NET Framework, no CsWinRT, no <c>-windows</c> target framework and no helper DLL.
///
/// <para><b>The toast's name and icon come from the app id.</b> Windows shows a toast under an app, so it needs an
/// app id that something registers. Call <see cref="AppIdentity.Claim(string, string)"/> at startup: the id it gave
/// the process, with its Start menu shortcut, is what a toast is shown under. The identity is resolved in this
/// order: <see cref="DesktopNotificationOptions.AppId"/>, the id of a successful Claim, the id of an
/// <see cref="AppIdentity.Register(string, string, string?)"/> (for a process with no shortcut), the process's own explicit
/// app id, then (for a packaged process) the default notifier, whose manifest names the app. With none of these a
/// call returns <see cref="DesktopNotificationStatus.NoIdentity"/>.</para>
///
/// <para><b>Nothing throws for the system.</b> Every call returns a <see cref="DesktopNotificationResult"/>: a status
/// and a reason that names the failing call and its HRESULT. Only a wrong argument (a blank title, a tag without a
/// group) throws <see cref="ArgumentException"/>, as <see cref="AppIdentity.Claim(string, string)"/> does for a blank
/// app id. Every call returns <see cref="DesktopNotificationStatus.Unsupported"/> off Windows for now.</para>
///
/// <para>The calls run synchronously on the caller's thread (a few milliseconds), initialising the WinRT apartment
/// as the multithreaded one when the thread has none and leaving it as it was.</para>
/// </summary>
public static class DesktopNotification
{
    /// <summary>The longest title, in characters, a toast shows before an ellipsis.</summary>
    public const int TitleLimit = 60;

    /// <summary>The longest body, in characters, a toast shows before an ellipsis.</summary>
    public const int BodyLimit = 200;

    /// <summary>The longest tag or group Windows takes; a longer one is mapped to a short stable form.</summary>
    public const int TagLimit = 64;

    private static int _logged;

    /// <summary>
    /// Shows a toast, replacing the one of the same tag and group when there is one. Text is flattened to single
    /// spaces, stripped of what XML cannot hold and cut to <see cref="TitleLimit"/> and <see cref="BodyLimit"/>
    /// characters with an ellipsis.
    /// </summary>
    /// <param name="title">The first line; not blank.</param>
    /// <param name="body">The second line, or null for none.</param>
    /// <param name="options">Urgency, tag, group and app id; null for the defaults.</param>
    /// <returns>The outcome; never throws for the system.</returns>
    /// <exception cref="ArgumentException">The title is blank, or a tag is given without a group.</exception>
    public static DesktopNotificationResult Show(string title, string? body = null, DesktopNotificationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        options ??= new DesktopNotificationOptions();
        if (options.Tag is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Tag, nameof(options));
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Group, nameof(options));
        }
        else if (options.Group is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Group, nameof(options));
        }

        string? link = null;
        if (options.LaunchPayload is { } payload)
        {
            var scheme = options.Scheme ?? LaunchScheme.RegisteredScheme
                ?? throw new ArgumentException("a launch payload needs a scheme: register one with LaunchScheme.Register or name it in the options", nameof(options));
            link = LaunchScheme.Link(scheme, payload);
        }
        else if (options.Scheme is not null)
        {
            LaunchScheme.ThrowIfBad(options.Scheme, nameof(options));
        }

        if (!OperatingSystem.IsWindows())
        {
            return DesktopNotificationResult.Unsupported();
        }

        var identity = CurrentIdentity(options.AppId);
        return identity.Reason is { } why
            ? DesktopNotificationResult.NoIdentity(why)
            : WinRtToast.Show(identity.AppId, ToastXml.Build(title, body, options.Urgency, link),
                options.Tag is null ? null : ToastXml.TagOf(options.Tag),
                options.Group is null ? null : ToastXml.TagOf(options.Group));
    }

    /// <summary>Withdraws the toast of <paramref name="tag"/> and <paramref name="group"/> from the notification
    /// centre. A toast that is not there is a success.</summary>
    /// <param name="tag">The tag it was shown with; not blank.</param>
    /// <param name="group">The group it was shown with; not blank.</param>
    /// <param name="appId">The app id it was shown under, when it was not the claimed one.</param>
    /// <returns>The outcome; never throws for the system.</returns>
    /// <exception cref="ArgumentException">The tag or the group is null or blank.</exception>
    public static DesktopNotificationResult Withdraw(string tag, string group, string? appId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        if (!OperatingSystem.IsWindows())
        {
            return DesktopNotificationResult.Unsupported();
        }

        var identity = CurrentIdentity(appId);
        return identity.Reason is { } why
            ? DesktopNotificationResult.NoIdentity(why)
            : WinRtToast.Withdraw(identity.AppId, ToastXml.TagOf(tag), ToastXml.TagOf(group));
    }

    /// <summary>The app's toasts still in the notification centre, in <see cref="DesktopNotificationResult.Toasts"/>
    /// (a tag or group longer than <see cref="TagLimit"/> reads back as its short form).</summary>
    /// <param name="appId">The app id they were shown under, when it was not the claimed one.</param>
    /// <returns>The outcome; never throws for the system.</returns>
    public static DesktopNotificationResult GetDelivered(string? appId = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return DesktopNotificationResult.Unsupported();
        }

        var identity = CurrentIdentity(appId);
        return identity.Reason is { } why
            ? DesktopNotificationResult.NoIdentity(why)
            : WinRtToast.Delivered(identity.AppId);
    }

    /// <summary>Whether Windows would take a toast right now: the identity resolves, Windows makes a notifier for the
    /// app id, and its toasts are not turned off. Shows nothing. An app id that has not shown a toast yet has no
    /// setting to read (Windows makes it on the first toast) and is not refused for that.</summary>
    /// <param name="appId">The app id to ask about, in place of the claimed one.</param>
    /// <returns><see cref="DesktopNotificationStatus.Succeeded"/> when one would; otherwise the status and reason
    /// for why not. Never throws for the system.</returns>
    public static DesktopNotificationResult Probe(string? appId = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return DesktopNotificationResult.Unsupported();
        }

        var identity = CurrentIdentity(appId);
        return identity.Reason is { } why
            ? DesktopNotificationResult.NoIdentity(why)
            : WinRtToast.Probe(identity.AppId);
    }

    /// <summary>Says once, why toasts are unavailable (see <see cref="Probe(string?)"/>); nothing when they are.</summary>
    /// <param name="log">Where the line goes (an app's own log).</param>
    public static void LogOnce(Action<string> log)
    {
        LogSink.Require(log);
        LogOnce(log, Probe, ref _logged);
    }

    /// <summary>Says once, why toasts are unavailable (see <see cref="Probe(string?)"/>); nothing when they are.</summary>
    /// <param name="logger">The logger the line goes to, at information level.</param>
    public static void LogOnce(ILogger logger)
    {
        LogOnce(LogSink.From(logger));
    }

    /// <summary>The body of <see cref="LogOnce(Action{string})"/> with the probe and the once-guard as parameters,
    /// so a test sees it log once without asking Windows.</summary>
    internal static void LogOnce(Action<string> log, Func<string?, DesktopNotificationResult> probe, ref int logged)
    {
        var result = probe(null);
        if (!result.IsSuccess && Interlocked.Exchange(ref logged, 1) == 0)
        {
            log($"no desktop notifications ({result.Status}): {result.Reason}");
        }
    }

    // The identity this process shows toasts under, read from what Claim recorded and what the process carries.
    private static (string? AppId, string? Reason) CurrentIdentity(string? requested) =>
        ResolveIdentity(requested, AppIdentity.ClaimedAppId, AppIdentity.RegisteredAppId, AppIdentity.ExplicitAppId(), AppIdentity.ClaimedPackaged || AppIdentity.IsPackaged);

    /// <summary>
    /// Which app id a toast is shown under, in order: the one asked for, the one a successful
    /// <see cref="AppIdentity.Claim(string, string)"/> gave, the one <see cref="AppIdentity.Register(string, string, string?)"/>
    /// registered, the process's own explicit app id, and, for a packaged
    /// process, none (the default notifier, which the package's manifest names). Pure.
    /// </summary>
    /// <returns>The app id (null for the default notifier) with a null reason, or a null id and the reason there is
    /// none.</returns>
    internal static (string? AppId, string? Reason) ResolveIdentity(string? requested, string? claimed, string? registered, string? explicitId, bool packaged)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return (requested, null);
        }

        if (!string.IsNullOrWhiteSpace(claimed))
        {
            return (claimed, null);
        }

        if (!string.IsNullOrWhiteSpace(registered))
        {
            return (registered, null);
        }

        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            return (explicitId, null);
        }

        return packaged
            ? (null, null)
            : (null, "the app has no identity Windows can show a toast under: call AppIdentity.Claim at startup, or give DesktopNotificationOptions.AppId");
    }
}
