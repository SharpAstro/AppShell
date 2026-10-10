using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SharpAstro.AppShell.Tests;

/// <summary>
/// A second PROCESS holding a gate. A claim's exclusivity is a property of the OS between processes,
/// and inside one process .NET refuses a second server of one name by itself (its Unix server keeps a
/// per-process table), so a test of two gates in one process passes whether or not two processes
/// would be kept apart.
///
/// <para>The holder is this test assembly run again, <c>dotnet SharpAstro.AppShell.Tests.dll</c>, with
/// <see cref="ChannelVariable"/> naming the channel: the module initializer below claims it before the
/// test runner starts, prints <c>claimed</c> or <c>taken</c>, holds the claim until its standard input
/// closes, and exits without running a test.</para>
/// </summary>
internal sealed class GateHolderProcess : IDisposable
{
    private const string ChannelVariable = "APPSHELL_TESTS_HOLD_GATE";

    private readonly Process _process;

    private GateHolderProcess(Process process) => _process = process;

    /// <summary>What the holder answered: <c>claimed</c> or <c>taken</c>.</summary>
    public string Answer { get; private init; } = "";

    public static async Task<GateHolderProcess> StartAsync(string channel, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(DotnetHost(), $"\"{typeof(GateHolderProcess).Assembly.Location}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        info.Environment[ChannelVariable] = channel;
        var process = Process.Start(info) ?? throw new InvalidOperationException("The holder process did not start.");
        var answer = await process.StandardOutput.ReadLineAsync(cancellationToken) ?? "";
        return new GateHolderProcess(process) { Answer = answer };
    }

    /// <summary>Ends the holder as a crash does: nothing it would do on its way out runs.</summary>
    public async Task KillAsync(CancellationToken cancellationToken)
    {
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(10_000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        _process.Dispose();
    }

    // The SDK's own host when a test runs under it, else the one on PATH
    private static string DotnetHost()
        => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

    [ModuleInitializer]
    internal static void HoldWhenAsked()
    {
        if (Environment.GetEnvironmentVariable(ChannelVariable) is not { Length: > 0 } channel)
        {
            return;
        }

        using (var gate = InstanceGate.TryClaim(channel))
        {
            Console.Out.WriteLine(gate is null ? "taken" : "claimed");
            Console.Out.Flush();
            Console.In.ReadToEnd();
        }
        Environment.Exit(0);
    }
}
