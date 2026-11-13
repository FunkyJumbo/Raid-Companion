using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace DpsInServerBar.Tracking;

/// <summary>
/// Pull history kept next to the plugin configuration, one JSON file per pull (in the "pulls" folder, named after
/// the pull's id): only the pulls that changed are written, and a damaged file only loses its own pull. Game thread
/// only, except the file writes.
/// <para>
/// Older versions kept everything in history.json: it is split into pull files on first load, then renamed
/// history.json.bak.
/// </para>
/// </summary>
public sealed class PullHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    // Changes often come in bursts (a pull, then its outcome, then a death refined): one write for all of them.
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    private readonly string folder;
    private readonly string legacyPath;
    private readonly Action<Exception, string> logError;
    private readonly List<PullRecord> pulls = [];
    private readonly HashSet<Guid> ids = [];

    // What the files do not have yet.
    private readonly HashSet<Guid> dirty = [];
    private readonly HashSet<Guid> deleted = [];
    private DateTime saveAtUtc;

    // File writes, one after the other in the order they were asked for.
    private Task writes = Task.CompletedTask;

    public PullHistory(string directory, Action<Exception, string> logError)
    {
        folder = Path.Combine(directory, "pulls");
        legacyPath = Path.Combine(directory, "history.json");
        this.logError = logError;
        Load();
    }

    /// <summary>Most recent first.</summary>
    public IReadOnlyList<PullRecord> Pulls => pulls;

    /// <summary>Incremented on every change, so windows can cache derived data.</summary>
    public int Revision { get; private set; }

    public void Add(PullRecord pull, int maxPulls)
    {
        pulls.Insert(0, pull);
        ids.Add(pull.Id);
        if (maxPulls > 0 && pulls.Count > maxPulls)
        {
            foreach (var old in pulls.Skip(maxPulls))
                Forget(old.Id);
            pulls.RemoveRange(maxPulls, pulls.Count - maxPulls);
        }

        Changed(pull);
    }

    public void Remove(Guid id)
    {
        if (pulls.RemoveAll(p => p.Id == id) > 0)
            Forget(id);
        Touch();
    }

    public void Clear()
    {
        foreach (var pull in pulls)
            Forget(pull.Id);
        pulls.Clear();
        Touch();
    }

    /// <summary>Call after mutating a record in place (e.g. its outcome). A pull that is not kept is ignored.</summary>
    public void Changed(PullRecord pull)
    {
        if (!ids.Contains(pull.Id))
            return;

        dirty.Add(pull.Id);
        Touch();
    }

    /// <summary>Call regularly (game thread): writes the files a little after the last changes, all at once.</summary>
    public void Update()
    {
        if (HasPending && DateTime.UtcNow >= saveAtUtc)
            writes = QueueWrites();
    }

    /// <summary>Writes what is still waiting, now, and waits for it (when the plugin unloads).</summary>
    public void Flush()
    {
        try
        {
            if (HasPending)
                writes = QueueWrites();
            writes.Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            logError(ex, "Could not save the pull history");
        }
    }

    /// <summary>Best value of a metric among the kills of the same encounter, excluding the given pull.</summary>
    public double? BestOf(string title, Func<PullRecord, double?> metric, Guid exclude) =>
        string.IsNullOrEmpty(title)
            ? null
            : pulls.Where(p => p.Id != exclude && p.Outcome == PullOutcome.Kill
                               && p.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
                   .Select(metric)
                   .Where(v => v is > 0)
                   .Max();

    /// <summary>
    /// Best personal DPS among the kills of the same encounter, excluding the given pull. Wipes are ignored:
    /// comparing with a prog pull that ended early says nothing.
    /// </summary>
    public PullRecord? BestFor(string title, Guid? exclude = null) =>
        string.IsNullOrEmpty(title)
            ? null
            : pulls.Where(p => p.Id != exclude && p.Outcome == PullOutcome.Kill
                               && p.Title.Equals(title, StringComparison.OrdinalIgnoreCase) && p.YourDps > 0)
                   .MaxBy(p => p.YourDps);

    private bool HasPending => dirty.Count > 0 || deleted.Count > 0;

    private string FileOf(Guid id) => Path.Combine(folder, $"{id:N}.json");

    private void Forget(Guid id)
    {
        ids.Remove(id);
        dirty.Remove(id);
        deleted.Add(id);
    }

    private void Touch()
    {
        Revision++;
        if (HasPending && saveAtUtc == default)
            saveAtUtc = DateTime.UtcNow + SaveDelay;
    }

    /// <summary>
    /// The pulls are turned into JSON here (game thread, so no record changes meanwhile), the files are written in
    /// the background after the writes already under way.
    /// </summary>
    private Task QueueWrites()
    {
        var files = new List<(string Path, byte[]? Json)>();
        foreach (var id in deleted)
            files.Add((FileOf(id), null));
        foreach (var pull in pulls.Where(p => dirty.Contains(p.Id)))
            files.Add((FileOf(pull.Id), JsonSerializer.SerializeToUtf8Bytes(pull, JsonOptions)));
        dirty.Clear();
        deleted.Clear();
        saveAtUtc = default;

        return writes.ContinueWith(_ => Write(files), TaskScheduler.Default);
    }

    private void Write(List<(string Path, byte[]? Json)> files)
    {
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var (path, json) in files)
            {
                if (json is null)
                {
                    File.Delete(path);
                    continue;
                }

                // Written beside, then moved in place: never a half-written pull.
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            logError(ex, "Could not save the pull history");
        }
    }

    private void Load()
    {
        try
        {
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<PullRecord>(File.ReadAllBytes(file), JsonOptions) is { } pull && ids.Add(pull.Id))
                            pulls.Add(pull);
                    }
                    catch (Exception ex)
                    {
                        logError(ex, $"Could not read the pull {Path.GetFileName(file)}, skipped");
                    }
                }
            }

            ImportLegacy();
        }
        catch (Exception ex)
        {
            logError(ex, "Could not read the pull history");
        }

        pulls.Sort((a, b) => b.StartUtc.CompareTo(a.StartUtc));
    }

    /// <summary>history.json of the older versions: its pulls become pull files, and it is kept as history.json.bak.</summary>
    private void ImportLegacy()
    {
        if (!File.Exists(legacyPath))
            return;

        var loaded = JsonSerializer.Deserialize<List<PullRecord>>(File.ReadAllText(legacyPath), JsonOptions) ?? [];
        var files = new List<(string Path, byte[] Json)>();
        foreach (var pull in loaded)
        {
            if (!ids.Add(pull.Id))
                continue;
            pulls.Add(pull);
            files.Add((FileOf(pull.Id), JsonSerializer.SerializeToUtf8Bytes(pull, JsonOptions)));
        }

        // Only set aside once every pull has its file.
        Directory.CreateDirectory(folder);
        foreach (var (path, json) in files)
            File.WriteAllBytes(path, json);
        File.Move(legacyPath, legacyPath + ".bak", overwrite: true);
    }
}
