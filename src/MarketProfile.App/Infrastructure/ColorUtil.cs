using System.Globalization;
using System.Windows.Media;

namespace MarketProfile.App.Infrastructure;

/// <summary>
/// Pomocnicze funkcje do kolorów zapisywanych w ustawieniach jako tekst "#RRGGBB".
/// </summary>
public static class ColorUtil
{
    /// <summary>Gotowe kolory do szybkiego wyboru (paleta w polach wyboru koloru).</summary>
    public static IReadOnlyList<string> Palette { get; } =
    [
        "#FFD54F", "#FFA726", "#FF5252", "#FF5050", "#E040FB", "#7C4DFF",
        "#448AFF", "#4090FF", "#40C4FF", "#64FFDA", "#69F0AE", "#B2FF59",
        "#F0C030", "#FF9E40", "#5A6E8C", "#7A8CA8", "#8090A0", "#C8CDD4",
        "#FFFFFF", "#9E9E9E", "#616161", "#000000",
    ];

    /// <summary>Sprawdza, czy tekst jest kolorem w formacie #RRGGBB, i zwraca ten kolor.</summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        text = text?.Trim() ?? string.Empty;

        if (text.Length != 7 || text[0] != '#' ||
            !int.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    /// <summary>
    /// Tworzy zamrożony pędzel z koloru "#RRGGBB" z podanym kryciem (0–100%).
    /// Przy błędnym kolorze używa koloru zapasowego.
    /// </summary>
    public static SolidColorBrush ToBrush(string? text, int opacityPercent = 100, string fallback = "#FFFFFF")
    {
        if (!TryParse(text, out var color))
        {
            TryParse(fallback, out color);
        }

        color.A = (byte)Math.Round(Math.Clamp(opacityPercent, 0, 100) * 2.55);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
