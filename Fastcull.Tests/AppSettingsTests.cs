using System;
using System.IO;
using Fastcull.Services;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// PRD 1.1.1's last-folder memory. Every test here restores the real settings file afterwards -
/// these run against the same %LOCALAPPDATA% path the app uses, and a test that clobbered a
/// developer's remembered folder would be an unpleasant surprise.
/// </summary>
public class AppSettingsTests : IDisposable
{
    private readonly string? _originalJson;
    private readonly bool _existedBefore;

    public AppSettingsTests()
    {
        _existedBefore = File.Exists(AppSettings.SettingsPath);
        _originalJson = _existedBefore ? File.ReadAllText(AppSettings.SettingsPath) : null;
    }

    public void Dispose()
    {
        try
        {
            if (_existedBefore && _originalJson is not null)
                File.WriteAllText(AppSettings.SettingsPath, _originalJson);
            else if (File.Exists(AppSettings.SettingsPath))
                File.Delete(AppSettings.SettingsPath);
        }
        catch { /* best effort, as the type itself is */ }
    }

    [Fact]
    public void TheSettingsFileSitsBesideTheSessionDatabases()
    {
        Assert.EndsWith("settings.json", AppSettings.SettingsPath);
        Assert.Contains("FastCull", AppSettings.SettingsPath);
    }

    [Fact]
    public void AFolderThatExistsRoundTrips()
    {
        var folder = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        Assert.True(AppSettings.SetLastFolder(folder));
        Assert.Equal(folder, AppSettings.ReadRaw());
        Assert.Equal(folder, AppSettings.GetResumableFolder());
    }

    [Fact]
    public void AFolderThatNoLongerExistsIsNotResumable()
    {
        // The unplugged-card case. PRD 1.1.1 wants this to land on the same empty state as a
        // first run, so GetResumableFolder must report nothing...
        const string gone = @"C:\no\such\folder\at\all";
        AppSettings.SetLastFolder(gone);

        Assert.Null(AppSettings.GetResumableFolder());

        // ...while the raw value survives, so the empty state can name what it could not open.
        Assert.Equal(gone, AppSettings.ReadRaw());
    }

    [Fact]
    public void NoSettingsFileReadsAsNoLastFolder()
    {
        if (File.Exists(AppSettings.SettingsPath)) File.Delete(AppSettings.SettingsPath);

        Assert.Null(AppSettings.ReadRaw());
        Assert.Null(AppSettings.GetResumableFolder());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"lastFolder\": ")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{ \"lastFolder\": null }")]
    [InlineData("{ \"lastFolder\": \"   \" }")]
    public void AMalformedOrEmptySettingsFileReadsAsNothingRatherThanThrowing(string contents)
    {
        Directory.CreateDirectory(AppSettings.RootDirectory);
        File.WriteAllText(AppSettings.SettingsPath, contents);

        Assert.Null(AppSettings.ReadRaw());
        Assert.Null(AppSettings.GetResumableFolder());
    }

    [Fact]
    public void ClearingTheLastFolderIsSupported()
    {
        AppSettings.SetLastFolder(Path.GetTempPath());
        Assert.NotNull(AppSettings.ReadRaw());

        AppSettings.SetLastFolder(null);
        Assert.Null(AppSettings.ReadRaw());
    }

    [Fact]
    public void TheMostRecentWriteWins()
    {
        var first = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var second = Path.GetDirectoryName(first)!;

        AppSettings.SetLastFolder(first);
        AppSettings.SetLastFolder(second);

        Assert.Equal(second, AppSettings.ReadRaw());
    }
}
