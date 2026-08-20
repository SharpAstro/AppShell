using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpAstro.AppShell;

/// <summary>
/// The half of window activation that has to happen in the process that is going away.
///
/// <para>Windows does not let a background process pull itself to the front. A process may only
/// call <c>SetForegroundWindow</c> if it is already the foreground process, received the last input
/// event, or was granted the right by such a process. A running app that has been sitting behind
/// other windows is none of those, so its own attempt to raise itself is downgraded to flashing its
/// taskbar button -- which looks exactly like a hand-off that silently failed.</para>
///
/// <para>The launching process, on the other hand, does hold that right: the shell just started it
/// in response to a user double-click. So it spends the right on the process it is handing to,
/// which is what <see cref="AllowFor"/> does, and only then sends the payload. Getting that order
/// wrong grants nothing, because the grant has to precede the target's attempt.</para>
///
/// <para>On every other platform this is a no-op: X11, Wayland and macOS each have their own focus
/// policy and none of them needs a grant from the caller. A viewer on those platforms raises its
/// window with whatever its toolkit offers.</para>
/// </summary>
public static partial class ForegroundActivation
{
    /// <summary>
    /// Grant <paramref name="processId"/> the right to bring its window to the front. Returns
    /// whether the grant was made, which is NOT the same question as whether the window then came
    /// forward.
    ///
    /// <para>The distinction is the whole reason this reports anything. The OS refuses the grant
    /// when the CALLER does not itself hold the foreground right -- which is normal for a process
    /// started by a script rather than by a user's double-click. Without this return value, that
    /// case and a genuinely broken raise are indistinguishable: both look like a window that
    /// flashed its taskbar button and stayed put.</para>
    ///
    /// <para>Always true off Windows, where nothing needs granting; a false is never fatal, because
    /// the fallback is the taskbar flash this is trying to improve on.</para>
    /// </summary>
    public static bool AllowFor(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            return AllowSetForegroundWindow((uint)processId);
        }
        catch (Exception)
        {
            // A missing entry point on some future Windows edition must not break a hand-off whose
            // payload has not been sent yet.
            return false;
        }
    }

    // LibraryImport, not DllImport: the marshalling is source-generated, so this stays AOT-clean.
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool AllowSetForegroundWindow(uint dwProcessId);
}
