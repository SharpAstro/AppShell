using Microsoft.Extensions.Logging;

namespace SharpAstro.AppShell;

/// <summary>
/// Leaves a trace of an exception nothing caught, wherever the app keeps its log. Install it first thing in
/// <c>Main</c>: an exception thrown before an app's own logging is up, on a background thread, or out of a
/// callback from native code otherwise ends the process with nothing in any log file.
///
/// <para>Hooks <see cref="AppDomain.UnhandledException"/> (the process is going down) and
/// <see cref="TaskScheduler.UnobservedTaskException"/> (a faulted task nobody awaited; the process lives on, so
/// this is often the only sign it happened), once, however many sinks are installed. Every sink gets the full
/// exception text, stamped with the time and what kind of failure it was.</para>
///
/// <para><b>iOS:</b> a managed exception that reaches a native frame is turned into an Objective-C exception and
/// raises <c>ObjCRuntime.Runtime.MarshalManagedException</c>, not <see cref="AppDomain.UnhandledException"/>. This
/// library is plain <c>net10.0</c> and cannot see that type, so an iOS head adds the one hook itself and forwards:
/// <c>Runtime.MarshalManagedException += (_, e) =&gt; CrashTrace.Write("managed exception crossing into native code",
/// e.Exception);</c>. That is the path the first iPad crash of chess took.</para>
///
/// <para>Never throws: a sink that fails is skipped, and when no sink took the text it goes to stderr. A crash
/// handler that throws loses the one trace it existed to keep.</para>
/// </summary>
public static class CrashTrace
{
    private static readonly Lock Gate = new();
    private static Action<string>[] _sinks = [];
    private static bool _hooked;

    /// <summary>Sends every uncaught exception's trace to <paramref name="sink"/> (a log file, a rolling log).</summary>
    public static void Install(Action<string> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (Gate)
        {
            _sinks = [.. _sinks, sink];
            if (_hooked) return;
            _hooked = true;
            AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
                Write($"unhandled exception (terminating: {e.IsTerminating})", e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += static (_, e) =>
                Write("unobserved task exception", e.Exception);
        }
    }

    /// <summary>Sends every uncaught exception's trace to <paramref name="logger"/>, at Critical.</summary>
    public static void Install(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Install(text => logger.LogCritical("{CrashTrace}", text));
    }

    /// <summary>
    /// Writes one trace to every installed sink: <paramref name="what"/> says what kind of failure (the hooks above
    /// say "unhandled exception" and "unobserved task exception"; a host forwarding a platform's own event says its
    /// own), <paramref name="exception"/> is written whole, stack and inner exceptions included.
    /// </summary>
    public static void Write(string what, object? exception)
    {
        var text = $"{DateTimeOffset.Now:O} {what}:{Environment.NewLine}{exception}";
        var delivered = false;
        foreach (var sink in Volatile.Read(ref _sinks))
        {
            try
            {
                sink(text);
                delivered = true;
            }
            catch
            {
                // The next sink, or stderr, still gets it.
            }
        }

        if (!delivered)
        {
            try { Console.Error.WriteLine(text); } catch { /* nowhere left to write */ }
        }
    }
}
