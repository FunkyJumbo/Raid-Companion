using System;
using Dalamud.Game;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.UI;

/// <summary>The game's sky for the ambience: the weather where you are.</summary>
public static class GameSky
{
    public enum Sky
    {
        Clear,
        Rain,
        Snow,
    }

    private static float checkedAt = -100f;
    private static Sky weather;

    /// <summary>The weather in the zone you are in, read once a second.</summary>
    public static Sky Weather
    {
        get
        {
            if (Fx.Now - checkedAt < 1f && Fx.Now >= checkedAt)
                return weather;
            checkedAt = Fx.Now;
            try
            {
                weather = Read();
            }
            catch (Exception)
            {
                weather = Sky.Clear;
            }

            return weather;
        }
    }

    private static unsafe Sky Read()
    {
        var manager = WeatherManager.Instance();
        if (manager == null)
            return Sky.Clear;
        var id = manager->GetCurrentWeather();
        if (id == 0 || !Plugin.DataManager.GetExcelSheet<Weather>(ClientLanguage.English).TryGetRow(id, out var row))
            return Sky.Clear;
        var name = row.Name.ExtractText();
        if (name.Contains("Rain", StringComparison.OrdinalIgnoreCase) || name.Contains("Showers", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Thunderstorm", StringComparison.OrdinalIgnoreCase))
            return Sky.Rain;
        if (name.Contains("Snow", StringComparison.OrdinalIgnoreCase) || name.Contains("Blizzard", StringComparison.OrdinalIgnoreCase))
            return Sky.Snow;
        return Sky.Clear;
    }
}
