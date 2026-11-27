using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpAstro.AppShell;

/// <summary>
/// Shows, withdraws and lists toasts by calling the WinRT toast API in process, ported from pilot's
/// <c>WinRtToastRunner</c> (SharpAstro/pilot#1378). The calls go through the raw ABI by function pointers on each
/// interface's vtable, as <see cref="AppIdentity"/> does for the shell link: AOT-safe, no CsWinRT, no
/// <c>-windows</c> target framework, no helper DLL. A vtable starts with IUnknown (3 slots) and IInspectable (3
/// more), so an interface's own methods begin at slot 6; every IID and slot here was checked against
/// <c>windows.ui.notifications.h</c> of the 10.0.26100.0 SDK.
///
/// <para>Every call runs synchronously on the caller's thread, with the apartment balanced (see
/// <see cref="Apartment"/>). A failed HRESULT becomes a result whose reason names the call and the HRESULT; nothing
/// escapes as an exception.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WinRtToast
{
    private static readonly Guid IidXmlDocument = new("F7F3A506-1E87-42D6-BCFB-B8C809FA5494");
    private static readonly Guid IidXmlDocumentIO = new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");
    private static readonly Guid IidToastNotificationFactory = new("04124B20-82C6-4229-B109-FD9ED4662B53");
    private static readonly Guid IidToastNotification2 = new("9DFB9FD1-143A-490E-90BF-B9FBA7132DE7");
    private static readonly Guid IidManagerStatics = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");
    private static readonly Guid IidManagerStatics2 = new("7AB93C52-0E48-4750-BA9D-1A4113981847");
    private static readonly Guid IidToastNotifier = new("75927B93-03F3-41EC-91D3-6E5BAC1B38E7");
    private static readonly Guid IidHistory = new("5CADDC63-01D3-4C97-986F-0533483FEE14");
    private static readonly Guid IidHistory2 = new("3BC3D253-2F31-4092-9129-8AD5ABF067DA");

    private const string XmlDocumentClass = "Windows.Data.Xml.Dom.XmlDocument";
    private const string ToastNotificationClass = "Windows.UI.Notifications.ToastNotification";
    private const string ManagerClass = "Windows.UI.Notifications.ToastNotificationManager";

    private const int RpcEChangedMode = unchecked((int)0x80010106);

    /// <summary>ERROR_NOT_FOUND as an HRESULT: what the notifier answers for an app id nothing registers.</summary>
    internal const int ErrorNotFound = unchecked((int)0x80070490);

    // Windows.UI.Notifications.NotificationSetting
    private static readonly string[] SettingNames =
    [
        "Enabled", "DisabledForApplication", "DisabledForUser", "DisabledByGroupPolicy", "DisabledByManifest",
    ];

    /// <summary>Whether a toast would show for <paramref name="appId"/> (null: the default notifier of a packaged
    /// process): the notifier's setting is read, nothing is shown.</summary>
    internal static DesktopNotificationResult Probe(string? appId) => Run(refs =>
        Notifier(refs, appId, out var refusal) == 0 ? refusal! : DesktopNotificationResult.Succeeded());

    /// <summary>Shows <paramref name="xml"/> as a toast, tagged and grouped when <paramref name="tag"/> is given
    /// (a toast of the same tag and group is replaced).</summary>
    internal static DesktopNotificationResult Show(string? appId, string xml, string? tag, string? group) => Run(refs =>
    {
        var notifier = Notifier(refs, appId, out var refusal);
        if (notifier == 0)
        {
            return refusal!;
        }

        var toast = Build(refs, xml, tag, group);
        Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(notifier, 6))(notifier, toast), "IToastNotifier.Show");
        return DesktopNotificationResult.Succeeded();
    });

    /// <summary>Withdraws the toast of <paramref name="tag"/> and <paramref name="group"/> from the notification
    /// centre. One that is not there is a success: Windows answers S_OK, and ERROR_NOT_FOUND is taken as the
    /// same.</summary>
    internal static DesktopNotificationResult Withdraw(string? appId, string tag, string group) => Run(refs =>
    {
        var history = History(refs);
        var hTag = refs.HString(tag);
        var hGroup = refs.HString(group);
        int hr;
        string call;
        if (appId is null)
        {
            call = "IToastNotificationHistory.RemoveGroupedTag";
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(history, 9))(history, hTag, hGroup);
        }
        else
        {
            call = "IToastNotificationHistory.RemoveGroupedTagWithId";
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)Slot(history, 8))(history, hTag, hGroup, refs.HString(appId));
        }

        if (hr != ErrorNotFound)
        {
            Check(hr, call);
        }

        return DesktopNotificationResult.Succeeded();
    });

    /// <summary>The toasts of the app still in the notification centre (tag and group as Windows holds them).</summary>
    internal static DesktopNotificationResult Delivered(string? appId) => Run(refs =>
    {
        var history = History(refs);
        var history2 = Qi(refs, history, IidHistory2, "IToastNotificationHistory.QueryInterface(IToastNotificationHistory2)");
        nint vector;
        if (appId is null)
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(history2, 6))(history2, &vector), "IToastNotificationHistory2.GetHistory");
        }
        else
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(history2, 7))(history2, refs.HString(appId), &vector),
                "IToastNotificationHistory2.GetHistoryWithId");
        }

        refs.Own(vector);
        uint size;
        Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(vector, 7))(vector, &size), "IVectorView.get_Size");
        var toasts = new List<DeliveredToast>((int)size);
        for (uint i = 0; i < size; i++)
        {
            nint item;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(vector, 6))(vector, i, &item), "IVectorView.GetAt");
            refs.Own(item);
            var toast2 = Qi(refs, item, IidToastNotification2, "IToastNotification.QueryInterface(IToastNotification2)");
            toasts.Add(new DeliveredToast(
                ReadString(refs, toast2, 7, "IToastNotification2.get_Tag"),
                ReadString(refs, toast2, 9, "IToastNotification2.get_Group")));
        }

        return DesktopNotificationResult.Succeeded(toasts);
    });

    /// <summary>
    /// Builds the notification of <paramref name="xml"/>, sets its tag and group, and reads both back; shows
    /// nothing. What a test calls to prove the interface ids and the vtable slots in milliseconds with no process
    /// and no toast. A failure comes back as a failed result.
    /// </summary>
    internal static (DesktopNotificationResult Result, string? Tag, string? Group) BuildAndReadBack(string xml, string tag, string group)
    {
        string? readTag = null, readGroup = null;
        var result = Run(refs =>
        {
            var toast = Build(refs, xml, tag, group);
            var toast2 = Qi(refs, toast, IidToastNotification2, "IToastNotification.QueryInterface(IToastNotification2)");
            readTag = ReadString(refs, toast2, 7, "IToastNotification2.get_Tag");
            readGroup = ReadString(refs, toast2, 9, "IToastNotification2.get_Group");
            return DesktopNotificationResult.Succeeded();
        });
        return (result, readTag, readGroup);
    }

    /// <summary>Creates the notifier for <paramref name="appId"/> and reads its setting; shows nothing. What a test
    /// calls to pin what Windows answers for an app id that nothing registers.</summary>
    internal static DesktopNotificationResult ReadSetting(string? appId) => Probe(appId);

    // Each entry point enters the apartment before its refs and leaves it after them: a using releases in reverse
    // order, so every pointer is released while the thread is still initialised. The catch is outside both.
    private static DesktopNotificationResult Run(Func<Refs, DesktopNotificationResult> body)
    {
        try
        {
            using var apartment = Apartment.Enter();
            using var refs = new Refs();
            return body(refs);
        }
        catch (ToastCallException e)
        {
            return DesktopNotificationResult.Failed(e.Message);
        }
    }

    // The notifier of appId (the default one for null) with its setting read, or 0 and the refusal. The read fails
    // with ERROR_NOT_FOUND for an app id that no Start menu shortcut and no registry key registers.
    private static nint Notifier(Refs refs, string? appId, out DesktopNotificationResult? refusal)
    {
        refusal = null;
        var statics = Factory(refs, ManagerClass, IidManagerStatics);
        nint notifier;
        int hr;
        string call;
        if (appId is null)
        {
            call = "IToastNotificationManagerStatics.CreateToastNotifier";
            hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(statics, 6))(statics, &notifier);
        }
        else
        {
            call = "IToastNotificationManagerStatics.CreateToastNotifierWithId";
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(statics, 7))(statics, refs.HString(appId), &notifier);
        }

        if (hr < 0)
        {
            refusal = hr == ErrorNotFound ? NotRegistered(appId, call, hr) : DesktopNotificationResult.Failed(Describe(call, hr));
            return 0;
        }

        refs.Own(notifier);

        int setting;
        hr = ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(notifier, 8))(notifier, &setting);
        if (hr < 0)
        {
            refusal = hr == ErrorNotFound
                ? NotRegistered(appId, "IToastNotifier.get_Setting", hr)
                : DesktopNotificationResult.Failed(Describe("IToastNotifier.get_Setting", hr));
            return 0;
        }

        if (setting != 0)
        {
            var name = (uint)setting < SettingNames.Length ? SettingNames[setting] : $"setting {setting}";
            refusal = DesktopNotificationResult.Disabled(
                $"Windows will not show this app's toasts: the notifier's setting is {name}.");
            return 0;
        }

        return notifier;
    }

    private static DesktopNotificationResult NotRegistered(string? appId, string call, int hr) =>
        DesktopNotificationResult.NoIdentity(
            $"the app id {(appId is null ? "of this process" : $"'{appId}'")} is not known to Windows' notification platform in this session ({Describe(call, hr)}). Either no Start menu shortcut and no registry key carries it, or the platform cannot serve this session at all (in a remote desktop session it answers the same for every app id, Windows' own included).");

    private static nint Build(Refs refs, string xml, string? tag, string? group)
    {
        var instance = Call(refs, "RoActivateInstance(XmlDocument)", o => RoActivateInstance(refs.HString(XmlDocumentClass), o));
        var io = Qi(refs, instance, IidXmlDocumentIO, "XmlDocument.QueryInterface(IXmlDocumentIO)");
        Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(io, 6))(io, refs.HString(xml)), "IXmlDocumentIO.LoadXml");
        var document = Qi(refs, instance, IidXmlDocument, "XmlDocument.QueryInterface(IXmlDocument)");

        var factory = Factory(refs, ToastNotificationClass, IidToastNotificationFactory);
        var toast = Call(refs, "IToastNotificationFactory.CreateToastNotification", o =>
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(factory, 6))(factory, document, o));

        if (tag is not null || group is not null)
        {
            var toast2 = Qi(refs, toast, IidToastNotification2, "IToastNotification.QueryInterface(IToastNotification2)");
            if (tag is not null)
            {
                Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(toast2, 6))(toast2, refs.HString(tag)), "IToastNotification2.put_Tag");
            }

            if (group is not null)
            {
                Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(toast2, 8))(toast2, refs.HString(group)), "IToastNotification2.put_Group");
            }
        }

        return toast;
    }

    private static nint History(Refs refs)
    {
        var statics2 = Factory(refs, ManagerClass, IidManagerStatics2);
        return Call(refs, "IToastNotificationManagerStatics2.get_History", o =>
            ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(statics2, 6))(statics2, o));
    }

    /// <summary>
    /// The thread's place in the MTA for one call, balanced: every successful <c>RoInitialize</c> (S_OK, or S_FALSE
    /// when the thread is already in the MTA, which still counts once) is matched by one <c>RoUninitialize</c> on
    /// dispose, so the apartment count does not climb with each toast. A thread that chose its own apartment
    /// (RPC_E_CHANGED_MODE, an app's single-threaded UI thread) can still call WinRT and needs no balance.
    /// </summary>
    private readonly struct Apartment : IDisposable
    {
        private readonly bool _entered;

        private Apartment(bool entered) => _entered = entered;

        public static Apartment Enter()
        {
            const int MtaType = 1; // RO_INIT_MULTITHREADED
            var hr = RoInitialize(MtaType);
            if (hr < 0 && hr != RpcEChangedMode)
            {
                Check(hr, "RoInitialize");
            }

            return new Apartment(hr >= 0);
        }

        public void Dispose()
        {
            if (_entered)
            {
                RoUninitialize();
            }
        }
    }

    private static nint Factory(Refs refs, string className, Guid iid) =>
        Call(refs, $"RoGetActivationFactory({className})", o =>
        {
            var id = iid;
            return RoGetActivationFactory(refs.HString(className), &id, o);
        });

    private static nint Qi(Refs refs, nint obj, Guid iid, string call) =>
        Call(refs, call, o =>
        {
            var id = iid;
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(obj, 0))(obj, &id, o);
        });

    private static string ReadString(Refs refs, nint obj, int slot, string call)
    {
        // An HSTRING, not a COM object: freed with WindowsDeleteString, never released through a vtable.
        nint h;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(obj, slot))(obj, &h), call);
        refs.OwnString(h);
        uint length;
        var raw = WindowsGetStringRawBuffer(h, &length);
        return new string(raw, 0, (int)length);
    }

    private delegate int Producer(nint* result);

    // Runs a call that yields a pointer; checks its HRESULT, and hands the pointer to refs so it is released when
    // the caller's using ends.
    private static nint Call(Refs refs, string call, Producer produce)
    {
        nint result;
        Check(produce(&result), call);
        refs.Own(result);
        return result;
    }

    private static void* Slot(nint obj, int index) => (*(void***)obj)[index];

    internal static string Describe(string call, int hr) => $"{call} failed: HRESULT 0x{hr:X8}";

    private static void Check(int hr, string call)
    {
        if (hr < 0)
        {
            throw new ToastCallException(Describe(call, hr), hr);
        }
    }

    // Internal to this class: every entry point catches it and answers with a failed result.
    private sealed class ToastCallException : Exception
    {
        public ToastCallException(string message, int hresult) : base(message) => HResult = hresult;
    }

    /// <summary>The pointers and strings one call made, released in reverse order when it ends (success or not).</summary>
    private sealed class Refs : IDisposable
    {
        private readonly List<(nint Pointer, bool IsString)> _owned = [];

        public void Own(nint pointer)
        {
            if (pointer != 0)
            {
                _owned.Add((pointer, false));
            }
        }

        public void OwnString(nint hstring)
        {
            if (hstring != 0)
            {
                _owned.Add((hstring, true));
            }
        }

        public nint HString(string text)
        {
            fixed (char* chars = text)
            {
                nint h;
                Check(WindowsCreateString(chars, (uint)text.Length, &h), "WindowsCreateString");
                _owned.Add((h, true));
                return h;
            }
        }

        public void Dispose()
        {
            for (var i = _owned.Count - 1; i >= 0; i--)
            {
                var (pointer, isString) = _owned[i];
                if (isString)
                {
                    WindowsDeleteString(pointer);
                }
                else
                {
                    ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(pointer, 2))(pointer);
                }
            }

            _owned.Clear();
        }
    }

    // LibraryImport, not DllImport: the marshalling is source-generated, so this stays AOT-clean.
    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(int initType);

    [LibraryImport("combase.dll")]
    private static partial void RoUninitialize();

    [LibraryImport("combase.dll")]
    private static partial int RoActivateInstance(nint activatableClassId, nint* instance);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, Guid* iid, nint* factory);

    [LibraryImport("combase.dll")]
    private static partial int WindowsCreateString(char* source, uint length, nint* hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static partial char* WindowsGetStringRawBuffer(nint hstring, uint* length);
}
