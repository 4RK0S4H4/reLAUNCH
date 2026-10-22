using ReLaunch.Core.Catalogue;
using Xunit;

namespace ReLaunch.Core.Tests;

public class CatalogueTests
{
    private static AppEntry Entry(string name, string parsing, params string[] procs) =>
        new(AppIds.From(parsing), name, parsing, null, procs);

    [Fact]
    public void IdsAreStableAndCaseInsensitive()
    {
        Assert.Equal(AppIds.From("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"), AppIds.From("microsoft.windowsnotepad_8wekyb3d8bbwe!app"));
        Assert.Equal(12, AppIds.From("x").Length);
        Assert.NotEqual(AppIds.From("a"), AppIds.From("b"));
    }

    [Fact]
    public void JellyfinProfileAddsTrayAndServerProcesses()
    {
        var app = CatalogueBuilder.Merge(Entry("Jellyfin Server", "jf", "Jellyfin.Windows.Tray"), null);
        Assert.Contains("Jellyfin.Windows.Tray", app.ProcessNames);
        Assert.Contains("jellyfin", app.ProcessNames);
        Assert.Equal(2, app.ProcessNames.Length);
    }

    [Fact]
    public void OverrideAddsExtraProcessesArgsAndTimeout()
    {
        var over = new AppOverride("id", " --fullscreen ", ["helper.exe", "HELPER"], 9000);
        var app = CatalogueBuilder.Merge(Entry("Thing", "t", "thing"), over);
        Assert.Equal(new[] {"thing", "helper"}, app.ProcessNames);
        Assert.Equal(" --fullscreen ", app.Args);
        Assert.Equal(9000, app.CloseTimeoutMs);
    }

    [Fact]
    public void AppWithoutProcessesIsNotTrackable()
    {
        Assert.False(CatalogueBuilder.Merge(Entry("Link", "l"), null).Trackable);
    }

    [Fact]
    public void BuildDeduplicatesAndSorts()
    {
        var a = Entry("Zed", "z", "zed");
        var b = Entry("alpha", "a", "alpha");
        var list = CatalogueBuilder.Build([a, b, a], new Dictionary<string, AppOverride>());
        Assert.Equal(new[] {"alpha", "Zed"}, list.Select(x => x.Name));
    }

    [Fact]
    public void SearchRanksPrefixBeforeSubstring()
    {
        var apps = new[] { Entry("Media Player", "mp"), Entry("Player One", "p1"), Entry("Notepad", "n") }
            .Select(e => CatalogueBuilder.Merge(e, null)).ToList();
        var found = CatalogueBuilder.Search(apps, "player").Select(a => a.Name).ToList();
        Assert.Equal(new[] {"Player One", "Media Player"}, found);
        Assert.Equal(3, CatalogueBuilder.Search(apps, "").Count());
    }

    [Fact]
    public void ProcessMatchingIgnoresCaseAndExeSuffix()
    {
        var now = DateTimeOffset.UtcNow;
        var procs = new[]
        {
            new ProcessSnapshot("Jellyfin.Windows.Tray", 1, now.AddSeconds(-100), 10),
            new ProcessSnapshot("JELLYFIN", 2, now.AddSeconds(-40), 30),
            new ProcessSnapshot("other", 3, now.AddSeconds(-500), 99),
        };
        var status = ProcessMatcher.Summarize(["jellyfin.exe", "Jellyfin.Windows.Tray"], procs, now);
        Assert.True(status.Running);
        Assert.Equal(2, status.ProcessCount);
        Assert.Equal(40, status.WorkingSetBytes);
        Assert.InRange(status.UptimeSeconds, 99, 101);
    }

    [Fact]
    public void NoMatchingProcessMeansStopped()
    {
        var status = ProcessMatcher.Summarize(["x"], [new ProcessSnapshot("y", 1, null, 1)], DateTimeOffset.UtcNow);
        Assert.False(status.Running);
        Assert.Equal(AppStatus.Stopped, status);
    }
}
