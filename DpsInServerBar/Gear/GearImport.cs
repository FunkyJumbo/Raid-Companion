using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DpsInServerBar.Gear;

/// <summary>
/// Gear sets imported from a link of xivgear.app (a set or a whole sheet, best in slot lists included) or etro.gg.
/// The only part of the plugin reaching the internet, and only when the player imports a link.
/// </summary>
public static partial class GearImport
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    static GearImport() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("RaidCompanion-FFXIV-Plugin");

    // xivgear slot names.
    private static readonly Dictionary<string, GearSlot> XivgearSlots = new()
    {
        ["Weapon"] = GearSlot.Weapon, ["OffHand"] = GearSlot.OffHand, ["Head"] = GearSlot.Head, ["Body"] = GearSlot.Body,
        ["Hand"] = GearSlot.Hands, ["Legs"] = GearSlot.Legs, ["Feet"] = GearSlot.Feet, ["Ears"] = GearSlot.Ears,
        ["Neck"] = GearSlot.Neck, ["Wrist"] = GearSlot.Wrists, ["RingLeft"] = GearSlot.RingLeft, ["RingRight"] = GearSlot.RingRight,
    };

    // xivgear relic stat names.
    private static readonly Dictionary<string, uint> XivgearStats = new()
    {
        ["crit"] = GearStats.CriticalHit, ["dhit"] = GearStats.DirectHit, ["determination"] = GearStats.Determination,
        ["skillspeed"] = GearStats.SkillSpeed, ["spellspeed"] = GearStats.SpellSpeed, ["tenacity"] = GearStats.Tenacity,
        ["piety"] = GearStats.Piety,
    };

    // etro field names.
    private static readonly Dictionary<string, GearSlot> EtroSlots = new()
    {
        ["weapon"] = GearSlot.Weapon, ["offHand"] = GearSlot.OffHand, ["head"] = GearSlot.Head, ["body"] = GearSlot.Body,
        ["hands"] = GearSlot.Hands, ["legs"] = GearSlot.Legs, ["feet"] = GearSlot.Feet, ["ears"] = GearSlot.Ears,
        ["neck"] = GearSlot.Neck, ["wrists"] = GearSlot.Wrists, ["fingerL"] = GearSlot.RingLeft, ["fingerR"] = GearSlot.RingRight,
    };

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Uuid();

    /// <summary>Whether the text looks like a link the import reads.</summary>
    public static bool IsLink(string text) => text.Contains("xivgear.app", StringComparison.OrdinalIgnoreCase)
                                              || text.Contains("etro.gg", StringComparison.OrdinalIgnoreCase);

    // ---- Share code: a set as text to paste (no internet), "RCBIS1:" then the set's JSON, compressed, in base64.

    private const string CodePrefix = "RCBIS1:";

    /// <summary>Whether the text is a share code of a set.</summary>
    public static bool IsCode(string text) => text.Trim().StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The share code of a set (without its id and relic check, which are the player's own).</summary>
    public static string ToCode(GearSet set)
    {
        var copy = set.Copy(set.Name);
        copy.Id = string.Empty;
        foreach (var piece in copy.Pieces.Values)
            piece.RelicChecked = null;
        copy.Pieces = copy.Pieces.Where(p => p.Value.ItemId != 0 || p.Value.Note.Length > 0)
                          .ToDictionary(p => p.Key, p => p.Value);

        var json = System.Text.Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(copy, new Newtonsoft.Json.JsonSerializerSettings
        {
            DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore,
        }));
        using var output = new System.IO.MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.SmallestSize))
            deflate.Write(json);
        return CodePrefix + Convert.ToBase64String(output.ToArray());
    }

    /// <summary>The set of a share code, or an exception when the code is not one.</summary>
    public static GearSet FromCode(string code)
    {
        try
        {
            var data = Convert.FromBase64String(code.Trim()[CodePrefix.Length..].Trim());
            using var input = new System.IO.Compression.DeflateStream(new System.IO.MemoryStream(data), System.IO.Compression.CompressionMode.Decompress);
            using var reader = new System.IO.StreamReader(input, System.Text.Encoding.UTF8);
            var set = Newtonsoft.Json.JsonConvert.DeserializeObject<GearSet>(reader.ReadToEnd())
                      ?? throw new InvalidOperationException();
            set.Id = Guid.NewGuid().ToString("N");
            set.Pieces ??= new();
            foreach (var piece in set.Pieces.Values)
            {
                piece.Materia ??= [];
                piece.Relic ??= new();
                piece.Note ??= string.Empty;
                piece.RelicChecked = null;
            }

            set.Name ??= string.Empty;
            set.Note ??= string.Empty;
            set.FoodNote ??= string.Empty;
            set.Source ??= string.Empty;
            set.Job ??= string.Empty;
            if (GearCatalog.Jobs.All(j => j.Abbreviation != set.Job))
                throw new InvalidOperationException();
            return set;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(Loc.T("Not a valid set code."), e);
        }
    }

    /// <summary>The sets of a link (several for an xivgear sheet), or an exception with what went wrong.</summary>
    public static async Task<List<GearSet>> FromLink(string link)
    {
        link = Uri.UnescapeDataString(link.Trim());
        if (link.Contains("etro.gg", StringComparison.OrdinalIgnoreCase))
            return [await FromEtro(link)];
        if (link.Contains("xivgear.app", StringComparison.OrdinalIgnoreCase))
            return await FromXivgear(link);
        throw new InvalidOperationException(Loc.T("Not an xivgear.app or etro.gg link."));
    }

    // ---- xivgear.app

    private static async Task<List<GearSet>> FromXivgear(string link)
    {
        // A best in slot list: "?page=bis|sge|ultimate|fru" or "#/bis/sge/ultimate/fru".
        var bis = Regex.Match(link, @"bis[|/]([a-z]+(?:[|/][A-Za-z0-9_\-]+)*)");
        string url;
        if (bis.Success)
            url = $"https://staticbis.xivgear.app/{bis.Groups[1].Value.Replace('|', '/')}.json";
        else if (Uuid().Match(link) is { Success: true } id)
            url = $"https://api.xivgear.app/shortlink/{id.Value}";
        else
            throw new InvalidOperationException(Loc.T("This xivgear link holds no set: use the link of a set or sheet (Export, link)."));

        using var json = JsonDocument.Parse(await Http.GetStringAsync(url));
        var root = json.RootElement;
        var job = Text(root, "job");

        // A single set ("onlySetIndex" in the link: one set of the sheet).
        var only = Regex.Match(link, @"onlySetIndex=(\d+)");
        var sets = new List<GearSet>();
        if (root.TryGetProperty("sets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var set in list.EnumerateArray())
            {
                var at = index++;
                if (set.TryGetProperty("isSeparator", out var sep) && sep.ValueKind == JsonValueKind.True)
                    continue;
                if (only.Success && at != int.Parse(only.Groups[1].Value))
                    continue;
                sets.Add(XivgearSet(set, job, Text(root, "name"), link));
            }
        }
        else
        {
            sets.Add(XivgearSet(root, job, string.Empty, link));
        }

        if (sets.Count == 0)
            throw new InvalidOperationException(Loc.T("No set found behind this link."));
        return sets;
    }

    private static GearSet XivgearSet(JsonElement set, string job, string sheet, string link)
    {
        var name = Text(set, "name");
        var gear = new GearSet
        {
            Job = job.ToUpperInvariant(),
            Name = sheet.Length > 0 && name.Length > 0 ? $"{sheet} - {name}" : name.Length > 0 ? name : sheet,
            Note = Text(set, "description"),
            Source = link,
        };

        if (set.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in items.EnumerateObject())
            {
                if (!XivgearSlots.TryGetValue(item.Name, out var slot) || !item.Value.TryGetProperty("id", out var id))
                    continue;

                var piece = gear.Edit(slot);
                piece.ItemId = id.GetUInt32();
                if (item.Value.TryGetProperty("materia", out var materia) && materia.ValueKind == JsonValueKind.Array)
                    piece.Materia = materia.EnumerateArray().Select(m => m.TryGetProperty("id", out var mid) && mid.GetInt32() > 0 ? (uint)mid.GetInt32() : 0u).ToList();

                // A relic's chosen stats.
                if (item.Value.TryGetProperty("relicStats", out var relic) && relic.ValueKind == JsonValueKind.Object)
                {
                    foreach (var stat in relic.EnumerateObject())
                    {
                        if (XivgearStats.TryGetValue(stat.Name, out var param) && stat.Value.ValueKind == JsonValueKind.Number && stat.Value.GetInt32() > 0)
                            piece.Relic[param] = stat.Value.GetInt32();
                    }
                }
            }
        }

        if (set.TryGetProperty("food", out var food) && food.ValueKind == JsonValueKind.Number)
            gear.Food = food.GetUInt32();
        gear.MaxItemLevel = gear.Pieces.Values.Select(p => GearCatalog.Item(p.ItemId)?.ItemLevel ?? 0).DefaultIfEmpty(0).Max();
        return gear;
    }

    // ---- etro.gg

    private static async Task<GearSet> FromEtro(string link)
    {
        if (Uuid().Match(link) is not { Success: true } id)
            throw new InvalidOperationException(Loc.T("This etro link holds no gear set."));

        using var json = JsonDocument.Parse(await Http.GetStringAsync($"https://etro.gg/api/gearsets/{id.Value}/"));
        var root = json.RootElement;
        var gear = new GearSet
        {
            Name = Text(root, "name"),
            Job = Text(root, "jobAbbrev").ToUpperInvariant(),
            Note = Text(root, "notes"),
            Source = link,
        };
        if (gear.Job.Length == 0 && root.TryGetProperty("job", out var jobId) && jobId.ValueKind == JsonValueKind.Number)
            gear.Job = GearCatalog.JobAbbreviation(jobId.GetUInt32());

        root.TryGetProperty("materia", out var materia);
        foreach (var (field, slot) in EtroSlots)
        {
            if (!root.TryGetProperty(field, out var item) || item.ValueKind != JsonValueKind.Number)
                continue;

            var piece = gear.Edit(slot);
            piece.ItemId = item.GetUInt32();
            // The materia by item id; a ring's by its id and side ("123L"), as the same ring can be on both hands.
            var key = piece.ItemId.ToString();
            var side = slot == GearSlot.RingLeft ? key + "L" : slot == GearSlot.RingRight ? key + "R" : key;
            if (materia.ValueKind == JsonValueKind.Object
                && (materia.TryGetProperty(side, out var melds) || materia.TryGetProperty(key, out melds)))
            {
                piece.Materia = melds.EnumerateObject().OrderBy(m => int.TryParse(m.Name, out var n) ? n : 99)
                                     .Select(m => m.Value.ValueKind == JsonValueKind.Number ? m.Value.GetUInt32() : 0u).ToList();
            }
        }

        // The food is etro's own id: its item comes from etro too.
        if (root.TryGetProperty("food", out var food) && food.ValueKind == JsonValueKind.Number)
        {
            try
            {
                using var foodJson = JsonDocument.Parse(await Http.GetStringAsync($"https://etro.gg/api/food/{food.GetInt32()}/"));
                if (foodJson.RootElement.TryGetProperty("item", out var foodItem) && foodItem.ValueKind == JsonValueKind.Number)
                    gear.Food = foodItem.GetUInt32();
            }
            catch (HttpRequestException)
            {
                // The set without its food.
            }
        }

        gear.MaxItemLevel = gear.Pieces.Values.Select(p => GearCatalog.Item(p.ItemId)?.ItemLevel ?? 0).DefaultIfEmpty(0).Max();
        return gear;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}
