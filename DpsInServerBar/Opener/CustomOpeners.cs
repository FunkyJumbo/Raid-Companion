using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Dalamud.Game;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;

namespace DpsInServerBar.Opener;

/// <summary>One button of an opener made in the editor. <see cref="ActionIds"/>: any of them counts, the first is shown.</summary>
[Serializable]
public sealed class CustomOpenerStep
{
    public List<uint> ActionIds { get; set; } = [];
    public StepKind Kind { get; set; }

    /// <summary>Seconds before the pull, null after it.</summary>
    public double? Prepull { get; set; }

    public CustomOpenerStep Clone() => new() { ActionIds = [.. ActionIds], Kind = Kind, Prepull = Prepull };
}

/// <summary>An opener made in the editor (new, copied from a built-in one, or imported from a code), saved in the settings.</summary>
[Serializable]
public sealed class CustomOpener
{
    public string Id { get; set; } = CustomOpeners.NewId();
    public string Job { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<CustomOpenerStep> Steps { get; set; } = [];
    public List<string> Notes { get; set; } = [];

    /// <summary>English names of actions that never count as mistakes (kept from a copied built-in opener).</summary>
    public List<string> Ignored { get; set; } = [];

    /// <summary>Bumped on each edit, so the practice format is built again (and the try in progress restarts).</summary>
    [Newtonsoft.Json.JsonIgnore]
    public int Revision { get; set; }
}

/// <summary>Openers made by the player: conversion to the practice format, copies of built-in ones, share codes.</summary>
public static class CustomOpeners
{
    public const string IdPrefix = "custom-";

    // Share codes: this prefix, then the opener as compact JSON, deflated, in base64.
    private const string CodePrefix = "RCOPENER1:";

    public static string NewId() => IdPrefix + Guid.NewGuid().ToString("N")[..12];

    public static bool IsCustom(string openerId) => openerId.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>The built-in openers of a job, then the player's own.</summary>
    public static List<OpenerDefinition> ForJob(Configuration config, string job) =>
        [.. OpenerDefinitions.ForJob(job), .. config.CustomOpeners.Where(o => o.Job == job).Select(ToDefinition)];

    // The practice format of each opener, kept while it is not edited: the opener window compares openers to know
    // whether its try is still the same one.
    private static readonly Dictionary<CustomOpener, (int Revision, string Language, OpenerDefinition Definition)> Definitions = new();

    /// <summary>The opener in the practice format (actions by English name, like the built-in ones).</summary>
    public static OpenerDefinition ToDefinition(CustomOpener opener)
    {
        if (Definitions.TryGetValue(opener, out var cached) && cached.Revision == opener.Revision && cached.Language == Loc.Code)
            return cached.Definition;

        var definition = Build(opener);
        Definitions[opener] = (opener.Revision, Loc.Code, definition);
        return definition;
    }

    private static OpenerDefinition Build(CustomOpener opener)
    {
        var stat = MainStat(opener.Job);
        var steps = opener.Steps
                          .Where(s => s.Kind == StepKind.Potion || s.ActionIds.Count > 0)
                          .Select(s => new OpenerStep(
                                      s.Kind == StepKind.Potion ? [] : s.ActionIds.Select(EnglishName).ToList(),
                                      s.Kind, s.Prepull, s.Kind == StepKind.Potion ? stat : null))
                          .ToList();
        return new OpenerDefinition(opener.Id, opener.Job, opener.Name.Length > 0 ? opener.Name : Loc.T("Unnamed opener"),
                                    Loc.T("Your opener"), steps, opener.Notes, opener.Ignored.Count > 0 ? opener.Ignored : null);
    }

    /// <summary>A copy of an opener (built-in or not) that can be edited.</summary>
    public static CustomOpener Copy(OpenerDefinition source) => new()
    {
        Job = source.Job,
        Name = Loc.F("{0} (copy)", Loc.T(source.Name)),
        Steps = source.Steps.Select(s => new CustomOpenerStep
        {
            ActionIds = s.Names.Select(ActionNames.Find).Where(id => id != 0).ToList(),
            Kind = s.Kind,
            Prepull = s.Prepull,
        }).ToList(),
        Notes = source.Notes.Select(Loc.T).ToList(),
        Ignored = source.Ignored?.ToList() ?? [],
    };

    /// <summary>Whether an action is a GCD (shares the global cooldown), from the game data.</summary>
    public static bool IsGcd(uint actionId) =>
        Plugin.DataManager.GetExcelSheet<Action>().TryGetRow(actionId, out var row)
        && (row.CooldownGroup == 58 || row.AdditionalCooldownGroup == 58);

    private static string EnglishName(uint actionId) =>
        Plugin.DataManager.GetExcelSheet<Action>(ClientLanguage.English).TryGetRow(actionId, out var row)
            ? row.Name.ExtractText()
            : string.Empty;

    /// <summary>The job's main stat (for the tincture's icon): Strength, Dexterity, Intelligence or Mind.</summary>
    public static string MainStat(string job)
    {
        var row = Plugin.DataManager.GetExcelSheet<ClassJob>(ClientLanguage.English)
                        .FirstOrDefault(j => j.Abbreviation.ExtractText() == job);
        return row.PrimaryStat switch
        {
            2 => "Dexterity",
            4 => "Intelligence",
            5 => "Mind",
            _ => "Strength",
        };
    }

    // ------------------------------------------------------------------ Share codes

    private sealed record CodeStep(uint[] A, int K, double? P);

    private sealed record CodeOpener(string J, string N, CodeStep[] S, string[] Notes, string[]? I);

    /// <summary>A text code holding the whole opener, to paste anywhere and import elsewhere.</summary>
    public static string Export(CustomOpener opener)
    {
        var data = new CodeOpener(
            opener.Job, opener.Name,
            opener.Steps.Select(s => new CodeStep([.. s.ActionIds], (int)s.Kind, s.Prepull)).ToArray(),
            [.. opener.Notes], opener.Ignored.Count > 0 ? [.. opener.Ignored] : null);
        var json = JsonSerializer.SerializeToUtf8Bytes(data);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize))
            deflate.Write(json);
        return CodePrefix + Convert.ToBase64String(output.ToArray());
    }

    /// <summary>The opener held by a code, as a new opener of yours; null (with the reason) when the code is not valid.</summary>
    public static CustomOpener? Import(string code, out string error)
    {
        error = string.Empty;
        code = code.Trim();
        if (!code.StartsWith(CodePrefix, StringComparison.Ordinal))
        {
            error = Loc.T("This is not an opener code.");
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(code[CodePrefix.Length..]);
            using var input = new DeflateStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var json = new MemoryStream();
            input.CopyTo(json);
            var data = JsonSerializer.Deserialize<CodeOpener>(json.ToArray());
            if (data is null || data.J.Length == 0 || data.S.Length == 0)
            {
                error = Loc.T("The code holds no opener.");
                return null;
            }

            return new CustomOpener
            {
                Job = data.J,
                Name = data.N,
                Steps = data.S.Select(s => new CustomOpenerStep
                {
                    ActionIds = [.. s.A],
                    Kind = Enum.IsDefined(typeof(StepKind), s.K) ? (StepKind)s.K : StepKind.Ogcd,
                    Prepull = s.P,
                }).ToList(),
                Notes = [.. data.Notes],
                Ignored = data.I?.ToList() ?? [],
            };
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or JsonException)
        {
            error = Loc.T("The code is damaged (copied only in part?).");
            return null;
        }
    }

    // ------------------------------------------------------------------ Actions of a job

    private static readonly Dictionary<string, List<uint>> ActionsByJob = new();

    /// <summary>
    /// The job's actions that can go in an opener: those of the official job guide (job and role actions, in level
    /// order), then the ones used in its built-in openers that the guide does not list (e.g. each Ninjutsu).
    /// </summary>
    public static IReadOnlyList<uint> ActionsOf(string job)
    {
        if (ActionsByJob.TryGetValue(job, out var cached))
            return cached;

        var guide = Opener.JobActions.ByJob.GetValueOrDefault(job) ?? [];
        var fromOpeners = OpenerDefinitions.ForJob(job).SelectMany(o => o.Steps).SelectMany(s => s.Names);
        var result = guide.Concat(fromOpeners)
                          .Select(ActionNames.Find)
                          .Where(id => id != 0)
                          .Distinct()
                          .ToList();
        ActionsByJob[job] = result;
        return result;
    }

    /// <summary>The combat jobs (abbreviations), tanks, healers, melee, ranged and casters.</summary>
    public static IReadOnlyList<string> CombatJobs() => [.. Opener.JobActions.ByJob.Keys];
}
