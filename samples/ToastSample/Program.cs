using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpAstro.AppShell;

namespace ToastSample;

internal static class Program
{
    public const string AppId = "SharpAstro.AppShell.ToastSample";
    public const string HeadlessAppId = "SharpAstro.AppShell.ToastSample.Headless";
    public const string DisplayName = "AppShell Toast Sample";
    public const string Scheme = "appshell-toast-sample";
    public const string Group = "toast-sample";

    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("ToastSample is Windows-only: desktop notifications are only implemented on Windows.");
            return 0;
        }

        return Run(args);
    }

    [SupportedOSPlatform("windows")]
    private static int Run(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "";
        Sample.Log($"ToastSample started: pid {Environment.ProcessId}, args [{string.Join(" ", args)}]");
        return mode switch
        {
            "--clean" => Sample.Clean(),
            "--self-test" => Sample.Window(selfTest: true, null),
            _ => Sample.Window(selfTest: false, mode),
        };
    }
}

[SupportedOSPlatform("windows")]
internal static unsafe class Sample
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "ToastSample.log");
    private static nint _hwnd, _edit;
    private static InstanceGate? _gate;
    private static HwndWindow? _window;

    // self-test state
    private static bool _selfTest, _failed, _cannot;
    private static string? _clickPayload;
    private static long _deadline;
    private static int _exitCode = -1;
    private static StartMenuShortcut _claimResult;

    /// <summary>Every step goes to the window, to stdout and to a log file beside the exe.</summary>
    public static void Log(string line)
    {
        var text = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {line}";
        try
        {
            Console.Out.WriteLine(text);
            Console.Out.Flush();
        }
        catch (IOException)
        {
            // a WinExe started without redirected output has no console to write to
        }

        try
        {
            File.AppendAllText(LogPath, text + "\r\n");
        }
        catch (IOException)
        {
            // another instance has the file at this instant: the window and stdout still have the line
        }

        if (_edit != 0)
        {
            var wide = text + "\r\n";
            fixed (char* p = wide)
            {
                Win32.SendMessage(_edit, Win32.EM_SETSEL, nuint.MaxValue, -1);
                Win32.SendMessage(_edit, Win32.EM_REPLACESEL, 0, (nint)p);
            }
        }
    }

    // ---- the window ------------------------------------------------------------------------------------------------

    public static int Window(bool selfTest, string? arg)
    {
        _selfTest = selfTest;
        _claimResult = AppIdentity.Claim(Program.AppId, Program.DisplayName);
        Log($"claim: {_claimResult} (shortcut '{Program.DisplayName}.lnk')");
        Log($"scheme: {LaunchScheme.Register(Program.Scheme, Program.AppId, Program.DisplayName)} ({Program.Scheme}:)");
        var channel = InstanceGate.ChannelFor("toast-sample");
        _gate = InstanceGate.TryClaim(channel);
        string? launchPayload = null;
        var isLink = arg is not null && LaunchScheme.TryRead(arg, Program.Scheme, out launchPayload);

        if (_gate is null)
        {
            if (selfTest)
            {
                Log("FAIL another ToastSample instance holds the gate; close it and run again");
                return 1;
            }

            var handed = InstanceGate.TryHandOff(channel, isLink ? launchPayload! : "", TimeSpan.FromSeconds(3));
            Log(handed ? $"another instance holds the gate: handed {(isLink ? $"payload '{launchPayload}'" : "an empty request")} to it, exiting without a window"
                       : "another instance holds the gate but did not answer in time; exiting");
            return handed ? 0 : 1;
        }

        using (_gate)
        {
            if (!CreateWindow())
            {
                return 1;
            }

            _window = new HwndWindow(_hwnd);
            Log("window open: 'AppShell toast sample'");
            Win32.SetTimer(_hwnd, 1, 100, 0); // a timer, not a spin: it drains the gate's queue every 100 ms

            if (selfTest)
            {
                SelfTest();
            }
            else
            {
                if (isLink)
                {
                    Log($"started by a toast click: payload '{launchPayload}'");
                }

                var r = DesktopNotification.Show("AppShell toast sample", "Minimise or cover this window, then click this toast.",
                    new DesktopNotificationOptions { Tag = "sample", Group = Program.Group, LaunchPayload = "hello-from-toast", Scheme = Program.Scheme });
                Log($"toast shown: {r.Status} {r.Reason}");
            }

            while (_exitCode < 0 && Win32.GetMessage(out var msg, 0, 0, 0) > 0)
            {
                Win32.TranslateMessage(in msg);
                Win32.DispatchMessage(in msg);
            }
        }

        return _exitCode < 0 ? 0 : _exitCode;
    }

    private static bool CreateWindow()
    {
        var instance = Win32.GetModuleHandle(0);
        fixed (char* name = "ToastSampleWindow")
        {
            var wc = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WndProc,
                Instance = instance,
                Cursor = Win32.LoadCursor(0, 32512), // IDC_ARROW
                Background = 6, // COLOR_WINDOW + 1
                ClassName = (nint)name,
            };
            if (Win32.RegisterClassEx(in wc) == 0)
            {
                Log($"FAIL RegisterClassExW: {Marshal.GetLastPInvokeError()}");
                return false;
            }
        }

        _hwnd = Win32.CreateWindowEx(0, "ToastSampleWindow", "AppShell toast sample", Win32.WS_OVERLAPPEDWINDOW,
            Win32.CW_USEDEFAULT, Win32.CW_USEDEFAULT, 900, 520, 0, 0, instance, 0);
        if (_hwnd == 0)
        {
            Log($"FAIL CreateWindowExW: {Marshal.GetLastPInvokeError()}");
            return false;
        }

        Win32.ShowWindow(_hwnd, Win32.SW_SHOWNORMAL);
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_CREATE:
                _edit = Win32.CreateWindowEx(Win32.WS_EX_CLIENTEDGE, "EDIT", "",
                    Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_VSCROLL | Win32.ES_MULTILINE | Win32.ES_AUTOVSCROLL | Win32.ES_READONLY,
                    0, 0, 100, 100, hwnd, 0, 0, 0);
                return 0;
            case Win32.WM_SIZE:
                if (_edit != 0)
                {
                    Win32.GetClientRect(hwnd, out var r);
                    Win32.MoveWindow(_edit, 0, 0, r.Right, r.Bottom, true);
                }

                return 0;
            case Win32.WM_TIMER:
                try
                {
                    OnTimer();
                }
                catch (Exception e)
                {
                    Log($"FAIL timer: {e.GetType().Name}: {e.Message}");
                }

                return 0;
            case Win32.WM_DESTROY:
                Win32.PostQuitMessage(0);
                return 0;
        }

        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // Drains what a later launch handed to this instance, raises the window and, in the self-test, ends the wait.
    private static void OnTimer()
    {
        if (_gate is { } gate && gate.TryDequeue(out var request))
        {
            Log($"hand-off received: payload '{request.Payload}'");
            _window!.Activate();
            var foreground = Win32.GetForegroundWindow() == _hwnd;
            Log(foreground
                ? "raise: the window took the foreground"
                : "raise: the window did NOT take the foreground (a script-started process holds no foreground right; not a failure)");
            if (_selfTest && request.Payload == _clickPayload)
            {
                Step("launch link through the shell reached the running instance with the same payload", true, $" ('{request.Payload}')");
                Finish();
                return;
            }
        }

        if (_selfTest && _clickPayload is not null && Environment.TickCount64 > _deadline)
        {
            Step("launch link through the shell reached the running instance within 15 s", false);
            Finish();
        }
    }

    private static void Finish()
    {
        Win32.KillTimer(_hwnd, 1);
        _exitCode = _cannot ? 2 : _failed ? 1 : 0;
        Log(_exitCode == 0 ? "SELF-TEST PASSED" : _exitCode == 2 ? "SELF-TEST CANNOT RUN: this machine cannot show toasts" : "SELF-TEST FAILED");
        Win32.DestroyWindow(_hwnd);
    }

    // ---- the self-test ---------------------------------------------------------------------------------------------

    private static bool Step(string what, bool ok, string detail = "")
    {
        Log($"{(ok ? "PASS" : "FAIL")} {what}{detail}");
        if (!ok)
        {
            _failed = true;
        }

        return ok;
    }

    private static int Count(string? appId, string tag)
    {
        var r = DesktopNotification.GetDelivered(appId);
        return r.IsSuccess ? r.Toasts.Count(t => t.Tag == tag && t.Group == Program.Group) : -1;
    }

    private static void SelfTest()
    {
        var created = _claimResult == StartMenuShortcut.Created;
        Log($"self-test: identity 1 is the claimed app id '{Program.AppId}' (Start menu shortcut, {_claimResult})");
        Log($"self-test: identity 2 is '{Program.HeadlessAppId}', given only by AppIdentity.Register: {AppIdentity.Register(Program.HeadlessAppId, Program.DisplayName + " (headless)")} (registry key, no shortcut)");

        // both identities always run, so one that cannot show toasts does not hide what the other does
        RunIdentity("claimed", null, created);
        RunIdentity("registry", Program.HeadlessAppId, false);

        // the click: the shell resolves the scheme as a click on the toast does, and starts a second copy of this exe
        _clickPayload = "self-test-" + Guid.NewGuid().ToString("N")[..8];
        var link = LaunchScheme.Link(Program.Scheme, _clickPayload);
        Log($"self-test: simulating a click: starting '{link}' through the shell");
        _deadline = Environment.TickCount64 + 15_000;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Step("start the launch link through the shell", false, $" ({e.Message})");
            Finish();
        }
    }

    // Normal, replace by tag as Reminder, Urgent, then withdraw both: for one app id. False when toasts cannot be shown at all.
    private static bool RunIdentity(string label, string? appId, bool shortcutJustCreated)
    {
        var tagA = $"self-test-{label}-A";
        var tagB = $"self-test-{label}-B";
        DesktopNotificationOptions Options(string tag, ToastUrgency urgency, string? payload = null) =>
            new() { Tag = tag, Group = Program.Group, AppId = appId, Urgency = urgency, LaunchPayload = payload, Scheme = payload is null ? null : Program.Scheme };

        var probe = DesktopNotification.Probe(appId);
        if (probe.Status is DesktopNotificationStatus.NoIdentity or DesktopNotificationStatus.Disabled)
        {
            Log($"CANNOT {label}: toasts are not available ({probe.Status}): {probe.Reason}");
            if (shortcutJustCreated)
            {
                Log($"NOTE {label}: the shortcut was created by this very run; the shell may not know the app id yet. Not retried here: run --self-test again.");
            }

            _cannot = true;
            return false;
        }

        var shown = DesktopNotification.Show($"Toast sample ({label})", "Normal toast A", Options(tagA, ToastUrgency.Normal, label == "claimed" ? "self-test-toast" : null));
        if (shown.Status is DesktopNotificationStatus.NoIdentity or DesktopNotificationStatus.Disabled)
        {
            Log($"CANNOT {label}: Show answered {shown.Status}: {shown.Reason}");
            _cannot = true;
            return false;
        }

        Step($"{label}: Show A (Normal) answers success", shown.IsSuccess, shown.IsSuccess ? "" : $" ({shown.Status}: {shown.Reason})");
        var count = Count(appId, tagA);
        if (count == 0 && shortcutJustCreated)
        {
            Log($"NOTE {label}: the first toast right after the shortcut was created was dropped (delivered read found none); not retried");
        }

        Step($"{label}: delivered read finds A exactly once", count == 1, $" (found {count})");

        var again = DesktopNotification.Show($"Toast sample ({label})", "Same tag A again, as a Reminder", Options(tagA, ToastUrgency.Reminder));
        Step($"{label}: Show A again (Reminder) answers success", again.IsSuccess, again.IsSuccess ? "" : $" ({again.Status}: {again.Reason})");
        count = Count(appId, tagA);
        Step($"{label}: the same tag replaced the earlier toast, A still exactly once", count == 1, $" (found {count})");

        var urgent = DesktopNotification.Show($"Toast sample ({label})", "Tag B, Urgent", Options(tagB, ToastUrgency.Urgent));
        Step($"{label}: Show B (Urgent) answers success", urgent.IsSuccess, urgent.IsSuccess ? "" : $" ({urgent.Status}: {urgent.Reason})");

        var wa = DesktopNotification.Withdraw(tagA, Program.Group, appId);
        var wb = DesktopNotification.Withdraw(tagB, Program.Group, appId);
        Step($"{label}: Withdraw A and B answer success", wa.IsSuccess && wb.IsSuccess, wa.IsSuccess && wb.IsSuccess ? "" : $" ({wa.Reason} {wb.Reason})");
        Step($"{label}: after the withdraw neither A nor B is delivered", Count(appId, tagA) == 0 && Count(appId, tagB) == 0);
        return true;
    }

    // ---- --clean ---------------------------------------------------------------------------------------------------

    public static int Clean()
    {
        foreach (var id in new[] { Program.AppId, Program.HeadlessAppId })
        {
            var delivered = DesktopNotification.GetDelivered(id);
            if (!delivered.IsSuccess)
            {
                Log($"clean: toasts of '{id}' not read ({delivered.Status}): {delivered.Reason}");
                continue;
            }

            foreach (var toast in delivered.Toasts)
            {
                var w = DesktopNotification.Withdraw(toast.Tag, toast.Group, id);
                Log($"clean: withdrew toast '{toast.Tag}' of '{id}': {w.Status}");
            }

            Log($"clean: {delivered.Toasts.Count} toast(s) of '{id}' withdrawn");
        }

        var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Program.DisplayName + ".lnk");
        if (!File.Exists(lnk))
        {
            Log("clean: no Start menu shortcut");
        }
        else if (AppIdentity.TryReadShortcut(lnk, out var shortcutId, out _) && shortcutId == Program.AppId)
        {
            File.Delete(lnk);
            Log($"clean: deleted the Start menu shortcut {lnk}");
        }
        else
        {
            Log($"clean: left {lnk} alone: it does not carry the sample's app id");
        }

        var registry = new UserRegistry();
        var schemeKey = LaunchScheme.KeyOf(Program.Scheme);
        if (registry.Read(schemeKey, LaunchScheme.AppIdValue) == Program.AppId)
        {
            registry.DeleteTree(schemeKey);
            Log($"clean: deleted HKCU\\{schemeKey}");
        }
        else
        {
            Log($"clean: HKCU\\{schemeKey} is absent or not the sample's; left alone");
        }

        var idKey = @"Software\Classes\AppUserModelId\" + Program.HeadlessAppId;
        registry.DeleteTree(idKey);
        Log($"clean: deleted HKCU\\{idKey}");
        Log("clean: done");
        return 0;
    }
}
