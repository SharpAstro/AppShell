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
