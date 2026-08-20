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
/// <para><b>The pipe is the lock.</b> A named pipe with a single server instance can only be
/// created once, so <see cref="TryClaim"/> succeeding IS the claim, and the same object then
/// carries the hand-off traffic. One primitive means one lifetime to get right and no
/// abandoned-mutex case to reason about.</para>
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

    private readonly NamedPipeServerStream _server;
    private readonly ConcurrentQueue<HandoffRequest> _incoming = new();
    private readonly ILogger? _log;
    private Thread? _thread;
    private int _dropped;

    // A flag rather than a token because nothing waits on one: the accept thread is woken by a
    // connection (see Dispose), and this is what tells it that connection was ours.
    private volatile bool _stopping;

    private InstanceGate(NamedPipeServerStream server, string channel, ILogger? log)
    {
        _server = server;
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

        NamedPipeServerStream server;
        try
        {
            // maxNumberOfServerInstances 1 is what makes creation exclusive, and therefore what
            // makes this the primacy test rather than merely a transport.
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
            return null;
        }
        catch (Exception ex)
        {
            // Anything else (an unsupported platform, a permissions problem) means we cannot gate.
            // Running without one is correct: the app opens its own window.
            log?.LogDebug(ex, "Could not claim instance channel {Channel}; continuing ungated", channel);
            return null;
        }

        var gate = new InstanceGate(server, channel, log);
        gate._thread = new Thread(gate.AcceptLoop)
        {
            IsBackground = true,
            Name = "instance-gate",
        };
        gate._thread.Start();
        return gate;
    }

    /// <summary>
    /// Hand <paramref name="payload"/> to the instance holding <paramref name="channel"/>, and
    /// grant it the right to raise its window. Returns false if nobody answered in time, in which
    /// case the caller should do the work itself.
    /// </summary>
    public static bool TryHandOff(string channel, string payload, TimeSpan timeout, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(payload);

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

        while (!_stopping)
        {
            try
            {
                _server.WaitForConnection();
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
            catch (Exception ex) when (!_stopping)
            {
                _log?.LogDebug(ex, "Instance gate connection failed on {Channel}", Channel);
            }
            finally
            {
                try
                {
                    if (_server.IsConnected)
                    {
                        _server.Disconnect();
                    }
                }
                catch (Exception)
                {
                    // A disconnect that fails during teardown has nothing left to protect.
                }
            }
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
