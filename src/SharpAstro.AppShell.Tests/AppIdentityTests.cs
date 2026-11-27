using Shouldly;
using Xunit;
using static SharpAstro.AppShell.AppIdentity;

namespace SharpAstro.AppShell.Tests;

/// <summary>
/// The Start menu shortcut that names the app: written when missing, kept while what it points at is there,
/// repointed when that is gone, and never taken from anyone else.
///
/// <para>The policy is a pure function and runs everywhere. The round trips write real shortcuts into a temporary
/// folder through the shell's link object, so they run on Windows only -- the Start menu itself is never touched,
/// nor this process's app id.</para>
/// </summary>
public class AppIdentityTests
{
    private const string AppId = "SharpAstro.AppShell.Tests";

    private const string AumidKey = @"Software\Classes\AppUserModelId\" + AppId;

    [Fact]
    public void Register_writes_the_display_name_and_no_icon_when_none_is_given()
    {
        var registry = new FakeRegistry();
        AppIdentity.Register(AppId, "Viewer", null, registry, packaged: false).ShouldBe(AppUserModelRegistration.Written);
        registry.Values.ShouldBe(new Dictionary<(string, string), string> { [(AumidKey, "DisplayName")] = "Viewer" });
    }

    [Fact]
    public void Register_writes_the_icon_only_when_given()
    {
        var registry = new FakeRegistry();
        AppIdentity.Register(AppId, "Viewer", @"C:\Apps\viewer.png", registry, packaged: false).ShouldBe(AppUserModelRegistration.Written);
        registry.Values[(AumidKey, "IconUri")].ShouldBe(@"C:\Apps\viewer.png");
        registry.Values[(AumidKey, "DisplayName")].ShouldBe("Viewer");
    }

    [Fact]
    public void Register_writes_only_what_differs()
    {
        var registry = new FakeRegistry();
        AppIdentity.Register(AppId, "Viewer", @"C:\a.png", registry, packaged: false);
        var writes = registry.Writes;
        AppIdentity.Register(AppId, "Viewer", @"C:\a.png", registry, packaged: false).ShouldBe(AppUserModelRegistration.Present);
        registry.Writes.ShouldBe(writes);

        AppIdentity.Register(AppId, "Viewer", @"C:\b.png", registry, packaged: false).ShouldBe(AppUserModelRegistration.Written);
        registry.Writes.ShouldBe(writes + 1);
        registry.Values[(AumidKey, "IconUri")].ShouldBe(@"C:\b.png");
    }

    [Fact]
    public void Register_in_a_package_writes_nothing()
    {
        var registry = new FakeRegistry();
        AppIdentity.Register(AppId, "Viewer", null, registry, packaged: true).ShouldBe(AppUserModelRegistration.Packaged);
        registry.Writes.ShouldBe(0);
    }

    [Fact]
    public void Register_that_the_registry_refuses_is_a_result()
        => AppIdentity.Register(AppId, "Viewer", null, new RefusingRegistry(), packaged: false).ShouldBe(AppUserModelRegistration.Failed);

    [Theory]
    [InlineData("", "n")]
    [InlineData(" ", "n")]
    [InlineData("id", "")]
    public void Register_validates_its_arguments(string appId, string name)
        => Should.Throw<ArgumentException>(() => AppIdentity.Register(appId, name));

    private sealed class RefusingRegistry : IUserRegistry
    {
        public string? Read(string subKey, string name) => null;

        public void Write(string subKey, string name, string value) => throw new UnauthorizedAccessException();

        public void DeleteTree(string subKey) => throw new UnauthorizedAccessException();
    }

    [Fact]
    public void No_shortcut_is_created()
        => Decide(null, AppId, _ => true).ShouldBe(ShortcutAction.Create);

    [Fact]
    public void Ours_pointing_at_something_that_is_there_is_kept()
    {
        // Including a copy other than the one running: a development build must not take the installed app's
        // shortcut over.
        Decide((AppId, @"C:\Apps\Viewer\viewer.exe"), AppId, _ => true).ShouldBe(ShortcutAction.Keep);
    }

    [Fact]
    public void Ours_pointing_at_nothing_is_repointed()
    {
        Decide((AppId, @"C:\Old\viewer.exe"), AppId, _ => false).ShouldBe(ShortcutAction.Repoint);
        Decide((AppId, null), AppId, _ => true).ShouldBe(ShortcutAction.Repoint);
    }

    [Fact]
    public void Someone_elses_is_left_alone()
    {
        // A user's own shortcut of the same name (no app id), another app's, and one that could not be read.
        Decide((null, @"C:\Apps\Viewer\viewer.exe"), AppId, _ => true).ShouldBe(ShortcutAction.LeaveAlone);
        Decide(("Other.App", @"C:\Apps\Viewer\viewer.exe"), AppId, _ => false).ShouldBe(ShortcutAction.LeaveAlone);
        Decide((null, null), AppId, _ => false).ShouldBe(ShortcutAction.LeaveAlone);
    }

    [Fact]
    public void A_test_host_has_no_package_identity()
    {
        // The test host is an unpackaged executable on every OS, so this is the answer Claim must see before it
        // sets an app id; off Windows it is false without asking.
        IsPackaged.ShouldBeFalse();
    }

    [Fact]
    public void Claim_inside_a_package_returns_Packaged_and_touches_neither_app_id_nor_shortcut()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = "AppShellPackagedTest-" + Guid.NewGuid().ToString("N");
        var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), name + ".lnk");
        try
        {
            Claim("SharpAstro.AppShell.Tests.Packaged", name, packaged: true).ShouldBe(StartMenuShortcut.Packaged);

            File.Exists(link).ShouldBeFalse();
            ProcessAppId().ShouldBeNull();
        }
        finally
        {
            File.Delete(link);
        }
    }

    // The process's explicit app id, or null when none is set (the test host never sets one).
    private static string? ProcessAppId()
    {
        if (GetCurrentProcessExplicitAppUserModelID(out var id) < 0 || id == 0)
        {
            return null;
        }

        try
        {
            return System.Runtime.InteropServices.Marshal.PtrToStringUni(id);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(id);
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int GetCurrentProcessExplicitAppUserModelID(out nint appId);

    [Fact]
    public void Packaged_is_appended_so_the_earlier_values_keep_their_numbers()
    {
        // A caller built against 1.4 compares against these values; adding one must not move them.
        ((int)StartMenuShortcut.None).ShouldBe(0);
        ((int)StartMenuShortcut.Failed).ShouldBe(5);
        ((int)StartMenuShortcut.Packaged).ShouldBe(6);
    }

    [Fact]
    public void A_written_shortcut_carries_the_app_id_and_the_target()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var target = dir.File("viewer.exe");
        var link = Path.Combine(dir.Path, "Start Menu", "Viewer.lnk");

        EnsureShortcut(link, AppId, target).ShouldBe(StartMenuShortcut.Created);

        TryReadShortcut(link, out var appId, out var readTarget).ShouldBeTrue();
        appId.ShouldBe(AppId);
        readTarget.ShouldBe(target, StringCompareShould.IgnoreCase);
        EnsureShortcut(link, AppId, dir.File("elsewhere.exe")).ShouldBe(StartMenuShortcut.Present);
    }

    [Fact]
    public void A_shortcut_whose_app_moved_follows_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var old = dir.File("old.exe");
        var link = Path.Combine(dir.Path, "Viewer.lnk");
        EnsureShortcut(link, AppId, old).ShouldBe(StartMenuShortcut.Created);
        File.Delete(old);

        var moved = dir.File("moved.exe");
        EnsureShortcut(link, AppId, moved).ShouldBe(StartMenuShortcut.Repointed);

        TryReadShortcut(link, out var appId, out var target).ShouldBeTrue();
        appId.ShouldBe(AppId);
        target.ShouldBe(moved, StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void Another_apps_shortcut_of_the_same_name_is_not_rewritten()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var theirs = dir.File("theirs.exe");
        var link = Path.Combine(dir.Path, "Viewer.lnk");
        EnsureShortcut(link, "Other.App", theirs).ShouldBe(StartMenuShortcut.Created);

        EnsureShortcut(link, AppId, dir.File("ours.exe")).ShouldBe(StartMenuShortcut.Foreign);

        TryReadShortcut(link, out var appId, out var target).ShouldBeTrue();
        appId.ShouldBe("Other.App");
        target.ShouldBe(theirs, StringCompareShould.IgnoreCase);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("appshell-identity-").FullName;

        // An empty file standing in for an executable: a shortcut only needs its target to exist.
        public string File(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllBytes(path, []);
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
