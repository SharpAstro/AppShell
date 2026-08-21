namespace SharpAstro.AppShell;

/// <summary>
/// The least a toolkit has to expose for a hand-off to bring its window forward correctly.
///
/// <para>Three members, deliberately. Every windowing toolkit has all three, so an adapter is a
/// handful of lines, and going through an interface keeps this library free of any toolkit
/// dependency -- which is the whole reason a single-instance gate is reusable at all. The
/// alternative, taking a reference on one windowing library, would make a named-pipe gate
/// unavailable to every app built on a different one.</para>
/// </summary>
public interface IActivatableWindow
{
    /// <summary>Whether the window is currently minimised (iconified).</summary>
    bool IsMinimized { get; }

    /// <summary>
    /// Return the window to its normal size and position.
    ///
    /// <para><b>Implementers do not need to narrow this, and callers must not assume it is
    /// harmless.</b> On the toolkits this was written against -- SDL, and <c>ShowWindow</c> with
    /// <c>SW_RESTORE</c> underneath it -- restoring un-maximises as well as un-minimises. That is
    /// why <c>Activate</c> calls it conditionally rather than as a tidy-up prelude.</para>
    /// </summary>
    void Restore();

    /// <summary>Bring the window to the front and give it input focus.</summary>
    void Raise();
}

/// <summary>
/// Bringing the running instance's window forward, once <see cref="InstanceGate"/> has handed it a
/// request. The counterpart to <see cref="ForegroundActivation"/>, which spends the LAUNCHING
/// process's foreground right so that this side is permitted to succeed at all.
///
/// <para><b>Why this is a shared decision and not two lines at each call site.</b> The two obvious
/// spellings are both wrong, in opposite directions, and each looks correct until someone tries the
/// other window state:</para>
///
/// <list type="bullet">
/// <item><description><c>Raise()</c> alone. Raising moves input focus WITHOUT un-minimising, so a
/// minimised window becomes the foreground window while still parked off-screen (measured at
/// -21333,-21333 on Windows). Keyboard input then goes somewhere the user cannot see, which is
/// worse than the taskbar flash that activation exists to replace.</description></item>
/// <item><description><c>Restore()</c> then <c>Raise()</c>. This fixes the minimised case and
/// breaks the commonest one: restoring un-maximises, so a maximised window is knocked back to its
/// floating size on every hand-off. That shipped, and it presents as "opening a second file
/// un-maximises my window" -- a window-management bug with no obvious connection to the file
/// association that caused it.</description></item>
/// </list>
///
/// <para>So restore only when the window is actually minimised. The compound state needs no special
/// case: a window minimised FROM maximised is restored by the OS back to maximised, because that is
/// what <c>SW_RESTORE</c> means.</para>
/// </summary>
public static class WindowActivation
{
    extension(IActivatableWindow window)
    {
        /// <summary>
        /// Bring <paramref name="window"/> forward for a hand-off, preserving whether it was
        /// maximised. Safe to call for an empty payload ("activate only"), which is the whole of the
        /// work for an app with no document to open.
        /// </summary>
        public void Activate()
        {
            // Only a minimised window is off-screen and unable to receive the focus that Raise is
            // about to give it. Anything else -- maximised, or merely behind another window -- is
            // already where the user put it, and restoring it would MOVE it.
            if (window.IsMinimized)
            {
                window.Restore();
            }

            window.Raise();
        }
    }
}
