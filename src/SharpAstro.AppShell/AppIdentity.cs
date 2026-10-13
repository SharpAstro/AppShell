using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpAstro.AppShell;

/// <summary>What <see cref="AppIdentity.Claim"/> found, or did, at the Start menu shortcut that names the app.</summary>
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
    /// name the app. Never throws for a shell that refuses: an app without a name is still an app.</returns>
    public static StartMenuShortcut Claim(string appId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!OperatingSystem.IsWindows())
        {
            return StartMenuShortcut.None;
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

        return EnsureShortcut(Path.Combine(programs, displayName + ".lnk"), appId, target);
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

    // LibraryImport, not DllImport: the marshalling is source-generated, so this stays AOT-clean.
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [SupportedOSPlatform("windows")]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

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
