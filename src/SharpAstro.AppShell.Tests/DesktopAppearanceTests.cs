using Shouldly;
using Xunit;

namespace SharpAstro.AppShell.Tests;

/// <summary>
/// The AppsUseLightTheme mapping and its failure reasons run against a fake source: no registry read. The two LogOnce guard
/// tests touch the public static (a harmless in-process read-only HKCU read); the shared LogSink tests are in DesktopAccentTests.
/// </summary>
public class DesktopAppearanceTests
{
    private sealed class FakeSource(object? value, string? reason = null, Exception? throws = null) : IAppModeSource
    {
        public (object? Value, string? Reason) Read() => throws is null ? (value, reason) : throw throws;
    }

    [Fact]
    public void One_is_light()
    {
        var (mode, reason) = AppModeResolver.Resolve(new FakeSource(1));
        mode.ShouldBe(AppMode.Light);
        reason.ShouldBeNull();
    }

    [Fact]
    public void Zero_is_dark()
    {
        var (mode, reason) = AppModeResolver.Resolve(new FakeSource(0));
        mode.ShouldBe(AppMode.Dark);
        reason.ShouldBeNull();
    }

    [Fact]
    public void Missing_is_unknown_with_the_sources_reason()
    {
        var (mode, reason) = AppModeResolver.Resolve(new FakeSource(null, "not Windows"));
        mode.ShouldBe(AppMode.Unknown);
        reason.ShouldBe("not Windows");
    }

    [Fact]
    public void Missing_without_a_reason_still_has_one()
    {
        AppModeResolver.Resolve(new FakeSource(null)).Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Wrong_type_is_unknown_with_a_reason()
    {
        var (mode, reason) = AppModeResolver.Resolve(new FakeSource("1"));
        mode.ShouldBe(AppMode.Unknown);
        reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Other_number_is_unknown_with_a_reason()
    {
        var (mode, reason) = AppModeResolver.Resolve(new FakeSource(2));
        mode.ShouldBe(AppMode.Unknown);
        reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Throwing_source_is_unknown_with_a_reason()
    {
        var (mode, reason) = AppModeResolver.Resolve(new FakeSource(null, throws: new InvalidOperationException("boom")));
        mode.ShouldBe(AppMode.Unknown);
        reason.ShouldNotBeNull().ShouldContain("boom");
    }

    [Fact]
    public void DesktopAppearance_LogOnce_with_a_null_sink_throws_at_once()
        => Should.Throw<ArgumentNullException>(() => DesktopAppearance.LogOnce((Action<string>)null!));

    [Fact]
    public void DesktopAppearance_LogOnce_with_a_null_logger_throws_at_once()
        => Should.Throw<ArgumentNullException>(() => DesktopAppearance.LogOnce((Microsoft.Extensions.Logging.ILogger)null!));
}
