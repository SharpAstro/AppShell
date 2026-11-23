using Microsoft.Extensions.Logging;

namespace SharpAstro.AppShell;

/// <summary>
/// The argument checks and the logger adapter shared by <see cref="DesktopAccent"/> and <see cref="DesktopAppearance"/>,
/// kept apart from them so a test can reach them without running their initialisers (which read the real registry).
/// </summary>
internal static class LogSink
{
    /// <summary>Throws at once on a null sink; otherwise returns it.</summary>
    internal static Action<string> Require(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return log;
    }

    /// <summary>Throws at once on a null logger; otherwise a sink that logs each line at information level.</summary>
    internal static Action<string> From(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return message => logger.LogInformation("{Message}", message);
    }
}
