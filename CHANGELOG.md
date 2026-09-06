# Changelog

Release notes live here rather than beside the version number: the number is one line in
`Directory.Build.props` and nothing reads prose from there. Newest first, one section per
`MAJOR.MINOR`.

## 1.1

- **`InstanceGate.TryHandOff` answers "nobody is there" at once instead of at the timeout.**
  `NamedPipeClientStream.Connect` does not distinguish an absent holder from a busy one: it polls
  for the name to appear and gives up only when the timeout expires. Waiting is right for a busy
  holder and completely wrong for an absent one -- which is the COMMON case, since every launch made
  while no other window is open takes that path. Measured in the FITS viewer, which offers each
  document to an "empty window" channel that usually holds nobody: **five seconds on every
  double-click of a file**, spent before the window was created, against 0.1 s for a bare launch.
  Windows publishes the pipe namespace as a filesystem, so the distinction is one stat call; a name
  that could not be a file name is left unprobed, and off Windows the timed connect is unchanged.
  No API change -- a consumer on `1.1.*` gets it on the next restore.
- `IActivatableWindow` + `WindowActivation.Activate()`: the window half of a hand-off, which until
  now every consumer had to write for itself. Both of the obvious spellings are wrong, in opposite
  directions: raising alone leaves a minimised window off-screen while holding input focus, and
  restoring before raising un-maximises a maximised one. So restore only when the window is actually
  minimised. A three-member interface rather than a dependency on a windowing toolkit, so this
  library stays usable from any of them.

## 1.0

First release.

- `InstanceGate`: a named-pipe single-instance gate whose identity is a caller-supplied string, so
  one type covers both "one instance per application" and "one instance per open folder". The pipe
  with a single server instance IS the lock, so there is no separate mutex and no abandoned-mutex
  case. The accept loop runs on its own thread over a deliberately synchronous pipe, because an
  awaited accept resumes on a thread-pool worker and an app that saturates its own pool with decode
  work would refuse hand-offs while merely busy.
- `InstanceGate.NormalizePathIdentity`: canonical folder identity, folding a trailing separator, a
  relative path and (only where the file system agrees) letter case.
- `ForegroundActivation.AllowFor`: the `AllowSetForegroundWindow` grant that lets the running
  instance raise its window. Without it the window flashes its taskbar button and stays behind,
  which reads as the hand-off failing. No-op off Windows.
