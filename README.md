# SharpAstro.AppShell

Desktop app-shell plumbing for single-window native applications. Pure managed, AOT- and
trim-friendly, one dependency (`Microsoft.Extensions.Logging.Abstractions`).

```
dotnet add package SharpAstro.AppShell
```

## Why this exists

Register a file type and every double-click in Explorer becomes a **fresh process** — its own GPU
device, its own font atlas, its own copy of every cache it touches. What the user wanted was for the
file to appear in the window already on screen.

`InstanceGate` turns those later launches into a message to the running instance, and
`ForegroundActivation` is the half that makes its window actually come to the front.

## The identity is yours to choose

A gate is claimed on a *channel*, and a channel is a scope plus an arbitrary identity string. That
one parameter is the whole policy:

```csharp
// One instance for the whole application: every file joins the running window.
var channel = InstanceGate.ChannelFor("my-viewer");

// One instance per open folder: a file in a folder already on screen activates that window,
// a file anywhere else gets a new one.
var channel = InstanceGate.ChannelFor("my-viewer", InstanceGate.NormalizePathIdentity(folder));
```

Startup then reads:

```csharp
var gate = InstanceGate.TryClaim(channel, logger);
if (gate is null)
{
    // Somebody already owns this identity. Hand them the file and leave.
    if (InstanceGate.TryHandOff(channel, filePath, TimeSpan.FromSeconds(5), logger))
    {
        return 0;
    }
    // Hand-off failed: fall through and open it here. An extra window beats nothing happening.
}

// ... build the window, then once per frame:
while (gate?.TryDequeue(out var request) == true)
{
    Open(request.Payload);
    RaiseWindow();          // your toolkit's raise; the grant has already been made for you
}
```

To ask "is that instance running?" without claiming or connecting (a holder sees no hand-off), probe:
`!InstanceGate.IsKnownAbsent(channel)` means held, or cannot tell (always so off Windows).

`NormalizePathIdentity` is what makes the per-folder mode work: it folds a trailing separator, a
relative path and (on Windows and macOS only) a difference in case into one identity, so `C:\Data`
and `c:\data\` do not open two windows onto the same folder.

## Three things it gets right that are easy to get wrong

**On Windows the pipe is the lock.** A named pipe with a single server instance can only be created
once, so claiming it *is* the primacy test — there is no separate mutex, one lifetime to get right,
and no abandoned-mutex case. On Linux and macOS .NET's pipe is a Unix-domain socket whose server
replaces whatever socket is at its path, so there the claim is an advisory lock (`flock`) on a file
beside it, which the kernel releases when its process dies, a crash included.

**The accept loop is not on the thread pool.** The pipe is deliberately not `Asynchronous`, and the
accept runs on a dedicated thread. An awaited accept resumes on a pool worker, and an app of this
kind saturates its own pool with decode work — so the accept would queue behind that and a client
would time out while the app was merely busy. A busy app refusing hand-offs is exactly the stray
window this is meant to prevent, and an idle measurement never shows it.

**Activation has to be granted by the process that is leaving.** Windows will not let a background
process pull itself to the front; the right must come from a process that currently holds it. The
launching process does hold it — the shell just started it — so it spends the right on the target
before sending the payload, via `AllowSetForegroundWindow`. Skip this and the running window flashes
its taskbar button and stays behind, which reads as the hand-off silently failing. It is a no-op off
Windows, where focus policy belongs to the compositor.

## A name the shell can show

An app shipped as a zip has no identity of its own: Windows knows its windows by the executable's file name,
and its media flyout calls the app's media session "Unknown app". One call at startup, before the first window,
fixes that:

```csharp
AppIdentity.Claim("MyCompany.Viewer", "Viewer");
```

It gives the process the app id (an AppUserModelID) and makes sure a Start menu shortcut named "Viewer"
carries it, which is where the shell finds the name and the icon, and which also puts the app in Start menu
search. The shortcut is written once and afterwards only read: it follows the app when the executable it points
at is gone, stays with the copy it points at while that is there, and a shortcut of the same name that does not
carry the id is never rewritten. No-op off Windows.

Inside an app package (MSIX, or a sparse package) it does nothing and returns `StartMenuShortcut.Packaged`: the
package's manifest names the app already, an app id of the app's own would take its windows away from the package's
Start entry, and the shortcut would be a second Start entry beside the package's own (or, where Windows redirects a
packaged app's new AppData files to a private copy, a file the shell never sees). So the same call is right in a
zip build and in a Store build of the same executable. `AppIdentity.IsPackaged` answers the question on its own.

## Desktop notifications

`DesktopNotification` shows, replaces and withdraws a Windows toast in process, through the WinRT toast API
called by vtable: no PowerShell, no .NET Framework, no CsWinRT, no `-windows` target framework and no helper DLL,
so it is AOT-clean from plain `net10.0`.

**Call `AppIdentity.Claim` first.** Windows shows a toast under an app, and the app id and shortcut that `Claim`
gives the process are what give the toast its name and icon. A toast is shown under, in order: the
`AppId` in the options, the id of a successful `Claim`, the id of a successful `AppIdentity.Register` (see below),
the process's own explicit app id, and for a packaged process the default notifier (its manifest names the app).
With none of these the result is `NoIdentity`.

```csharp
AppIdentity.Claim("MyCompany.Viewer", "Viewer");

var options = new DesktopNotificationOptions { Tag = "export", Group = "jobs" };
DesktopNotification.Show("Export finished", "42 files written", options);
DesktopNotification.Show("Export finished", "43 files written", options); // replaces the first
DesktopNotification.Withdraw("export", "jobs");                           // removes it from the notification centre
var left = DesktopNotification.GetDelivered().Toasts;                     // tag and group of what is still there
```

A toast with the same tag and group replaces the earlier one; `Withdraw` takes it back (one that is not there is
a success). A tag or group longer than 64 characters, which Windows refuses, is mapped to a short stable form, so
`Show` and `Withdraw` still name the same toast. Text is flattened to single spaces, stripped of characters XML
cannot hold, and cut to 60 (title) and 200 (body) characters with an ellipsis.

Urgency: `Normal` shows, then rests in the notification centre. `Reminder` stays on screen until the person
dismisses it, with the system's own Dismiss button. `Urgent` is a reminder that Windows 11 can let through Do Not
Disturb when the person allows that for the app, so keep it for the most serious level.

Every call returns one `DesktopNotificationResult`: a `Status` (`Succeeded`, `NoIdentity`, `Disabled`,
`Unsupported`, `Failed`) and a `Reason` that names the failing call and its HRESULT. Nothing throws for the system;
only a wrong argument (a blank title, a tag without a group) throws `ArgumentException`. `Probe()` says whether
Windows would take a toast without showing one, and `DesktopNotification.LogOnce(log)` (or an `ILogger`) says once
why toasts are unavailable and nothing when they are. Off Windows every call returns `Unsupported` for now.

`Disabled` comes from the notifier's setting (turned off for the app or the person, by group policy or by the
manifest). An app id that has never shown a toast has no setting yet: Windows keeps it under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\<app id>`, makes it on the first toast, and
until then answers the read with ERROR_NOT_FOUND (0x80070490), in a console and a remote desktop session alike.
That answer is not a refusal, so the first toast is shown.

### A process with no shortcut: the registry identity

A process with no windows and no Start menu shortcut (a headless node) can still show toasts.
`AppIdentity.Register(appId, displayName, iconPath?)` writes `HKCU\Software\Classes\AppUserModelId\<appId>` with a
`DisplayName` and, only when `iconPath` is given, an `IconUri`; that is all Windows needs to show a toast under the
id. It sets no app id on the process and writes no shortcut, writes only the values that differ (so it is idempotent),
does nothing in a package or off Windows, and returns an `AppUserModelRegistration` (`Present`, `Written`, `Failed`,
`Packaged`, `None`), never throwing for the registry. `DesktopNotification` resolves the id it registered right after
the claimed one. The shell reads `IconUri` as an image file (`.png` or `.ico`): an `.exe` there leaves the default icon.

A `Shell_NotifyIcon` balloon fallback is left out on purpose: the registry identity already covers a process without
a shortcut, so there is no second path to maintain.

### A click on a toast

A click reaches the running instance through a per-user URL scheme and `InstanceGate`. `LaunchScheme.Register(scheme,
appId, displayName)` writes `HKCU\Software\Classes\<scheme>` so that the shell starts this executable with the link
as its one argument. The scheme carries `AppUserModelID=<appId>`, which ties what it starts to the app and marks the
scheme as yours: one that is missing is created, one that is yours and starts an executable that exists is kept (an
installed copy keeps it over a development build), one that is yours and starts nothing is repointed, and one with no
`AppUserModelID` or another one is left alone (`Foreign`). The scheme is a lower-case RFC 3986 scheme; `http`,
`https`, `file`, `mailto`, `ms-*` and the other schemes the shell owns are refused with `ArgumentException`.

```csharp
AppIdentity.Claim("MyCompany.Viewer", "Viewer");
LaunchScheme.Register("viewer", "MyCompany.Viewer", "Viewer");

// A toast whose click hands "export-42" to the app:
DesktopNotification.Show("Export finished", null, new DesktopNotificationOptions { LaunchPayload = "export-42" });

// At startup, before opening the window:
var channel = InstanceGate.ChannelFor("viewer");
if (args.Length > 0 && LaunchScheme.TryRead(args[0], "viewer", out _))
{
    if (InstanceGate.TryHandOff(channel, args[0], TimeSpan.FromSeconds(2)))
    {
        return; // the running instance has it
    }
    // no instance is running: fall through, this process opens normally with the payload
}

// In the running instance, for each hand-off the gate delivers:
if (gate.TryDequeue(out var request) && LaunchScheme.TryRead(request.Payload, "viewer", out var id))
{
    OpenExport(id);          // look the id up
    window.Activate();       // WindowActivation, as for a file hand-off
}
```

`DesktopNotificationOptions.LaunchPayload` makes the toast carry `activationType="protocol"` and the link
`LaunchScheme.Link(scheme, payload)`, with the scheme registered in this process or `DesktopNotificationOptions.Scheme`.
A payload with no scheme is an `ArgumentException`; without a payload the toast carries no launch.

**The payload is untrusted input.** Any process or web page can start a registered scheme with any text, so what
comes back from `TryRead` is an id to look up, never a command, a path to run or a file to open unchecked.
`TryRead` itself only accepts the links `Link` makes and returns false for another scheme or a malformed link.

### Trying it

`samples/ToastSample` is a NativeAOT Win32 sample that exercises all of the above in a window that logs every step.

```
dotnet publish samples/ToastSample/ToastSample.csproj -c Release -r win-x64
samples\ToastSample\bin\Release\net10.0\win-x64\publish\ToastSample.exe --self-test
```

`--self-test` runs, for the claimed (shortcut) identity and for a registry-only identity: show, show again with the
same tag (still exactly one delivered), a Reminder, an Urgent toast, and a withdraw that leaves none. For the claimed
identity it also starts the toast's launch link through the shell, as a click does, and checks that the running
instance receives the same payload within 15 seconds. It exits 0 on success, 1 on a failure, and 2 when the app id
cannot show toasts here (no identity, or toasts turned off for it). Every step goes to the window, to stdout and to
`ToastSample.log` beside the exe.

Manual check for a person (a script cannot click): run `ToastSample.exe`, minimise the window, click the toast, and
watch the window come forward with the payload logged. Then run `ToastSample.exe --clean` to remove the shortcut, the
scheme key, the AppUserModelId key and the toasts.

## Desktop appearance

Opt-in: nothing here changes how your app draws unless you ask. `DesktopAccent` and `DesktopAppearance` read what
the desktop publishes, once, in a static initialiser on first use (on whatever thread touches it first, so touch it at startup rather
than on the render thread), so a change made in Windows
settings shows at the next launch.

- `DesktopAccent.Ramp` is the accent ramp (HKCU `Explorer\Accent\AccentPalette`: Light3, Light2, Light1, Base,
  Dark1, Dark2, Dark3, Extra). `DesktopAccent.For(darkChrome)` picks one of its own steps, never a lightened or
  darkened copy: **Light2 on dark chrome** and **Dark1 on light chrome**, the steps that keep their contrast
  against each background where the Base accent often does not.
- `DesktopAppearance.Mode` is the app mode (HKCU `Themes\Personalize\AppsUseLightTheme`): `AppMode.Light`,
  `Dark` or `Unknown`; `IsDark` is true only for `Dark`. Follow it by default and add your own override on top.
- Off Windows, or when the value is missing or unreadable, the accent is `null` and the mode `Unknown`, with a
  `Reason` for each and nothing thrown. `LogOnce(...)` says why, once.

```csharp
DesktopAccent.LogOnce(logger);
DesktopAppearance.LogOnce(logger);

var dark = userOverride ?? DesktopAppearance.IsDark;
var accent = DesktopAccent.For(darkChrome: dark) ?? myOwnDefaultAccent;
```

## Failure is never fatal

Every path out of a failed hand-off returns `false` rather than throwing, and `TryClaim` returns
`null` rather than throwing when it cannot gate at all. The caller's fallback is always "do the work
in this process", because an extra window is a poor outcome and a double-click that does nothing is
an unacceptable one.

## Licence

MIT.
