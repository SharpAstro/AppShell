using Shouldly;
using Xunit;

namespace SharpAstro.AppShell.Tests;

/// <summary>
/// The folder identity is what makes "one instance per open folder" work, so the cases that must
/// collapse to one channel are the ones worth pinning: a trailing separator, a relative path, and
/// (on Windows and macOS) a difference in case. Each of those arriving as a separate identity would
/// open a redundant window for a folder already on screen, which is the exact bug the gate exists
/// to prevent.
/// </summary>
public class PathIdentityTests
{
    [Fact]
    public void A_trailing_separator_does_not_change_the_identity()
    {
        var bare = Path.Combine(Path.GetTempPath(), "lights");

        InstanceGate.NormalizePathIdentity(bare)
            .ShouldBe(InstanceGate.NormalizePathIdentity(bare + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void A_relative_path_resolves_to_the_same_identity_as_its_absolute_form()
    {
        var absolute = Directory.GetCurrentDirectory();

        InstanceGate.NormalizePathIdentity(".").ShouldBe(InstanceGate.NormalizePathIdentity(absolute));
    }

    [Fact]
    public void Redundant_segments_collapse()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var roundabout = Path.Combine(root, "a", "..", "b");

        InstanceGate.NormalizePathIdentity(roundabout)
            .ShouldBe(InstanceGate.NormalizePathIdentity(Path.Combine(root, "b")));
    }

    [Fact]
    public void Case_is_ignored_only_where_the_file_system_ignores_it()
    {
        var lower = Path.Combine(Path.GetTempPath(), "lights");
        var upper = Path.Combine(Path.GetTempPath(), "LIGHTS");

        var same = InstanceGate.NormalizePathIdentity(lower) == InstanceGate.NormalizePathIdentity(upper);

        // Linux is case-sensitive, so two differently-cased paths really are two folders there and
        // folding them would send a file to the wrong window.
        same.ShouldBe(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
    }

    [Fact]
    public void A_drive_root_keeps_its_separator()
    {
        // "C:" and "C:\" mean different things to Path, so trimming the separator off a root would
        // produce an identity that is not the folder.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.GetPathRoot(Directory.GetCurrentDirectory());
        root.ShouldNotBeNull();

        InstanceGate.NormalizePathIdentity(root).ShouldEndWith(Path.DirectorySeparatorChar.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_path_is_rejected(string path)
        => Should.Throw<ArgumentException>(() => InstanceGate.NormalizePathIdentity(path));
}
