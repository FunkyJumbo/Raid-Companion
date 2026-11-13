using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DpsInServerBar.Tracking;
using Xunit;

namespace DpsInServerBar.Tests;

public sealed class PullHistoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> errors = [];

    public PullHistoryTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private PullHistory Open() => new(directory, (e, message) => errors.Add($"{message}: {e.Message}"));

    private string PullsFolder => Path.Combine(directory, "pulls");

    private static PullRecord Pull(string title, int minutesAgo, PullOutcome outcome = PullOutcome.Wipe, double dps = 1000) => new()
    {
        Title = title,
        StartUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesAgo),
        DurationSeconds = 120,
        Outcome = outcome,
        YourDps = dps,
        YourDamageTimeline = [0, 1000, 2000],
    };

    [Fact]
    public void PullsComeBackAfterAReload()
    {
        var history = Open();
        history.Add(Pull("Old", 10), 300);
        history.Add(Pull("New", 0, PullOutcome.Kill, 2500), 300);
        history.Flush();

        var reloaded = Open();
        Assert.Equal(["New", "Old"], reloaded.Pulls.Select(p => p.Title));
        Assert.Equal(PullOutcome.Kill, reloaded.Pulls[0].Outcome);
        Assert.Equal(2500, reloaded.Pulls[0].YourDps);
        Assert.Equal([0, 1000, 2000], reloaded.Pulls[0].YourDamageTimeline);
        Assert.Equal(2, Directory.GetFiles(PullsFolder, "*.json").Length);
        Assert.Empty(errors);
    }

    [Fact]
    public void AChangeToAPullIsSaved()
    {
        var history = Open();
        var pull = Pull("Boss", 0);
        history.Add(pull, 300);
        history.Flush();

        pull.Outcome = PullOutcome.Kill;
        history.Changed(pull);
        history.Flush();

        Assert.Equal(PullOutcome.Kill, Open().Pulls.Single().Outcome);
    }

    [Fact]
    public void RemovedAndOverflowingPullsLoseTheirFile()
    {
        var history = Open();
        var first = Pull("First", 3);
        history.Add(first, 2);
        history.Add(Pull("Second", 2), 2);
        history.Flush();
        history.Add(Pull("Third", 1), 2); // over the limit: First goes
        history.Remove(history.Pulls[0].Id); // Third
        history.Flush();

        Assert.Equal(["Second"], Open().Pulls.Select(p => p.Title));
        Assert.Single(Directory.GetFiles(PullsFolder, "*.json"));
    }

    [Fact]
    public void ClearEmptiesTheFolder()
    {
        var history = Open();
        history.Add(Pull("A", 1), 300);
        history.Add(Pull("B", 0), 300);
        history.Flush();
        history.Clear();
        history.Flush();

        Assert.Empty(Open().Pulls);
        Assert.Empty(Directory.GetFiles(PullsFolder, "*.json"));
    }

    [Fact]
    public void APullThatIsNotKeptIsIgnored()
    {
        var history = Open();
        history.Changed(Pull("Not recorded", 0));
        history.Flush();

        Assert.Empty(Open().Pulls);
    }

    [Fact]
    public void TheOldSingleFileIsSplitAndSetAside()
    {
        var old = new List<PullRecord> { Pull("Newest", 0), Pull("Middle", 5), Pull("Oldest", 10) };
        File.WriteAllText(Path.Combine(directory, "history.json"), JsonSerializer.Serialize(old));

        var history = Open();
        Assert.Equal(["Newest", "Middle", "Oldest"], history.Pulls.Select(p => p.Title));
        Assert.Equal(old.Select(p => p.Id), history.Pulls.Select(p => p.Id));
        Assert.False(File.Exists(Path.Combine(directory, "history.json")));
        Assert.True(File.Exists(Path.Combine(directory, "history.json.bak")));
        Assert.Equal(3, Directory.GetFiles(PullsFolder, "*.json").Length);

        // And it is only done once.
        Assert.Equal(3, Open().Pulls.Count);
        Assert.Empty(errors);
    }

    [Fact]
    public void ADamagedFileOnlyLosesItsPull()
    {
        var history = Open();
        history.Add(Pull("Good", 0), 300);
        history.Flush();
        File.WriteAllText(Path.Combine(PullsFolder, "broken.json"), "{ not json");

        Assert.Equal(["Good"], Open().Pulls.Select(p => p.Title));
        Assert.Single(errors);
    }

    [Fact]
    public void BestIsAmongTheKillsOfTheSameFight()
    {
        var history = Open();
        history.Add(Pull("Boss", 3, PullOutcome.Kill, 2000), 300);
        history.Add(Pull("Boss", 2, PullOutcome.Wipe, 9000), 300);
        history.Add(Pull("Other", 1, PullOutcome.Kill, 5000), 300);
        var last = Pull("boss", 0, PullOutcome.Kill, 3000);
        history.Add(last, 300);

        Assert.Equal(3000, history.BestFor("Boss")!.YourDps);
        Assert.Equal(2000, history.BestFor("Boss", last.Id)!.YourDps);
        Assert.Equal(2000, history.BestOf("Boss", p => p.YourDps, last.Id));
        Assert.Null(history.BestFor("Nothing"));
    }
}
