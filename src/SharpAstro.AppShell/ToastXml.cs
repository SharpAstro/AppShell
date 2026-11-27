using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace SharpAstro.AppShell;

/// <summary>
/// The toast XML of <see cref="DesktopNotification"/>, pure and the same on every OS so it can be tested anywhere.
/// Ported from pilot's <c>ToastXml</c> (SharpAstro/pilot#1378): the ToastGeneric binding with a title line and an
/// optional body line, whitespace flattened, characters XML 1.0 does not allow dropped (an ESC from terminal output
/// makes <c>XmlDocument.LoadXml</c> throw), the text cut to the limits with an ellipsis, then XML-escaped.
/// </summary>
internal static class ToastXml
{
    /// <summary>The toast XML for <paramref name="title"/> and <paramref name="body"/> at <paramref name="urgency"/>.</summary>
    internal static string Build(string title, string? body, ToastUrgency urgency)
    {
        // A reminder or an urgent toast without a button behaves like a normal one (a reminder would not stay
        // until dismissed), so both carry the system's own dismiss action. activationType="system" with
        // arguments="dismiss" and an empty content gets the localised label of the system's Dismiss button.
        var scenario = urgency switch
        {
            ToastUrgency.Reminder => " scenario=\"reminder\"",
            ToastUrgency.Urgent => " scenario=\"urgent\"",
            _ => string.Empty,
        };
        var actions = urgency == ToastUrgency.Normal
            ? string.Empty
            : "<actions><action content=\"\" arguments=\"dismiss\" activationType=\"system\"/></actions>";

        var lines = $"<text>{Escape(Cut(title, DesktopNotification.TitleLimit))}</text>";
        var text = Cut(body, DesktopNotification.BodyLimit);
        if (text.Length > 0)
        {
            lines += $"<text>{Escape(text)}</text>";
        }

        return $"<toast{scenario}><visual><binding template=\"ToastGeneric\">{lines}</binding></visual>{actions}</toast>";
    }

    /// <summary>
    /// <paramref name="text"/> with each whitespace run (newlines too) as one space, characters XML 1.0 does not
    /// allow dropped, and cut to <paramref name="limit"/> characters, the last of them an ellipsis when it was cut.
    /// Never splits a surrogate pair.
    /// </summary>
    internal static string Cut(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var flat = XmlCharsOf(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        if (flat.Length <= limit)
        {
            return flat;
        }

        var end = limit - 1;
        if (end > 0 && char.IsHighSurrogate(flat[end - 1]))
        {
            end--;
        }

        return flat[..end].TrimEnd() + "…";
    }

    // A control character is dropped, and a surrogate is kept only with its other half: a lone one, which only
    // invalid text holds, makes LoadXml throw.
    private static string XmlCharsOf(string text)
    {
        var kept = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], c))
                {
                    kept.Append(c).Append(text[i + 1]);
                    i++;
                }
            }
            else if (!char.IsLowSurrogate(c) && XmlConvert.IsXmlChar(c))
            {
                kept.Append(c);
            }
        }

        return kept.ToString();
    }

    /// <summary>XML-escapes <paramref name="text"/> for element text and attribute values: <c>&amp; &lt; &gt; " '</c>.</summary>
    internal static string Escape(string text) => SecurityElement.Escape(text) ?? string.Empty;

    /// <summary>
    /// The tag or group Windows gets for <paramref name="name"/>: the name itself when it fits
    /// <see cref="DesktopNotification.TagLimit"/> (a longer one throws in WinRT), else <c>s:</c> and the first 40 hex
    /// characters of its SHA-256, stable so a show and the later withdraw name the same toast. The one function
    /// both use.
    /// </summary>
    internal static string TagOf(string name)
    {
        if (name.Length <= DesktopNotification.TagLimit)
        {
            return name;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return "s:" + Convert.ToHexStringLower(hash)[..40];
    }
}
