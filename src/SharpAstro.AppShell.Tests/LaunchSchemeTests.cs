using Shouldly;
using Xunit;
using static SharpAstro.AppShell.LaunchScheme;

namespace SharpAstro.AppShell.Tests;

/// <summary>An in-memory registry: a unit test touches no real one. Counts what is written.</summary>
internal sealed class FakeRegistry : IUserRegistry
{
    public Dictionary<(string Key, string Name), string> Values { get; } = new();

    public int Writes { get; set; }

    public string? Read(string subKey, string name) => Values.GetValueOrDefault((subKey, name));

    public bool KeyExists(string subKey) => Values.Keys.Any(k => k.Key == subKey || k.Key.StartsWith(subKey + @"\", StringComparison.Ordinal));

    public void Write(string subKey, string name, string value)
    {
        Values[(subKey, name)] = value;
        Writes++;
    }

    public void DeleteTree(string subKey)
    {
        foreach (var k in Values.Keys.Where(k => k.Key == subKey || k.Key.StartsWith(subKey + @"\", StringComparison.Ordinal)).ToList())
        {
            Values.Remove(k);
        }
    }
}

/// <summary>The per-user URL scheme: what it writes, its ownership policy, scheme validation and the link round trip.
/// Pure and against a fake registry, so it runs on every OS.</summary>
public class LaunchSchemeTests
{
    private const string AppId = "SharpAstro.AppShell.Tests";
    private const string Exe = @"C:\Apps\Viewer\viewer.exe";

    private static LaunchSchemeRegistration Run(FakeRegistry registry, Func<string, bool>? exists = null, string exe = Exe)
        => LaunchScheme.Register("viewer", AppId, "Viewer", exe, registry, exists ?? (_ => true));

    [Fact]
    public void A_missing_scheme_is_created_with_the_documented_values()
    {
        var registry = new FakeRegistry();
        Run(registry).ShouldBe(LaunchSchemeRegistration.Created);
        registry.Values[(@"Software\Classes\viewer", "")].ShouldBe("URL:Viewer");
        registry.Values[(@"Software\Classes\viewer", "URL Protocol")].ShouldBe("");
        registry.Values[(@"Software\Classes\viewer", "AppUserModelID")].ShouldBe(AppId);
        registry.Values[(@"Software\Classes\viewer\shell\open\command", "")].ShouldBe($"\"{Exe}\" \"%1\"");
        registry.Values.Count.ShouldBe(4);
    }

    [Fact]
    public void A_second_register_writes_nothing()
    {
        var registry = new FakeRegistry();
        Run(registry);
        var writes = registry.Writes;
        Run(registry).ShouldBe(LaunchSchemeRegistration.Present);
        registry.Writes.ShouldBe(writes);
    }

    [Fact]
    public void Ours_pointing_at_another_copy_that_exists_is_kept()
    {
        var registry = new FakeRegistry();
        Run(registry, exe: @"C:\Installed\viewer.exe");
        var writes = registry.Writes;
        Run(registry, exe: @"C:\Dev\viewer.exe").ShouldBe(LaunchSchemeRegistration.Present);
        registry.Writes.ShouldBe(writes);
        registry.Values[(@"Software\Classes\viewer\shell\open\command", "")].ShouldContain("Installed");
    }

    [Fact]
    public void Ours_pointing_at_nothing_is_repointed_writing_only_the_command()
    {
        var registry = new FakeRegistry();
        Run(registry, exe: @"C:\Old\viewer.exe");
        var writes = registry.Writes;
        Run(registry, exists: _ => false).ShouldBe(LaunchSchemeRegistration.Repointed);
        registry.Writes.ShouldBe(writes + 1);
        registry.Values[(@"Software\Classes\viewer\shell\open\command", "")].ShouldBe($"\"{Exe}\" \"%1\"");
    }

    [Fact]
    public void A_scheme_with_no_app_id_or_another_one_is_left_alone()
    {
        var noId = new FakeRegistry();
        noId.Write(@"Software\Classes\viewer", "", "URL:Other");
        noId.Writes = 0;
        Run(noId).ShouldBe(LaunchSchemeRegistration.Foreign);
        noId.Writes.ShouldBe(0);

        var other = new FakeRegistry();
        other.Write(@"Software\Classes\viewer", "AppUserModelID", "Someone.Else");
        other.Write(@"Software\Classes\viewer\shell\open\command", "", "\"C:\\x.exe\" \"%1\"");
        var before = other.Writes;
        Run(other).ShouldBe(LaunchSchemeRegistration.Foreign);
        other.Writes.ShouldBe(before);
    }

    [Fact]
    public void Decide_covers_each_branch()
    {
        Decide(null, AppId, _ => true).ShouldBe(SchemeAction.Create);
        Decide((AppId, Exe), AppId, _ => true).ShouldBe(SchemeAction.Keep);
        Decide((AppId, Exe), AppId, _ => false).ShouldBe(SchemeAction.Repoint);
        Decide((AppId, null), AppId, _ => true).ShouldBe(SchemeAction.Repoint);
        Decide((null, Exe), AppId, _ => true).ShouldBe(SchemeAction.LeaveAlone);
        Decide(("Other", Exe), AppId, _ => true).ShouldBe(SchemeAction.LeaveAlone);
    }

    [Theory]
    [InlineData("\"C:\\a b\\x.exe\" \"%1\"", "C:\\a b\\x.exe")]
    [InlineData("C:\\x.exe %1", "C:\\x.exe")]
    [InlineData("", null)]
    [InlineData("\"", null)]
    public void ExeOf_reads_the_started_executable(string command, string? expected)
        => ExeOf(command).ShouldBe(expected);

    [Fact]
    public void A_registry_that_throws_is_a_failed_result()
    {
        Should.NotThrow(() => LaunchScheme.Register("viewer", AppId, "Viewer", Exe, new ThrowingRegistry(), _ => true))
            .ShouldBe(LaunchSchemeRegistration.Failed);
    }

    [Fact]
    public void No_executable_is_a_failed_result()
        => LaunchScheme.Register("viewer", AppId, "Viewer", null, new FakeRegistry(), _ => true).ShouldBe(LaunchSchemeRegistration.Failed);

    internal sealed class ThrowingRegistry : IUserRegistry
    {
        public string? Read(string subKey, string name) => throw new UnauthorizedAccessException();

        public bool KeyExists(string subKey) => throw new UnauthorizedAccessException();

        public void Write(string subKey, string name, string value) => throw new UnauthorizedAccessException();

        public void DeleteTree(string subKey) => throw new UnauthorizedAccessException();
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("a")]
    [InlineData("my-app.v2+x")]
    public void Valid_schemes_pass(string scheme) => IsValid(scheme).ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("Viewer")]
    [InlineData("1viewer")]
    [InlineData("-viewer")]
    [InlineData("my app")]
    [InlineData("my_app")]
    [InlineData("vïewer")]
    [InlineData("viewer:")]
    public void Invalid_schemes_are_refused(string scheme)
    {
        IsValid(scheme).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => LaunchScheme.Register(scheme, AppId, "Viewer"));
        Should.Throw<ArgumentException>(() => Link(scheme, "x"));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    [InlineData("file")]
    [InlineData("mailto")]
    [InlineData("ms-settings")]
    [InlineData("ms-windows-store")]
    [InlineData("microsoft-edge")]
    [InlineData("javascript")]
    public void Shell_owned_schemes_are_refused(string scheme)
    {
        Should.Throw<ArgumentException>(() => LaunchScheme.Register(scheme, AppId, "Viewer"));
        Should.Throw<ArgumentException>(() => Link(scheme, "x"));
    }

    [Fact]
    public void Blank_app_id_or_name_throws()
    {
        Should.Throw<ArgumentException>(() => LaunchScheme.Register("viewer", " ", "Viewer"));
        Should.Throw<ArgumentException>(() => LaunchScheme.Register("viewer", AppId, ""));
    }

    [Theory]
    [InlineData("job-42")]
    [InlineData("")]
    [InlineData("a b&c=d/e?f#g%h:i")]
    [InlineData("C:\\Users\\x\\file name.txt")]
    [InlineData("Grüße, 世界 😀")]
    [InlineData("\"quoted\" <tag> 'x'")]
    public void A_payload_round_trips(string payload)
    {
        var link = Link("viewer", payload);
        link.ShouldStartWith("viewer:");
        link.ShouldNotContain(" ");
        TryRead(link, "viewer", out var read).ShouldBeTrue();
        read.ShouldBe(payload);
    }

    [Fact]
    public void The_scheme_reads_without_regard_to_case()
    {
        TryRead("VIEWER:job-1", "viewer", out var read).ShouldBeTrue();
        read.ShouldBe("job-1");
    }

    [Theory]
    [InlineData("other:job-1")]
    [InlineData("viewerx:job-1")]
    [InlineData("viewer")]
    [InlineData("job-1")]
    [InlineData("")]
    [InlineData("viewer:%")]
    [InlineData("viewer:%4")]
    [InlineData("viewer:%zz")]
    [InlineData("viewer:%C3")]
    [InlineData("viewer:%FF%FE")]
    [InlineData("viewer:a b")]
    [InlineData("viewer://host/x")]
    [InlineData("--flag")]
    public void A_wrong_scheme_or_garbage_is_not_read(string arg)
    {
        TryRead(arg, "viewer", out var payload).ShouldBeFalse();
        payload.ShouldBeNull();
    }

    [Fact]
    public void A_null_arg_or_a_bad_scheme_is_not_read()
    {
        TryRead(null, "viewer", out _).ShouldBeFalse();
        TryRead("viewer:x", "Viewer", out _).ShouldBeFalse();
    }
}
