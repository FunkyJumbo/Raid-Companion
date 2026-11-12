using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Dalamud.Game;

namespace DpsInServerBar;

/// <summary>
/// The plugin's texts in the chosen language. Texts are written in English in the code and looked up in the
/// translation tables (Localization/fr.json, Localization/de.json, embedded); a text without translation stays in English.
/// </summary>
public static class Loc
{
    public static readonly (string Code, string Name)[] Languages =
    [
        ("auto", "Auto (Dalamud language)"),
        ("en", "English"),
        ("fr", "Français"),
        ("de", "Deutsch"),
        ("al", "Al Bhed"),
    ];

    private static Dictionary<string, string> table = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

    /// <summary>Language in use: en, fr, de, or al (Al Bhed: the English texts through the FFX cipher).</summary>
    public static string Code { get; private set; } = "en";

    /// <summary>The game data language matching <see cref="Code"/> (action and item names).</summary>
    public static ClientLanguage GameLanguage => Code switch { "fr" => ClientLanguage.French, "de" => ClientLanguage.German, _ => ClientLanguage.English };

    /// <summary>Raised after the language changes (texts computed once must be computed again).</summary>
    public static event Action? Changed;

    /// <summary>Picks the language: "auto" follows Dalamud's, anything other than fr / de falls back to English.</summary>
    public static void Set(string setting, string dalamudLanguage)
    {
        var code = setting == "auto" ? dalamudLanguage : setting;
        code = code is "fr" or "de" or "al" ? code : "en";
        if (code == Code && (code is "en" or "al" || table.Count > 0))
            return;

        Code = code;
        table = code is "en" or "al" ? new(StringComparer.Ordinal) : Load(code);
        lock (Cache)
            Cache.Clear();
        Changed?.Invoke();
    }

    private static Dictionary<string, string> Load(string code)
    {
        try
        {
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"DpsInServerBar.Localization.{code}.json");
            if (stream is null)
                return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) is { } loaded
                       ? new Dictionary<string, string>(loaded, StringComparer.Ordinal)
                       : new(StringComparer.Ordinal);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, $"Could not load the {code} translations");
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>The text in the current language. What follows "##" (an ImGui id) is kept as it is.</summary>
    public static string T(string text)
    {
        if (Code == "al")
            return AlBhedText(text);
        if (table.Count == 0 || text.Length == 0)
            return text;
        lock (Cache)
        {
            if (Cache.TryGetValue(text, out var done))
                return done;

            var hash = text.IndexOf("##", StringComparison.Ordinal);
            var visible = hash >= 0 ? text[..hash] : text;
            var result = table.TryGetValue(visible, out var translated) && translated.Length > 0 ? translated : visible;
            if (hash >= 0)
                result += text[hash..];
            Cache[text] = result;
            return result;
        }
    }

    /// <summary>A name from the game data (action, status, item) in the current language: ciphered in Al Bhed.</summary>
    public static string Game(string name) => Code == "al" ? AlBhed(name) : name;

    // English -> Al Bhed, letter by letter (A->Y, B->P, C->L...).
    private const string AlBhedLetters = "YPLTAVKREZGMSHUBXNCDIJFQOW";

    private static string AlBhedText(string text)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(text, out var done))
                return done;

            // What follows "##" is an ImGui id, kept as it is.
            var hash = text.IndexOf("##", StringComparison.Ordinal);
            var result = hash >= 0 ? AlBhed(text[..hash]) + text[hash..] : AlBhed(text);
            Cache[text] = result;
            return result;
        }
    }

    /// <summary>The text in Al Bhed. Format items ({0}, {1:0.0%}) are kept as they are.</summary>
    public static string AlBhed(string text)
    {
        var chars = text.ToCharArray();
        var depth = 0;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c == '{')
                depth++;
            else if (c == '}')
                depth = Math.Max(0, depth - 1);
            else if (depth == 0 && c is >= 'a' and <= 'z')
                chars[i] = char.ToLowerInvariant(AlBhedLetters[c - 'a']);
            else if (depth == 0 && c is >= 'A' and <= 'Z')
                chars[i] = AlBhedLetters[c - 'A'];
        }

        return new string(chars);
    }

    /// <summary>The text in the current language, with {0}, {1}... filled in.</summary>
    public static string F(string format, params object?[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, T(format), args);
        }
        catch (FormatException)
        {
            // A broken translation: fall back on the English text.
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
    }
}
