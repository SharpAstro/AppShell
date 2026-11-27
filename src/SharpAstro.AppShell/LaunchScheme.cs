using System.Security;
using System.Text;

namespace SharpAstro.AppShell;

/// <summary>What <see cref="LaunchScheme.Register(string, string, string)"/> found, or did, at the scheme's registry key.</summary>
public enum LaunchSchemeRegistration
{
    /// <summary>Nothing was done: not Windows.</summary>
    None,

    /// <summary>The scheme was there already, ours, and its command starts an executable that exists.</summary>
    Present,

    /// <summary>There was none; it was written, starting this executable.</summary>
    Created,

    /// <summary>The scheme was ours but started an executable that is gone (the app was moved); it now starts this one.</summary>
    Repointed,

    /// <summary>The scheme is registered by someone else (it carries no <c>AppUserModelID</c>, or another one): left as it is.</summary>
    Foreign,

    /// <summary>The registry could not be read or written.</summary>
    Failed,
}

/// <summary>
/// A per-user URL scheme (<c>myapp:</c>) that a click on a toast starts the app with, and the link it carries.
/// <see cref="Register(string, string, string)"/> writes <c>HKCU\Software\Classes\&lt;scheme&gt;</c> so that the shell
/// runs this executable with the link as its one argument; <see cref="Link"/> builds a link from a payload and
/// <see cref="TryRead"/> reads the payload back. Pass the payload as
/// <see cref="DesktopNotificationOptions.LaunchPayload"/> and the toast carries the link.
///
/// <para><b>The scheme is ours when it carries our app id.</b> <c>AppUserModelID</c> is the documented ProgID value that ties
/// what a scheme starts to an app, and it is also the ownership mark: a scheme with none, or another app's, is left alone
/// (<see cref="LaunchSchemeRegistration.Foreign"/>). One that is ours and starts an executable that exists is kept, so an
/// installed copy keeps the scheme over a development build; one that starts nothing is repointed.</para>
///
/// <para><b>The payload is untrusted input.</b> Any process or web page can start a registered scheme with any text.
/// Treat the payload as an id to look up, never as a command, a path to run or a file to open unchecked.</para>
/// </summary>
public static class LaunchScheme
{
    internal const string AppIdValue = "AppUserModelID";

    /// <summary>The scheme registered by a successful <see cref="Register(string, string, string)"/> in this process,
    /// or null. What <see cref="DesktopNotification.Show"/> builds a launch link with.</summary>
    internal static string? RegisteredScheme { get; private set; }

    // Schemes the shell or the browser own, which an app must not take over.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "http", "https", "file", "mailto", "ftp", "ftps", "sftp", "ws", "wss", "data", "javascript", "about", "blob",
        "tel", "sms", "callto", "news", "nntp", "telnet", "gopher", "ldap", "ldaps", "res", "shell", "vbscript",
        "search-ms", "view-source", "chrome", "edge", "ssh",
    };

    internal static string KeyOf(string scheme) => @"Software\Classes\" + scheme;

    internal static string CommandKeyOf(string scheme) => KeyOf(scheme) + @"\shell\open\command";

    /// <summary>Whether <paramref name="scheme"/> is a valid RFC 3986 scheme (a letter, then letters, digits, <c>+ - .</c>)
    /// in lower case. A reserved scheme is still valid here; see <see cref="IsReserved"/>.</summary>
    internal static bool IsValid(string? scheme)
    {
        if (string.IsNullOrEmpty(scheme) || scheme[0] is not (>= 'a' and <= 'z'))
        {
            return false;
        }

        foreach (var c in scheme)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the shell or the browser owns <paramref name="scheme"/> (<c>http</c>, <c>file</c>, <c>mailto</c>,
    /// <c>ms-*</c> and others).</summary>
    internal static bool IsReserved(string scheme) =>
        Reserved.Contains(scheme)
        || scheme.StartsWith("ms-", StringComparison.Ordinal)
        || scheme.StartsWith("microsoft-", StringComparison.Ordinal)
        || scheme.StartsWith("windows-", StringComparison.Ordinal);

    internal static void ThrowIfBad(string? scheme, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme, paramName);
        if (!IsValid(scheme))
        {
            throw new ArgumentException($"'{scheme}' is not a valid lower-case URL scheme (a letter, then letters, digits, + - .)", paramName);
        }

        if (IsReserved(scheme))
        {
            throw new ArgumentException($"'{scheme}' is a scheme the shell or the browser owns", paramName);
        }
    }

    /// <summary>
    /// Registers <paramref name="scheme"/> for the current user so that opening <c>scheme:...</c> starts this executable
    /// with the link as its argument, unless it is someone else's. Idempotent: it writes only what differs.
    /// </summary>
    /// <param name="scheme">A lower-case RFC 3986 scheme; not one the shell owns.</param>
    /// <param name="appId">The app id that marks the scheme as this app's: the one given to <see cref="AppIdentity.Claim(string, string)"/>.</param>
    /// <param name="displayName">The name shown for the scheme (<c>URL:&lt;displayName&gt;</c>).</param>
    /// <returns>What was found or done; never throws for the registry. <see cref="LaunchSchemeRegistration.None"/> off Windows.</returns>
    /// <exception cref="ArgumentException">The scheme is invalid or reserved, or the app id or the name is blank.</exception>
    public static LaunchSchemeRegistration Register(string scheme, string appId, string displayName)
    {
        Validate(scheme, appId, displayName);
        if (!OperatingSystem.IsWindows())
        {
            return LaunchSchemeRegistration.None;
        }

        var result = Register(scheme, appId, displayName, Environment.ProcessPath, new UserRegistry(), File.Exists);
        if (result is LaunchSchemeRegistration.Present or LaunchSchemeRegistration.Created or LaunchSchemeRegistration.Repointed)
        {
            RegisteredScheme = scheme;
        }

        return result;
    }

    private static void Validate(string scheme, string appId, string displayName)
    {
        ThrowIfBad(scheme, nameof(scheme));
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
    }

    /// <summary>The body of <see cref="Register(string, string, string)"/> with the registry, the executable and the file
    /// check as parameters, so a test sees what is written without a registry.</summary>
    internal static LaunchSchemeRegistration Register(string scheme, string appId, string displayName, string? exe,
        IUserRegistry registry, Func<string, bool> fileExists)
    {
        Validate(scheme, appId, displayName);
        if (string.IsNullOrEmpty(exe))
        {
            return LaunchSchemeRegistration.Failed;
        }

        try
        {
            var key = KeyOf(scheme);
            var defaultValue = registry.Read(key, "");
            var markedId = registry.Read(key, AppIdValue);
            var command = registry.Read(CommandKeyOf(scheme), "");
            (string? AppId, string? Exe)? existing = defaultValue is null && markedId is null && command is null
                ? null
                : (markedId, ExeOf(command));

            var action = Decide(existing, appId, fileExists);
            if (action == SchemeAction.LeaveAlone)
            {
                return LaunchSchemeRegistration.Foreign;
            }

            if (action == SchemeAction.Keep)
            {
                return LaunchSchemeRegistration.Present;
            }

            foreach (var (subKey, name, value) in Values(scheme, appId, displayName, exe))
            {
                if (registry.Read(subKey, name) != value)
                {
                    registry.Write(subKey, name, value);
                }
            }

            return action == SchemeAction.Create ? LaunchSchemeRegistration.Created : LaunchSchemeRegistration.Repointed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return LaunchSchemeRegistration.Failed;
        }
    }

    /// <summary>What the scheme holds for <paramref name="exe"/>: (key, value name, value); an empty name is the key's default.</summary>
    internal static IReadOnlyList<(string SubKey, string Name, string Value)> Values(string scheme, string appId, string displayName, string exe) =>
    [
        (KeyOf(scheme), "", "URL:" + displayName),
        (KeyOf(scheme), "URL Protocol", ""),
        (KeyOf(scheme), AppIdValue, appId),
        (CommandKeyOf(scheme), "", $"\"{exe}\" \"%1\""),
    ];

    /// <summary>The executable a registered command starts: the quoted first token, or the first word of an unquoted one;
    /// null for none.</summary>
    internal static string? ExeOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var text = command.Trim();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    /// <summary>What to do at a scheme, given what is there: create when missing; leave it alone when it carries no app id or
    /// another one; keep it when ours starts an executable that exists; repoint it when ours starts nothing. Pure.</summary>
    /// <param name="existing">The app id the scheme carries and the executable its command starts (either possibly null), or
    /// null when the scheme is not there.</param>
    internal static SchemeAction Decide((string? AppId, string? Exe)? existing, string appId, Func<string, bool> fileExists)
    {
        if (existing is not { } found)
        {
            return SchemeAction.Create;
        }

        if (!string.Equals(found.AppId, appId, StringComparison.Ordinal))
        {
            return SchemeAction.LeaveAlone;
        }

        return found.Exe is { Length: > 0 } exe && fileExists(exe) ? SchemeAction.Keep : SchemeAction.Repoint;
    }

    internal enum SchemeAction
    {
        Create,
        Keep,
        Repoint,
        LeaveAlone,
    }

    /// <summary>The link for <paramref name="payload"/>: <c>&lt;scheme&gt;:</c> and the payload escaped.</summary>
    /// <exception cref="ArgumentException">The scheme is invalid or reserved.</exception>
    public static string Link(string scheme, string payload)
    {
        ThrowIfBad(scheme, nameof(scheme));
        ArgumentNullException.ThrowIfNull(payload);
        return scheme + ":" + Uri.EscapeDataString(payload);
    }

    /// <summary>
    /// Reads the payload back from a link <see cref="Link"/> made. False, with a null payload, for a link of another scheme
    /// (the scheme compares without regard to case), for text that is no link, and for a malformed one (an escape that is not
    /// two hex digits, bytes that are not UTF-8, a character <see cref="Link"/> never leaves unescaped).
    /// The payload is untrusted input: see the class remarks.
    /// </summary>
    public static bool TryRead(string? arg, string scheme, out string? payload)
    {
        payload = null;
        if (arg is null || !IsValid(scheme) || arg.Length <= scheme.Length
            || !arg.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) || arg[scheme.Length] != ':')
        {
            return false;
        }

        var bytes = new List<byte>(arg.Length);
        for (var i = scheme.Length + 1; i < arg.Length; i++)
        {
            var c = arg[i];
            if (c == '%')
            {
                if (i + 2 >= arg.Length || !Uri.IsHexDigit(arg[i + 1]) || !Uri.IsHexDigit(arg[i + 2]))
                {
                    return false;
                }

                bytes.Add((byte)((Uri.FromHex(arg[i + 1]) << 4) | Uri.FromHex(arg[i + 2])));
                i += 2;
            }
            else if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.' or '_' or '~')
            {
                bytes.Add((byte)c);
            }
            else
            {
                return false;
            }
        }

        try
        {
            payload = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.ToArray());
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
