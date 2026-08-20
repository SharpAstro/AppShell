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

`NormalizePathIdentity` is what makes the per-folder mode work: it folds a trailing separator, a
relative path and (on Windows and macOS only) a difference in case into one identity, so `C:\Data`
and `c:\data\` do not open two windows onto the same folder.

## Three things it gets right that are easy to get wrong

**The pipe is the lock.** A named pipe with a single server instance can only be created once, so
claiming it *is* the primacy test — there is no separate mutex, one lifetime to get right, and no
abandoned-mutex case.

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

## Failure is never fatal

Every path out of a failed hand-off returns `false` rather than throwing, and `TryClaim` returns
`null` rather than throwing when it cannot gate at all. The caller's fallback is always "do the work
in this process", because an extra window is a poor outcome and a double-click that does nothing is
an unacceptable one.

## Licence

MIT.
