using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using DpsInServerBar.Combat;
using DpsInServerBar.Gear;

namespace DpsInServerBar.UI;

/// <summary>
/// A little pixel fight: a party against a boss, attacks, heals, the boss's raidwide, a death and the clear, in a loop.
/// The sprites are the player's own pictures, none comes with the plugin: in the plugin's config folder, "sprites\"
/// holds one PNG per job named after it (PLD.png, SGE.png...) and Boss.png. The party sprites look to the left and the
/// boss to the right: both are mirrored to face each other. Without job pictures there is no scene.
/// </summary>
public sealed class RaidScene
{
    // ---- The pictures, loaded once for every scene.

    private sealed record Sprite(IDalamudTextureWrap Normal, IDalamudTextureWrap White, IDalamudTextureWrap Grey, int Width, int Height) : IDisposable
    {
        public void Dispose()
        {
            Normal.Dispose();
            White.Dispose();
            Grey.Dispose();
        }
    }

    // Pixels are blown up this many times when loaded, so they stay sharp once drawn smaller.
    private const int Upscale = 4;

    private static Task<Dictionary<string, Sprite>>? loading;

    public static string Folder => Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "sprites");

    private static Dictionary<string, Sprite>? Sprites
    {
        get
        {
            loading ??= Task.Run(LoadAll);
            return loading.IsCompletedSuccessfully ? loading.Result : null;
        }
    }

    /// <summary>Whether there is a party to show (starts loading the pictures the first time).</summary>
    public static bool Available => Sprites is { } sprites && sprites.Keys.Any(k => k != "BOSS");

    public static void Unload()
    {
        if (loading is { IsCompletedSuccessfully: true } done)
        {
            foreach (var sprite in done.Result.Values)
                sprite.Dispose();
        }

        loading = null;
    }

    private static async Task<Dictionary<string, Sprite>> LoadAll()
    {
        var result = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(Folder))
            return result;
        foreach (var file in Directory.GetFiles(Folder, "*.png"))
        {
            try
            {
                result[Path.GetFileNameWithoutExtension(file).ToUpperInvariant()] = await Load(file);
            }
            catch (Exception e)
            {
                Plugin.Log.Warning(e, $"Could not load the sprite {file}");
            }
        }

        return result;
    }

    private static async Task<Sprite> Load(string file)
    {
        using var source = await Plugin.TextureProvider.CreateFromImageAsync(await File.ReadAllBytesAsync(file));
        var (spec, raw) = await Plugin.TextureReadback.GetRawImageAsync(source, new TextureModificationArgs(), true);
        var width = spec.Width;
        var height = spec.Height;
        // B8G8R8A8 (87) or R8G8B8A8 (28): read as BGRA.
        var rgba = spec.DxgiFormat == 28;

        var big = RawImageSpecification.Bgra32(width * Upscale, height * Upscale);
        var normal = new byte[width * Upscale * height * Upscale * 4];
        var white = new byte[normal.Length];
        var grey = new byte[normal.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = y * spec.Pitch + x * 4;
                byte b = raw[at], g = raw[at + 1], r = raw[at + 2], a = raw[at + 3];
                if (rgba)
                    (b, r) = (r, b);
                var lum = (byte)((r * 30 + g * 59 + b * 11) / 100 * 0.7f);
                for (var dy = 0; dy < Upscale; dy++)
                {
                    for (var dx = 0; dx < Upscale; dx++)
                    {
                        var to = ((y * Upscale + dy) * width * Upscale + x * Upscale + dx) * 4;
                        normal[to] = b;
                        normal[to + 1] = g;
                        normal[to + 2] = r;
                        normal[to + 3] = a;
                        white[to] = white[to + 1] = white[to + 2] = 255;
                        white[to + 3] = a;
                        grey[to] = grey[to + 1] = grey[to + 2] = lum;
                        grey[to + 3] = a;
                    }
                }
            }
        }

        var name = Path.GetFileName(file);
        return new Sprite(await Plugin.TextureProvider.CreateFromRawAsync(big, normal, $"Raid Companion sprite {name}"),
                          await Plugin.TextureProvider.CreateFromRawAsync(big, white, $"Raid Companion sprite {name} (hit)"),
                          await Plugin.TextureProvider.CreateFromRawAsync(big, grey, $"Raid Companion sprite {name} (down)"),
                          width, height);
    }

    // ---- The fight, in pixels of the sprites ("units"): the scene is 100 units high.

    private sealed class Member
    {
        public required string Job;
        public required JobRole Role;
        public float X, Y, Hp = 1, Shown = 1, Lunge, Hit, Next;
        public float? DownAt;
    }

    private record struct Particle(Vector2 Pos, Vector2 Velocity, float Life, Vector4 Color);

    private record struct Number(Vector2 Pos, int Value, Vector4 Color, float Life);

    private const float SceneHeight = 100, Floor = 98, CastTime = 1.4f, LungeTime = 0.35f;

    private static readonly Vector4 HealColor = new(0.48f, 0.88f, 0.54f, 1f);
    private static readonly Vector4 HurtColor = new(1f, 0.5f, 0.5f, 1f);
    private static readonly Vector4 FireColor = new(1f, 0.42f, 0.29f, 1f);
    private static readonly Vector4 SparkColor = new(1f, 0.91f, 0.63f, 1f);

    private readonly Random random = new();
    private readonly List<Member> party = [];
    private readonly List<Particle> particles = [];
    private readonly List<Number> numbers = [];
    private readonly List<(float Delay, int Damage)> pending = [];
    private float bossHp, bossShown, bossHit, bossCast, bossNext;
    private float time, clearedAt = -1, bossLeft, sceneWidth;
    private string partyKey = string.Empty;

    private void Reset(List<string> jobs)
    {
        party.Clear();
        for (var i = 0; i < jobs.Count; i++)
        {
            party.Add(new Member
            {
                Job = jobs[i],
                Role = JobRoles.Of(jobs[i]),
                X = 10 + i * 50,
                Y = 34 + i % 2 * 6,
                Next = 0.6f + i * 0.7f,
            });
        }

        particles.Clear();
        numbers.Clear();
        pending.Clear();
        bossHp = bossShown = 1;
        bossHit = 0;
        bossCast = -1;
        bossNext = 3;
        time = 0;
        clearedAt = -1;
    }

    private static readonly HashSet<string> Melee = new(StringComparer.OrdinalIgnoreCase)
        { "MNK", "DRG", "NIN", "SAM", "RPR", "VPR", "PGL", "LNC", "ROG" };

    // Left to right, the boss on the right: healers at the back, then ranged and casters, melee, the tank in front.
    private static int Rank(string job) => JobRoles.Of(job) switch
    {
        JobRole.Healer => 0,
        JobRole.Tank => 3,
        _ => Melee.Contains(job) ? 2 : 1,
    };

    // Up to four job pictures: the player's job first if there is one, then tanks, healers and the others.
    private static List<string> PickParty(Dictionary<string, Sprite> sprites)
    {
        var jobs = sprites.Keys.Where(k => k != "BOSS").ToList();
        var mine = GearCatalog.CurrentJob().ToUpperInvariant();
        return jobs.OrderBy(j => j == mine ? 0 : 1).ThenBy(j => JobRoles.Of(j)).ThenBy(j => j).Take(4)
                   .OrderBy(Rank).ThenBy(j => j).ToList();
    }

    /// <summary>Draws the scene in a band of the window, the given screen rectangle.</summary>
    public void Draw(Vector2 min, Vector2 max)
    {
        if (Sprites is not { } sprites)
            return;
        var jobs = PickParty(sprites);
        if (jobs.Count == 0)
            return;
        var key = string.Join(",", jobs);
        if (key != partyKey)
        {
            partyKey = key;
            Reset(jobs);
        }

        var boss = sprites.GetValueOrDefault("BOSS");
        var bossWidth = boss?.Width ?? 60f;
        var bossHeight = boss?.Height ?? 60f;

        // As big as the band allows, the party and the boss side by side; on the band's bottom.
        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(min, max, true);
        var unit = Math.Min((max.Y - min.Y) / SceneHeight, (max.X - min.X) / (party.Count * 50 + 30 + bossWidth + 24));
        min.Y = max.Y - SceneHeight * unit;
        var width = (max.X - min.X) / unit;

        if (Fx.Enabled)
            Update(Fx.Delta);

        sceneWidth = width;
        bossLeft = width - bossWidth - 10;
        var bossPos = new Vector2(bossLeft, Floor - bossHeight + MathF.Round(MathF.Sin(time * 1.6f) * 1.5f));
        Vector2 At(Vector2 v) => min + v * unit;
        void Box(float x, float y, float w, float h, Vector4 color, float alpha = 1) =>
            dl.AddRectFilled(At(new Vector2(x, y)), At(new Vector2(x + w, y + h)), Theme.U32(color, alpha));

        // The floor.
        for (var x = 0f; x < width; x += 6)
            Box(x, Floor + x / 6 % 2, 4, 1, Theme.TextFaint, 0.5f);

        // The boss: its picture (or a plain one), fading once beaten, its HP and its cast bars.
        var bossAlpha = clearedAt >= 0 ? Math.Max(0, 1 - (time - clearedAt) * 0.8f) : 1;
        if (boss is not null)
        {
            var texture = bossHit > 0 ? boss.White : boss.Normal;
            // Mirrored: the picture looks to the right, the party is on the left.
            dl.AddImage(texture.Handle, At(bossPos), At(bossPos + new Vector2(boss.Width, boss.Height)), new Vector2(1, 0), new Vector2(0, 1),
                        Theme.U32(Vector4.One, bossAlpha));
        }
        else
        {
            DrawPlainBoss(Box, bossPos, bossAlpha);
        }

        var barX = width - 100;
        Box(barX, 4, 90, 3, Theme.Ink with { W = 1 }, 0.8f);
        Box(barX, 4, 90 * Math.Max(0, bossShown), 3, Theme.Bad, 0.7f);
        Box(barX, 4, 90 * Math.Max(0, bossHp), 3, Theme.Bad);
        // Its cast bar, on the boss itself, a frame around it.
        if (bossCast >= 0)
        {
            var castWidth = MathF.Round(bossWidth * 0.6f);
            var castX = MathF.Round(bossPos.X + (bossWidth - castWidth) / 2);
            var castY = MathF.Round(bossPos.Y + bossHeight * 0.55f);
            var k = Math.Min(1, bossCast / CastTime);
            Box(castX - 1, castY - 1, castWidth + 2, 5, Theme.Ink with { W = 1 }, 0.9f);
            Box(castX, castY, castWidth * k, 3, Theme.Gold);
            Box(castX, castY, castWidth * k, 1, Theme.GoldBright);
            Box(castX + castWidth * k - 1, castY - 1, 2, 5, Theme.GoldBright, 0.5f + 0.5f * MathF.Sin(time * 20));
        }

        // The party, turned to the boss: a hop on an attack, a white flash on a hit, falling down grey.
        foreach (var (member, i) in party.Select((m, i) => (m, i)))
        {
            if (!sprites.TryGetValue(member.Job, out var sprite))
                continue;
            var pos = new Vector2(member.X, member.Y);
            if (member.Lunge > 0)
            {
                var k = 1 - member.Lunge / LungeTime;
                pos += new Vector2(member.Role == JobRole.Healer ? 0 : MathF.Sin(k * MathF.PI) * 10, -MathF.Sin(k * MathF.PI) * 3);
            }

            if (member.DownAt is null && Fx.Enabled)
                pos.Y += MathF.Floor(time * 2 + i * 0.5f) % 2;

            var texture = member.DownAt is not null ? sprite.Grey : member.Hit > 0 ? sprite.White : sprite.Normal;
            var size = new Vector2(sprite.Width, sprite.Height);
            // Corners from the top left, clockwise; the picture mirrored (u from 1 to 0).
            var corners = new[] { pos, pos + size with { Y = 0 }, pos + size, pos + size with { X = 0 } };
            if (member.DownAt is { } down)
            {
                // Falling backwards, around the feet.
                var angle = -Math.Min(1, (time - down) * 3) * MathF.PI / 2;
                var pivot = pos + new Vector2(size.X / 2, size.Y - 4);
                var (sin, cos) = MathF.SinCos(angle);
                for (var c = 0; c < 4; c++)
                {
                    var d = corners[c] - pivot;
                    corners[c] = pivot + new Vector2(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos);
                }
            }

            dl.AddImageQuad(texture.Handle, At(corners[0]), At(corners[1]), At(corners[2]), At(corners[3]),
                            new Vector2(1, 0), new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1));

            // Its HP under it.
            var hpX = member.X + 14;
            var hpY = member.Y + sprite.Height + 2;
            Box(hpX, hpY, 36, 2, Theme.Ink with { W = 1 }, 0.8f);
            Box(hpX, hpY, 36 * Math.Max(0, member.Shown), 2, member.Hp < 0.3f ? HurtColor : HealColor);
        }

        foreach (var p in particles)
            Box(p.Pos.X, p.Pos.Y, 1, 1, p.Color, Fx.Clamp01(p.Life * 2));
        foreach (var n in numbers)
            Digits(Box, n.Value.ToString(), n.Pos, n.Color, Fx.Clamp01(n.Life * 2));

        if (clearedAt >= 0)
            Word(Box, "CLEAR", new Vector2(width / 2 - 10, 40), Theme.Gold, Fx.Clamp01((time - clearedAt) * 2));

        dl.PopClipRect();
    }

    // ---- The fight going on.

    private void Update(float dt)
    {
        time += dt;
        bossHit -= dt;

        if (clearedAt >= 0)
        {
            if (random.NextSingle() < 0.3f)
                particles.Add(new Particle(new Vector2(sceneWidth / 2 - 40 + random.NextSingle() * 80, 40 + random.NextSingle() * 20),
                                           new Vector2((random.NextSingle() - 0.5f) * 40, -20), 0.8f, Theme.Gold));
            if (time - clearedAt > 4)
                partyKey = string.Empty;
        }
        else
        {
            foreach (var member in party)
                Act(member, dt);

            // Damage lands a little after the hop.
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var (delay, damage) = pending[i];
                delay -= dt;
                if (delay > 0)
                {
                    pending[i] = (delay, damage);
                    continue;
                }

                pending.RemoveAt(i);
                HitBoss(damage);
            }

            BossTurn(dt);
        }

        foreach (var member in party)
        {
            member.Shown += (member.Hp - member.Shown) * Math.Min(1, dt * 5);
            member.Lunge -= dt;
            member.Hit -= dt;
        }

        bossShown += (bossHp - bossShown) * Math.Min(1, dt * 3);
        for (var i = particles.Count - 1; i >= 0; i--)
        {
            var p = particles[i];
            p.Life -= dt;
            p.Pos += p.Velocity * dt;
            if (p.Life <= 0)
                particles.RemoveAt(i);
            else
                particles[i] = p;
        }

        for (var i = numbers.Count - 1; i >= 0; i--)
        {
            var n = numbers[i];
            n.Life -= dt;
            n.Pos -= new Vector2(0, 10 * dt);
            if (n.Life <= 0)
                numbers.RemoveAt(i);
            else
                numbers[i] = n;
        }
    }

    private void Act(Member member, float dt)
    {
        if (member.DownAt is not null)
            return;
        member.Next -= dt;
        if (member.Next > 0)
            return;

        var hurt = party.Where(m => m.DownAt is null && m.Hp < 0.75f).OrderBy(m => m.Hp).FirstOrDefault();
        if (member.Role == JobRole.Healer && hurt is not null)
        {
            hurt.Hp = Math.Min(1, hurt.Hp + 0.35f);
            for (var k = 0; k < 10; k++)
                particles.Add(new Particle(new Vector2(hurt.X + 20 + random.NextSingle() * 24, hurt.Y + 50),
                                           new Vector2(0, -12 - random.NextSingle() * 14), 1.2f, HealColor));
            numbers.Add(new Number(new Vector2(hurt.X + 26, hurt.Y + 8), 4000 + random.Next(3000), HealColor, 1));
            member.Lunge = LungeTime * 0.7f;
            member.Next = 1.6f;
            return;
        }

        member.Lunge = LungeTime;
        member.Next = 1.1f + random.NextSingle() * 0.8f;
        pending.Add((0.18f, 8000 + random.Next(14000)));
    }

    private void HitBoss(int damage)
    {
        var bossX = BossX;
        bossHp = Math.Max(0, bossHp - damage / 600000f);
        bossHit = 0.12f;
        var crit = random.NextSingle() < 0.25f;
        numbers.Add(new Number(new Vector2(bossX + random.NextSingle() * 50, 30 + random.NextSingle() * 10), damage,
                               crit ? Theme.GoldBright : Vector4.One, 1));
        for (var k = 0; k < 6; k++)
            particles.Add(new Particle(new Vector2(bossX + 10 + random.NextSingle() * 30, 50 + random.NextSingle() * 20),
                                       new Vector2((random.NextSingle() - 0.5f) * 60, -30 * random.NextSingle()), 0.4f, SparkColor));
        if (bossHp <= 0)
            clearedAt = time;
    }

    // Where numbers and sparks show on the boss: its left part, facing the party.
    private float BossX => bossLeft + 10;

    private void BossTurn(float dt)
    {
        bossNext -= dt;
        if (bossCast < 0 && bossNext <= 0)
            bossCast = 0;
        if (bossCast < 0)
            return;
        bossCast += dt;
        if (bossCast <= CastTime)
            return;

        bossCast = -1;
        bossNext = 4 + random.NextSingle() * 2;
        foreach (var member in party.Where(m => m.DownAt is null))
        {
            var damage = member.Role == JobRole.Tank ? 0.18f : 0.32f + random.NextSingle() * 0.12f;
            member.Hp = Math.Max(0, member.Hp - damage);
            member.Hit = 0.15f;
            numbers.Add(new Number(new Vector2(member.X + 26, member.Y + 4), (int)(damage * 60000), HurtColor, 1));
            if (member.Hp <= 0)
                member.DownAt = time;
        }

        for (var k = 0; k < 30; k++)
            particles.Add(new Particle(new Vector2(10 + random.NextSingle() * (party.Count * 50), Floor - 2),
                                       new Vector2((random.NextSingle() - 0.5f) * 20, -20 - random.NextSingle() * 40), 0.6f, FireColor));
    }

    // ---- Pixel drawing.

    private delegate void BoxDrawer(float x, float y, float w, float h, Vector4 color, float alpha = 1);

    private void DrawPlainBoss(BoxDrawer box, Vector2 at, float alpha)
    {
        var flash = bossHit > 0;
        var body = flash ? Vector4.One : new Vector4(0.29f, 0.16f, 0.37f, 1);
        var dark = flash ? Vector4.One : new Vector4(0.17f, 0.09f, 0.22f, 1);
        var horn = flash ? Vector4.One : new Vector4(0.79f, 0.7f, 0.48f, 1);
        var eye = bossCast >= 0 ? HurtColor : Theme.GoldBright;
        var (x, y) = (at.X + 14, at.Y + 6);
        box(x - 4, y, 6, 4, horn, alpha);
        box(x - 8, y - 4, 4, 6, horn, alpha);
        box(x + 30, y, 6, 4, horn, alpha);
        box(x + 36, y - 4, 4, 6, horn, alpha);
        box(x, y + 2, 32, 26, body, alpha);
        box(x - 4, y + 12, 40, 22, body, alpha);
        box(x + 4, y + 34, 8, 10, dark, alpha);
        box(x + 22, y + 34, 8, 10, dark, alpha);
        box(x + 8, y + 10, 4, 3, eye, alpha);
        box(x + 20, y + 10, 4, 3, eye, alpha);
    }

    // 3x5 pixel figures and letters.
    private static readonly Dictionary<char, string> Glyphs = new()
    {
        ['0'] = "111101101101111", ['1'] = "010110010010111", ['2'] = "111001111100111", ['3'] = "111001111001111",
        ['4'] = "101101111001001", ['5'] = "111100111001111", ['6'] = "111100111101111", ['7'] = "111001001001001",
        ['8'] = "111101111101111", ['9'] = "111101111001111",
        ['C'] = "111100100100111", ['L'] = "100100100100111", ['E'] = "111100111100111", ['A'] = "111101111101101",
        ['R'] = "110101110101101", ['W'] = "101101101111101", ['I'] = "111010010010111", ['P'] = "111101111100100",
    };

    private static void Digits(BoxDrawer box, string text, Vector2 at, Vector4 color, float alpha) => Word(box, text, at, color, alpha);

    private static void Word(BoxDrawer box, string text, Vector2 at, Vector4 color, float alpha)
    {
        for (var k = 0; k < text.Length; k++)
        {
            if (!Glyphs.TryGetValue(text[k], out var glyph))
                continue;
            for (var i = 0; i < 15; i++)
            {
                if (glyph[i] == '1')
                    box(MathF.Round(at.X) + k * 4 + i % 3, MathF.Round(at.Y) + i / 3, 1, 1, color, alpha);
            }
        }
    }
}
