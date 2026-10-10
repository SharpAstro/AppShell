using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SharpAstro.AppShell;

/// <summary>
/// One thing a later launch asked the running instance to do -- normally the path of a file the
/// user double-clicked in the shell.
///
/// <para>An EMPTY payload is a legitimate request meaning "activate only": a second launch of an
/// app with no file associations has nothing to open, but still wants the existing window in
/// front. Consumers should treat it as a raise with no document change.</para>
/// </summary>
public readonly record struct HandoffRequest(string Payload);

/// <summary>
/// Makes a second launch hand its work to an already-running instance instead of starting another
/// one.
///
/// <para>This exists because of the file association: once the shell opens a file type with an app,
/// every double-click is a fresh process, with its own GPU device, font atlas and caches. What the
/// user wanted was for the file to appear in the window that is already open.</para>
///
/// <para><b>The identity is the caller's choice, and that is the whole flexibility.</b> A gate is
/// claimed on a CHANNEL, and <see cref="ChannelFor"/> builds one from a scope plus an arbitrary
/// identity string. Pass an empty identity for the usual "one instance per application" behaviour.
/// Pass a normalised folder path (see <see cref="NormalizePathIdentity"/>) for "one instance per
/// open folder", where opening a file in a new folder gets a new window but opening one in a folder
/// already on screen activates that window. Neither policy is baked in here.</para>
///
/// <para><b>On Windows the pipe is the lock.</b> A named pipe with a single server instance can only be
/// created once, so <see cref="TryClaim"/> succeeding IS the claim, and the same object then
/// carries the hand-off traffic. One primitive means one lifetime to get right and no
/// abandoned-mutex case to reason about.</para>
///
/// <para><b>On Linux and macOS it cannot be, so a lock file is.</b> .NET's pipe there is a Unix-domain
/// socket, and its server unlinks whatever socket is at the path and binds its own, enforcing the
/// single instance only within one process (<c>NamedPipeServerStream.Unix.cs</c>, <c>SharedServer</c>).
/// So a second process took the name instead of being refused: every launch claimed the gate, a
/// hand-off reached the NEWEST window, and the older gate's dispose then unlinked the newer one's
/// socket. The claim is an exclusive advisory lock (<c>flock</c>, which .NET takes for
/// <see cref="FileShare.None"/>) on a file beside the socket, held for the gate's life; the kernel
/// releases it when its process dies, however it dies, so a crash leaves no stale claim behind, which
/// <see cref="PipeOptions.FirstPipeInstance"/> alone would (its bind fails on a dead holder's socket
/// file for good).</para>
///
/// <para><b>The accept loop gets its own thread, and the pipe is deliberately NOT
/// <see cref="PipeOptions.Asynchronous"/>.</b> An awaited accept resumes on a thread-pool worker,
/// and a desktop app of this kind saturates its own pool with decode and tessellation work -- so
/// the accept would queue behind that and the client would time out while the app was merely busy.
/// A busy app refusing hand-offs is exactly the stray window this class exists to prevent, and an
/// idle measurement never shows it.</para>
///
/// <para><b>Failure is never fatal.</b> Every path out of a failed hand-off returns false so the
/// caller can open the document in this process instead. An extra window is a poor outcome; a
/// double-click that does nothing is an unacceptable one.</para>
/// </summary>
public sealed class InstanceGate : IDisposable
{
    /// <summary>
    /// Bound on the queue of pending hand-offs. Reached only by something pathological (a script
    /// pushing paths in a loop); the alternative is unbounded growth on the consumer's heap.
    /// </summary>
    private const int MaxPendingHandoffs = 64;

    /// <summary>Bound on a single payload, so a malformed length cannot ask for a huge allocation.</summary>
    private const int MaxPayloadBytes = 64 * 1024;

    /// <summary>How many consecutive failed accepts mean the instance is wedged rather than unlucky.
    /// A client that connects and vanishes before the accept returns can leave the handle in a state
    /// where WaitForConnection throws ERROR_PIPE_CLOSING for ever, and Disconnect does not clear it.</summary>
    private const int WedgedAfter = 5;

    // Not readonly: a wedged instance is replaced, see AcceptLoop. Written only by the accept thread.
    private volatile NamedPipeServerStream _server;

    // The claim off Windows, held until after the server is disposed (see the class remarks); null on Windows.
    private readonly FileStream? _claim;
    private readonly ConcurrentQueue<HandoffRequest> _incoming = new();
    private readonly ILogger? _log;
    private Thread? _thread;
    private int _dropped;

    // A flag rather than a token because nothing waits on one: the accept thread is woken by a
    // connection (see Dispose), and this is what tells it that connection was ours.
    private volatile bool _stopping;

    private InstanceGate(NamedPipeServerStream server, FileStream? claim, string channel, ILogger? log)
    {
        _server = server;
        _claim = claim;
        Channel = channel;
        _log = log;
    }

    /// <summary>The pipe name this gate holds.</summary>
    public string Channel { get; }

    /// <summary>
    /// The pipe name for a scope and identity.
    ///
    /// <para>The hash covers the current user as well, because the pipe namespace is machine-wide
    /// rather than per-session and two people signed in to one machine must not hand documents to
    /// each other's windows. It also covers a wire version, so a future protocol change cannot be
    /// handed a message an older build would misread.</para>
    /// </summary>
    /// <param name="scope">Application identifier, e.g. the executable name. Appears in the pipe
    /// name in readable form (sanitised) so a stuck pipe can be recognised.</param>
    /// <param name="identity">What separates instances. Empty for one instance per application.</param>
    public static string ChannelFor(string scope, string identity = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var readable = Sanitize(scope);
        var material = $"{Environment.UserName}\u0000{WireVersion}\u0000{scope}\u0000{identity}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return $"{readable}.v{WireVersion}.{Convert.ToHexString(digest.AsSpan(0, 8))}";
    }

    private const int WireVersion = 1;

    /// <summary>
    /// Canonical form of a directory path for use as an identity: absolute, no trailing separator,
    /// and lower-cased on the platforms whose file systems are case-insensitive. Without this,
    /// <c>C:\Data</c> and <c>c:\data\</c> would claim two different channels for one folder and the
    /// second launch would open a redundant window.
    /// </summary>
    public static string NormalizePathIdentity(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // A root ("C:\", "/") trims to something odd, so put the separator back rather than let
        // "C:" and "C:\" differ.
        if (full.Length == 0 || (OperatingSystem.IsWindows() && full.EndsWith(':')))
        {
            full += Path.DirectorySeparatorChar;
        }

        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? full.ToLowerInvariant()
            : full;
    }

    /// <summary>
    /// Try to become the instance that owns <paramref name="channel"/>. Returns null when another
    /// process already holds it, which is the caller's signal to hand off instead.
    /// </summary>
    public static InstanceGate? TryClaim(string channel, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        FileStream? claim = null;
        NamedPipeServerStream server;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // The claim itself (see the class remarks); an IOException is another process holding it
                claim = new FileStream(ClaimPath(channel), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }

            // maxNumberOfServerInstances 1 is what makes creation exclusive on Windows, and therefore
            // what makes this the primacy test there rather than merely a transport. Elsewhere the
            // claim above is, and this server only replaces whatever socket a dead holder left.
            server = new NamedPipeServerStream(
                channel,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.None);
        }
        catch (IOException)
        {
            // The name is taken: somebody else is the instance for this identity.
            claim?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            // Anything else (an unsupported platform, a permissions problem) means we cannot gate.
            // Running without one is correct: the app opens its own window.
            claim?.Dispose();
            log?.LogDebug(ex, "Could not claim instance channel {Channel}; continuing ungated", channel);
            return null;
        }

        var gate = new InstanceGate(server, claim, channel, log);
        gate._thread = new Thread(gate.AcceptLoop)
        {
            IsBackground = true,
            Name = "instance-gate",
        };
        gate._thread.Start();
        return gate;
    }

    // Beside .NET's own socket for the channel, in the per-user temporary folder on macOS; on Linux the
    // channel's hash of the user's name keeps two users' claims apart in a shared /tmp
    private static string ClaimPath(string channel) => Path.Combine(Path.GetTempPath(), $"{channel}.claim");

    /// <summary>
    /// Hand <paramref name="payload"/> to the instance holding <paramref name="channel"/>, and
    /// grant it the right to raise its window. Returns false if nobody answered in time, in which
    /// case the caller should do the work itself.
    /// </summary>
    public static bool TryHandOff(string channel, string payload, TimeSpan timeout, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(payload);

        // Nobody there is answered NOW, not at the timeout. See IsKnownAbsent: the wait exists for a
        // holder that is busy, and spending it on a holder that does not exist is the common case.
        if (IsKnownAbsent(channel))
        {
            log?.LogDebug("No instance holds {Channel}", channel);
            return false;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", channel, PipeDirection.InOut);
            client.Connect((int)timeout.TotalMilliseconds);

            // The running instance identifies itself first, and this is the ONLY reason the pipe is
            // bidirectional. Windows will not let a background process take the foreground on its
            // own; the right has to be granted by a process that currently holds it, which is this
            // one -- the shell just launched it. Without this the other window would flash in the
            // taskbar and stay behind, which reads as the hand-off not working at all.
            Span<byte> header = stackalloc byte[4];
            if (!ReadExactly(client, header))
            {
                return false;
            }

            var holderPid = BitConverter.ToInt32(header);
            var granted = ForegroundActivation.AllowFor(holderPid);
            if (!granted)
            {
                // Logged rather than swallowed: the hand-off will still deliver and the document
                // will still open, but the window will flash its taskbar button instead of coming
                // forward, and this line is the only way to tell that apart from a broken raise.
                log?.LogDebug("Foreground grant to process {Pid} was refused; this process may not hold "
                    + "the foreground right (normal when launched by a script rather than the shell)", holderPid);
            }

            var bytes = Encoding.UTF8.GetBytes(payload);
            if (bytes.Length > MaxPayloadBytes)
            {
                log?.LogWarning("Hand-off payload of {Bytes} bytes exceeds the {Max} byte limit", bytes.Length, MaxPayloadBytes);
                return false;
            }

            client.Write(BitConverter.GetBytes(bytes.Length));
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (TimeoutException)
        {
            // Nobody is listening, or the holder is too busy to accept. Either way the caller opens
            // its own window; that is a worse outcome than a hand-off and a far better one than
            // nothing happening.
            log?.LogDebug("No instance answered {Channel} within {Timeout}", channel, timeout);
            return false;
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "Hand-off to {Channel} failed", channel);
            return false;
        }
    }

    /// <summary>
    /// Whether it is CERTAIN that nothing holds <paramref name="channel"/> -- false means "held, or
    /// cannot tell", so a caller still has to try.
    /// </summary>
    /// <remarks>
    /// <para><b>Why an existence check exists at all.</b>
    /// <see cref="NamedPipeClientStream.Connect(int)"/> does not distinguish "no holder" from "the
    /// holder is busy". It POLLS for the name to appear and gives up only at the timeout, which is
    /// right for a busy holder and completely wrong for an absent one -- and absent is the COMMON
    /// case, since every launch made while no other window is open takes it. The FITS viewer paid
    /// the full five seconds on every double-click of a file, before its window was even created,
    /// because it offers each document to an "empty window" channel that usually holds nobody.</para>
    ///
    /// <para><b>Windows publishes the pipe namespace as a filesystem</b>, so the distinction is one
    /// stat call. A name that could not BE a file name is left unprobed and reported as "cannot
    /// tell": a false negative here would skip a hand-off that would have worked, which is the one
    /// outcome worse than waiting. <see cref="ChannelFor"/> only ever produces safe names.</para>
    ///
    /// <para>Off Windows there is no equally reliable probe -- the .NET pipe path is an
    /// implementation detail -- so the answer is "cannot tell" and the timed connect runs exactly as
    /// it did before.</para>
    ///
    /// <para>Racy by nature, and harmlessly so: a holder appearing right after the probe means the
    /// caller opens its own window, which is this class's documented fallback for every failed
    /// hand-off.</para>
    /// </remarks>
    private static bool IsKnownAbsent(string channel)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        foreach (var c in channel)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return !File.Exists($@"\\.\pipe\{channel}");
    }

    /// <summary>
    /// Take the next hand-off, if any. Call this from whichever thread owns the UI; nothing here
    /// blocks it.
    /// </summary>
    public bool TryDequeue(out HandoffRequest request) => _incoming.TryDequeue(out request);

    private void AcceptLoop()
    {
        // One buffer for the life of the loop, not one per connection: a stackalloc inside the
        // loop is never reclaimed until the method returns, so a long-lived instance would leak
        // four bytes of stack per hand-off (CA2014).
        Span<byte> header = stackalloc byte[4];
        var failures = 0;

        while (!_stopping)
        {
            // A wedged handle cannot be recovered by disconnecting it, so replace it. This is the one
            // place the pipe NAME is released and retaken, which normally must never happen -- but the
            // alternative here is an instance that has stopped accepting for good, so a window of
            // microseconds in which another process could claim primacy is the better of the two. It is
            // reached only after WedgedAfter consecutive failures, so an unlucky connection cannot
            // trigger it.
            if (failures >= WedgedAfter)
            {
                if (!TryReplaceWedgedServer())
                {
                    return;
                }

                failures = 0;
            }

            try
            {
                _server.WaitForConnection();
                failures = 0;
                if (_stopping)
                {
                    break;
                }

                // Announce who we are so the client can grant us foreground rights before it sends
                // anything; see TryHandOff.
                _server.Write(BitConverter.GetBytes(Environment.ProcessId));
                _server.Flush();

                if (ReadExactly(_server, header))
                {
                    var length = BitConverter.ToInt32(header);
                    if (length == 0)
                    {
                        // Activate-only: a launch that has nothing to open still wants the
                        // existing window in front. Dropping this would make a whole-app gate
                        // claim primacy and then do nothing with it.
                        Enqueue(new HandoffRequest(string.Empty));
                    }
                    else if (length > 0 && length <= MaxPayloadBytes)
                    {
                        var buffer = new byte[length];
                        if (ReadExactly(_server, buffer))
                        {
                            Enqueue(new HandoffRequest(Encoding.UTF8.GetString(buffer)));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Shutdown is read off the FLAG, not inferred from the exception, and deliberately not
                // expressed as a filter. A filter that declines does not swallow the exception: it
                // leaves AcceptLoop and goes unhandled on this thread, which takes the whole process
                // down. Disposing while a connection is in flight is common enough under load to do
                // exactly that -- measured on Linux, 4 cores, 8 gates at a time, where it killed a
                // 400-round harness outright rather than failing a round.
                if (_stopping)
                {
                    return;
                }

                _log?.LogDebug(ex, "Instance gate connection failed on {Channel}", Channel);
                failures++;

                // Cheap insurance against a spin: if the wait itself is failing immediately rather
                // than a client misbehaving, this bounds it to 20/s instead of a burnt core. A
                // hand-off is human-paced, so the delay costs nothing anyone can perceive.
                Thread.Sleep(50);
            }
            finally
            {
                try
                {
                    // Unconditional, and this matters: IsConnected tracks the LOCAL handle and goes
                    // false as soon as a read hits end-of-stream, while the instance still needs an
                    // explicit Disconnect before it will accept another client. Gating this on
                    // IsConnected means a hand-off whose client gave up before writing leaves the
                    // instance connected for ever, and every later hand-off finds a listener that
                    // throws instead of accepting.
                    _server.Disconnect();
                }
                catch (Exception)
                {
                    // Was not connected, or teardown is already under way. Nothing left to protect.
                }
            }
        }
    }

    /// <summary>
    /// Rebuilds the server instance after it has stopped accepting. Returns false when the name
    /// cannot be retaken, which means somebody else now holds it and this gate is finished. Off
    /// Windows the claim is held throughout, so nobody can take the name in between.
    /// </summary>
    private bool TryReplaceWedgedServer()
    {
        _log?.LogWarning("Instance gate on {Channel} stopped accepting; rebuilding the listener", Channel);
        try
        {
            _server.Dispose();
            _server = new NamedPipeServerStream(Channel, PipeDirection.InOut,
                maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte, PipeOptions.None);
            return true;
        }
        catch (Exception ex)
        {
            // Somebody claimed the name in the window above, or the OS will not give it back. Ending
            // the loop is right: this process is no longer the holder, and pretending otherwise would
            // leave later launches handing documents to a listener that is not there.
            _log?.LogWarning(ex, "Instance gate on {Channel} could not be rebuilt; no longer listening", Channel);
            return false;
        }
    }

    private void Enqueue(HandoffRequest request)
    {
        if (_incoming.Count >= MaxPendingHandoffs)
        {
            // Logged rather than silently discarded: a queue that fills means something upstream is
            // wrong, and a dropped double-click is invisible otherwise.
            _log?.LogWarning("Instance gate queue is full; dropped hand-off {Payload} ({Dropped} total)",
                request.Payload, Interlocked.Increment(ref _dropped));
            return;
        }

        _incoming.Enqueue(request);
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n <= 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;

        // The accept thread is blocked in WaitForConnection, and there is no way to cancel that on
        // a synchronous pipe. Connecting to ourselves wakes it; the flag above is what tells it the
        // connection was ours rather than a real hand-off.
        try
        {
            using var wake = new NamedPipeClientStream(".", Channel, PipeDirection.InOut);
            wake.Connect(250);
        }
        catch (Exception)
        {
            // Already gone, or never started. Either way the thread is not blocked on us.
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _server.Dispose();

        // Only now: disposing the server unlinks the socket path, which must not happen to a socket a
        // newer holder has already bound there
        _claim?.Dispose();
    }

    private static string Sanitize(string value)
    {
        Span<char> buffer = stackalloc char[Math.Min(value.Length, 48)];
        var length = 0;
        foreach (var c in value)
        {
            if (length == buffer.Length)
            {
                break;
            }

            buffer[length++] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-';
        }

        return length == 0 ? "app" : new string(buffer[..length]);
    }
}
