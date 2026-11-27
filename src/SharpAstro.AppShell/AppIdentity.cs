using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpAstro.AppShell;

/// <summary>What <see cref="AppIdentity.Claim(string, string)"/> found, or did, at the Start menu shortcut that names the app.</summary>
public enum StartMenuShortcut
{
    /// <summary>Nothing was done: not Windows, or the process's app id could not be set.</summary>
    None,

    /// <summary>The shortcut was there already, carrying the app id, and its target exists.</summary>
    Present,

    /// <summary>There was none; one was written, pointing at this executable.</summary>
    Created,

    /// <summary>The app's shortcut pointed at an executable that is gone (the app was moved); it now points at
    /// this one.</summary>
    Repointed,

    /// <summary>A shortcut of that name is there that does not carry the app id: someone else's, left as it is.
    /// Windows cannot name the app from it.</summary>
    Foreign,

    /// <summary>The shortcut could not be read or written.</summary>
    Failed,

    /// <summary>Nothing was done: the process runs from an app package (MSIX), whose manifest gives it its app id,
    /// name and icon already. See <see cref="AppIdentity.IsPackaged"/>.</summary>
    Packaged,
}

/// <summary>What <see cref="AppIdentity.Register(string, string, string?)"/> did at the app id's registry key.</summary>
public enum AppUserModelRegistration
{
    /// <summary>Nothing was done: not Windows.</summary>
    None,

    /// <summary>The key was there already with these values; nothing was written.</summary>
    Present,

    /// <summary>The key or a value of it was missing or different and was written.</summary>
    Written,

    /// <summary>The registry could not be read or written.</summary>
    Failed,

    /// <summary>Nothing was done: the process runs from an app package, whose manifest names it.</summary>
    Packaged,
}

/// <summary>
/// Who the app is to the Windows shell: the app id (an AppUserModelID) its windows and its media session carry,
/// and the Start menu shortcut that gives that id a name and an icon.
///
/// <para><b>Why an unpackaged app needs both.</b> Without an app id of its own a process is known by its
/// executable's file name, and the shell cannot turn a file name into an app: Windows' media flyout says
/// "Unknown app" for it. With an app id the shell looks for a shortcut that carries the same id, which is how
/// Chrome is "Google Chrome" there, and takes the shortcut's name and its target's icon. Packaged (MSIX) apps get
/// both from their manifest; this is the same thing for an app shipped as a zip.</para>
///
/// <para><b>Inside a package it does nothing.</b> The same executable often ships both ways, as a zip and in a
/// Store package. In the package, setting an app id of our own would take the windows away from the package's
/// entry (the taskbar would group them under a second identity, apart from the pinned tile), and the shortcut
/// would be a second Start entry beside the package's own; where Windows redirects a packaged app's new AppData
/// files to a private per-app copy (which depends on the Windows version and the package's trust and
/// virtualization settings), the shell would not see it at all. So <see cref="Claim(string, string)"/> asks
/// <see cref="IsPackaged"/> first and returns <see cref="StartMenuShortcut.Packaged"/>.</para>
///
/// <para>The shortcut also puts the app in Start menu search. It is written once, beside the user's other
/// programs (<c>%APPDATA%\Microsoft\Windows\Start Menu\Programs</c>), and afterwards only read: a build run from
/// somewhere else does not take it over while the one it points at is still there.</para>
///
/// <para>On every other platform this is a no-op.</para>
/// </summary>
public static partial class AppIdentity
{
    /// <summary>
    /// Gives this process the app id <paramref name="appId"/> and makes sure a Start menu shortcut named
    /// <paramref name="displayName"/> carries it. Call it at startup, before the app shows any window: the shell
    /// takes a window's app id from its process when the window is created, so a window already open keeps the
    /// file-name identity.
    /// </summary>
    /// <param name="appId">A string of the form <c>Company.Product</c>, at most 128 characters, no spaces;
    /// the same on every launch.</param>
    /// <param name="displayName">The shortcut's file name, without <c>.lnk</c>: the name the shell shows.</param>
    /// <returns>What was found or done at the shortcut; <see cref="StartMenuShortcut.Present"/>,
    /// <see cref="StartMenuShortcut.Created"/> and <see cref="StartMenuShortcut.Repointed"/> mean the shell can
    /// name the app, and so does <see cref="StartMenuShortcut.Packaged"/>, from the package's manifest. Never throws
    /// for a shell that refuses: an app without a name is still an app.</returns>
    public static StartMenuShortcut Claim(string appId, string displayName)
        => Claim(appId, displayName, packaged: null);

    /// <summary>The body of <see cref="Claim(string, string)"/> with the package check as a parameter, so a test can
    /// say "packaged" and see that nothing is set or written. Null asks <see cref="IsPackaged"/>.</summary>
    internal static StartMenuShortcut Claim(string appId, string displayName, bool? packaged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!OperatingSystem.IsWindows())
        {
            return StartMenuShortcut.None;
        }

        if (packaged ?? IsPackaged)
        {
            ClaimedPackaged = true;
            return StartMenuShortcut.Packaged;
        }

        if (SetCurrentProcessExplicitAppUserModelID(appId) < 0)
        {
            return StartMenuShortcut.None;
        }

        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var target = Environment.ProcessPath;
        if (string.IsNullOrEmpty(programs) || string.IsNullOrEmpty(target))
        {
            return StartMenuShortcut.Failed;
        }

        var shortcut = EnsureShortcut(Path.Combine(programs, displayName + ".lnk"), appId, target);
        if (shortcut is StartMenuShortcut.Present or StartMenuShortcut.Created or StartMenuShortcut.Repointed)
        {
            ClaimedAppId = appId;
        }

        return shortcut;
    }

    /// <summary>
    /// The second way to an app identity, for a process with no Start menu shortcut and no windows (a headless node): writes
    /// <c>HKCU\Software\Classes\AppUserModelId\&lt;appId&gt;</c> with a <c>DisplayName</c>, and an <c>IconUri</c> when
    /// <paramref name="iconPath"/> is given, which is all Windows needs to show a toast under that id. It sets no app id on the
    /// process and writes no shortcut; <see cref="DesktopNotification"/> shows its toasts under the id it registered.
    /// Idempotent: only a value that is missing or differs is written. In a package, and off Windows, it does nothing.
    /// </summary>
    /// <param name="appId">A string of the form <c>Company.Product</c>, as for <see cref="Claim(string, string)"/>.</param>
    /// <param name="displayName">The name the toast is shown under.</param>
    /// <param name="iconPath">An image file (<c>.png</c> or <c>.ico</c>) for the toast's icon, or null for none. The shell
    /// reads <c>IconUri</c> as an image: an <c>.exe</c> there leaves the default icon.</param>
    /// <returns>What was done; never throws for the registry.</returns>
    public static AppUserModelRegistration Register(string appId, string displayName, string? iconPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!OperatingSystem.IsWindows())
        {
            return AppUserModelRegistration.None;
        }

        var result = Register(appId, displayName, iconPath, new UserRegistry(), packaged: null);
        if (result is AppUserModelRegistration.Present or AppUserModelRegistration.Written)
        {
            RegisteredAppId = appId;
        }

        return result;
    }

    /// <summary>The body of <see cref="Register(string, string, string?)"/> with the registry and the package check as
    /// parameters. Packaged is checked first, as <see cref="Claim(string, string)"/> does.</summary>
    internal static AppUserModelRegistration Register(string appId, string displayName, string? iconPath, IUserRegistry registry, bool? packaged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (packaged ?? IsPackaged)
        {
            return AppUserModelRegistration.Packaged;
        }

        try
        {
            var wrote = false;
            foreach (var (subKey, name, value) in AppIdValues(appId, displayName, iconPath))
            {
                if (registry.Read(subKey, name) != value)
                {
                    registry.Write(subKey, name, value);
                    wrote = true;
                }
            }

            return wrote ? AppUserModelRegistration.Written : AppUserModelRegistration.Present;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return AppUserModelRegistration.Failed;
        }
    }

    /// <summary>What the registry identity holds: (key, value name, value); the icon only when one is given. Pure.</summary>
    internal static IReadOnlyList<(string SubKey, string Name, string Value)> AppIdValues(string appId, string displayName, string? iconPath)
    {
        var key = @"Software\Classes\AppUserModelId\" + appId;
        var values = new List<(string, string, string)> { (key, "DisplayName", displayName) };
        if (!string.IsNullOrEmpty(iconPath))
        {
            values.Add((key, "IconUri", iconPath));
        }

        return values;
    }

    /// <summary>The app id of a successful <see cref="Register(string, string, string?)"/>, or null.
    /// <see cref="DesktopNotification"/> resolves it after the claimed one.</summary>
    internal static string? RegisteredAppId { get; private set; }

    /// <summary>The app id of a <see cref="Claim(string, string)"/> that the shell can name the app from
    /// (<see cref="StartMenuShortcut.Present"/>, <see cref="StartMenuShortcut.Created"/> or
    /// <see cref="StartMenuShortcut.Repointed"/>), or null when there was none. What <see cref="DesktopNotification"/>
    /// shows its toasts under.</summary>
    internal static string? ClaimedAppId { get; private set; }

    /// <summary>True once a <see cref="Claim(string, string)"/> found the process packaged
    /// (<see cref="StartMenuShortcut.Packaged"/>): its toasts then go through the default notifier.</summary>
    internal static bool ClaimedPackaged { get; private set; }

    /// <summary>The app id this process carries (<c>GetCurrentProcessExplicitAppUserModelID</c>), or null when it
    /// has none or this is not Windows.</summary>
    internal static unsafe string? ExplicitAppId()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        nint id;
        if (GetCurrentProcessExplicitAppUserModelID(&id) < 0 || id == 0)
        {
            return null;
        }

        try
        {
            var text = new string((char*)id);
            return text.Length == 0 ? null : text;
        }
        finally
        {
            CoTaskMemFree(id);
        }
    }

    /// <summary>What to do at a shortcut path, given what is there. Kept apart from the shell so the policy can be
    /// tested without one.</summary>
    /// <param name="existing">The shortcut found there (its app id and target, either possibly null), or null for
    /// none.</param>
    internal static ShortcutAction Decide((string? AppId, string? Target)? existing, string appId, Func<string, bool> fileExists)
    {
        if (existing is not { } found)
        {
            return ShortcutAction.Create;
        }

        // A shortcut by the same name that is not ours (a user's own, another app's) is not ours to rewrite.
        if (!string.Equals(found.AppId, appId, StringComparison.Ordinal))
        {
            return ShortcutAction.LeaveAlone;
        }

        // Ours, pointing at nothing: the app was moved or reinstalled elsewhere. Ours, pointing at another copy
        // that is still there (an installed one, while a development build runs): that copy keeps it.
        return found.Target is { Length: > 0 } t && fileExists(t) ? ShortcutAction.Keep : ShortcutAction.Repoint;
    }

    internal enum ShortcutAction
    {
        Create,
        Keep,
        Repoint,
        LeaveAlone,
    }

    [SupportedOSPlatform("windows")]
    internal static StartMenuShortcut EnsureShortcut(string path, string appId, string target)
    {
        try
        {
            using var com = ComScope.Enter();
            (string? AppId, string? Target)? existing = null;
            if (File.Exists(path))
            {
                existing = ShellLink.TryRead(path, out var foundId, out var foundTarget)
                    ? (foundId, foundTarget)
                    : (null, null); // unreadable: treated as someone else's
            }

            switch (Decide(existing, appId, File.Exists))
            {
                case ShortcutAction.Keep:
                    return StartMenuShortcut.Present;
                case ShortcutAction.LeaveAlone:
                    return StartMenuShortcut.Foreign;
                case ShortcutAction.Create:
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    ShellLink.Write(path, appId, target, load: false);
                    return StartMenuShortcut.Created;
                default:
                    ShellLink.Write(path, appId, target, load: true);
                    return StartMenuShortcut.Repointed;
            }
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException)
        {
            return StartMenuShortcut.Failed;
        }
    }

    /// <summary>The app id and target a shortcut carries, for the tests' round trip.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool TryReadShortcut(string path, out string? appId, out string? target)
    {
        using var com = ComScope.Enter();
        return ShellLink.TryRead(path, out appId, out target);
    }

    // The shell's link object (CLSID_ShellLink) through three of its interfaces, called by vtable slot: IShellLinkW
    // for the target, IPropertyStore for the app id (System.AppUserModel.ID) and IPersistFile to load and save. By
    // hand rather than through a COM wrapper generator, which for four calls would be the larger part of this
    // library.
    [SupportedOSPlatform("windows")]
    private static unsafe class ShellLink
    {
        private static readonly Guid ClsidShellLink = new("00021401-0000-0000-c000-000000000046");
        private static readonly Guid IidShellLinkW = new("000214f9-0000-0000-c000-000000000046");
        private static readonly Guid IidPersistFile = new("0000010b-0000-0000-c000-000000000046");
        private static readonly Guid IidPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

        // PKEY_AppUserModel_ID
        private static readonly PropertyKey AppIdKey = new(new Guid("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3"), 5);

        private const ushort VtLpwstr = 31;

        public static bool TryRead(string path, out string? appId, out string? target)
        {
            appId = null;
            target = null;
            var link = Create();
            nint file = 0, store = 0;
            try
            {
                file = Query(link, IidPersistFile);
                fixed (char* p = path)
                {
                    if (Call(file, 5, p, 0u) < 0) // IPersistFile::Load, STGM_READ
                    {
                        return false;
                    }
                }

                var buffer = stackalloc char[1024];
                // IShellLinkW::GetPath, no find data, the long path
                if (((delegate* unmanaged[Stdcall]<nint, char*, int, void*, uint, int>)Slot(link, 3))(link, buffer, 1024, null, 0) >= 0)
                {
                    target = new string(buffer);
                }

                store = Query(link, IidPropertyStore);
                var value = default(PropVariant);
                var key = AppIdKey;
                if (((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)Slot(store, 5))(store, &key, &value) >= 0)
                {
                    if (value.Vt == VtLpwstr && value.Pointer != 0)
                    {
                        appId = new string((char*)value.Pointer);
                    }

                    PropVariantClear(&value);
                }

                return true;
            }
            finally
            {
                Release(store);
                Release(file);
                Release(link);
            }
        }

        public static void Write(string path, string appId, string target, bool load)
        {
            var link = Create();
            nint file = 0, store = 0;
            try
            {
                file = Query(link, IidPersistFile);
                fixed (char* p = path)
                {
                    // Repointing keeps whatever else the shortcut carries; a new one starts empty.
                    if (load)
                    {
                        Check(Call(file, 5, p, 2u)); // IPersistFile::Load, STGM_READWRITE
                    }
                }

                fixed (char* t = target)
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)Slot(link, 20))(link, t)); // SetPath
                }

                fixed (char* d = Path.GetDirectoryName(target) ?? "")
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)Slot(link, 9))(link, d)); // SetWorkingDirectory
                }

                store = Query(link, IidPropertyStore);
                fixed (char* id = appId)
                {
                    var value = new PropVariant { Vt = VtLpwstr, Pointer = (nint)id };
                    var key = AppIdKey;
                    Check(((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)Slot(store, 6))(store, &key, &value)); // SetValue
                    Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(store, 7))(store)); // Commit
                }

                fixed (char* p = path)
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Slot(file, 6))(file, p, 1)); // IPersistFile::Save
                }
            }
            finally
            {
                Release(store);
                Release(file);
                Release(link);
            }
        }

        private static nint Create()
        {
            nint link;
            var clsid = ClsidShellLink;
            var iid = IidShellLinkW;
            Check(CoCreateInstance(&clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, &iid, &link));
            return link;
        }

        private static nint Query(nint unknown, Guid iid)
        {
            nint result;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(unknown, 0))(unknown, &iid, &result));
            return result;
        }

        private static int Call(nint file, int slot, char* path, uint mode)
            => ((delegate* unmanaged[Stdcall]<nint, char*, uint, int>)Slot(file, slot))(file, path, mode);

        private static void* Slot(nint unknown, int slot) => (*(void***)unknown)[slot];

        private static void Release(nint unknown)
        {
            if (unknown != 0)
            {
                ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(unknown, 2))(unknown);
            }
        }

        private static void Check(int hr)
        {
            if (hr < 0)
            {
                throw new COMException(null, hr);
            }
        }
    }

    // COM on the calling thread for the duration: initialized here when it was not, as a single-threaded apartment
    // (an app's UI thread, which is where a startup call runs), and left as it was when it already was, either way.
    [SupportedOSPlatform("windows")]
    private readonly ref struct ComScope
    {
        private readonly bool _initialized;

        private ComScope(bool initialized) => _initialized = initialized;

        public static ComScope Enter() => new(CoInitializeEx(0, 2 /* COINIT_APARTMENTTHREADED */) >= 0);

        public void Dispose()
        {
            if (_initialized)
            {
                CoUninitialize();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PropertyKey(Guid FormatId, uint PropertyId);

    // PROPVARIANT: the type tag, then (past three reserved words) a union as big as a DECIMAL. Only a string
    // pointer is read or written here.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public nint Pointer;
    }

    /// <summary>
    /// True when this process has a package identity: it runs from an app package (MSIX), or from a sparse package
    /// that gives an unpackaged executable one. The package's manifest then names the app, and
    /// <see cref="Claim(string, string)"/> leaves it alone. Always false off Windows. Asked of the system on each read; it is cheap.
    /// Deliberately biased: only the system's "no package" answer reads as false, so an unexpected error from the
    /// query also reads as true (see the note on <c>AppModelErrorNoPackage</c> for why that is the safer wrong answer).
    /// </summary>
    public static bool IsPackaged => OperatingSystem.IsWindows() && HasPackageIdentity();

    // GetCurrentPackageFullName with no buffer answers APPMODEL_ERROR_NO_PACKAGE for a process without a package
    // identity, and ERROR_INSUFFICIENT_BUFFER (the name would not fit in nothing) for one with it. Anything but the
    // first is read as packaged: wrongly calling a zip build packaged costs it its name in the shell, while wrongly
    // calling a packaged build unpackaged splits its windows from its own Start entry.
    private const int AppModelErrorNoPackage = 15700;

    [SupportedOSPlatform("windows")]
    private static unsafe bool HasPackageIdentity()
    {
        uint length = 0;
        return GetCurrentPackageFullName(&length, null) != AppModelErrorNoPackage;
    }

    [LibraryImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    private static unsafe partial int GetCurrentPackageFullName(uint* length, char* fullName);

    // LibraryImport, not DllImport: the marshalling is source-generated, so this stays AOT-clean.
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [SupportedOSPlatform("windows")]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [LibraryImport("shell32.dll")]
    [SupportedOSPlatform("windows")]
    private static unsafe partial int GetCurrentProcessExplicitAppUserModelID(nint* appId);

    [LibraryImport("ole32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial void CoTaskMemFree(nint memory);

    [LibraryImport("ole32.dll")]
    [SupportedOSPlatform("windows")]
    private static unsafe partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);

    [LibraryImport("ole32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial int CoInitializeEx(nint reserved, uint model);

    [LibraryImport("ole32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    [SupportedOSPlatform("windows")]
    private static unsafe partial int PropVariantClear(PropVariant* value);
}
