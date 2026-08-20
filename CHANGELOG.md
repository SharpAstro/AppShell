# Changelog

Release notes live here rather than beside the version number: the number is one line in
`Directory.Build.props` and nothing reads prose from there. Newest first, one section per
`MAJOR.MINOR`.

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
