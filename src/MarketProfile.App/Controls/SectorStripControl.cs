using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MarketProfile.App.ViewModels;
using MarketProfile.Core;

namespace MarketProfile.App.Controls;

/// <summary>
/// Pasek 11 sektorów S&amp;P 500: kafelki równej szerokości w jednym rzędzie (kolejność – od największej wagi).
/// Kafelek: symbol i nazwa sektora, waga w S&amp;P 500 oraz zmiana od otwarcia sesji kasowej; tło według poziomu zmiany
/// (ciemnozielony &gt; +1%, zielony +0,5…1%, jasnozielony 0…+0,5%, jasnoczerwony 0…−0,5%, czerwony −0,5…−1%,
/// ciemnoczerwony &lt; −1%). Po najechaniu – podpowiedź z ETF-em i cenami.
/// </summary>
public sealed class SectorStripControl : FrameworkElement
{
    public static readonly DependencyProperty TilesProperty = DependencyProperty.Register(
        nameof(Tiles), typeof(IReadOnlyList<SectorTileView>), typeof(SectorStripControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Kafelki sektorów (od największej wagi).</summary>
    public IReadOnlyList<SectorTileView>? Tiles
    {
        get => (IReadOnlyList<SectorTileView>?)GetValue(TilesProperty);
        set => SetValue(TilesProperty, value);
    }

    private const double TileGap = 3;

    private static readonly Typeface UiTypeface = new("Segoe UI");
    private static readonly Typeface BoldTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface EmojiTypeface = new("Segoe UI Emoji");
    private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1C)));
    private static readonly Pen BorderPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), 1));
    private static readonly Brush LightText = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF6)));
    private static readonly Brush DarkText = Frozen(new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1B)));
    private static readonly Brush NeutralText = Frozen(new SolidColorBrush(Color.FromRgb(0xA8, 0xAF, 0xB8)));

    /// <summary>Tło kafelka i kolor tekstu dla poziomu zmiany.</summary>
    private static (Brush Background, Brush Text) Colors(SectorChangeLevel level) => level switch
    {
        SectorChangeLevel.StrongUp => (Brush(0x0B, 0x6B, 0x2E), LightText),
        SectorChangeLevel.Up => (Brush(0x1F, 0x9E, 0x4C), LightText),
        SectorChangeLevel.SlightUp => (Brush(0x8F, 0xDF, 0xA6), DarkText),
        SectorChangeLevel.SlightDown => (Brush(0xF5, 0xAE, 0xAE), DarkText),
        SectorChangeLevel.Down => (Brush(0xE0, 0x4A, 0x4A), LightText),
        SectorChangeLevel.StrongDown => (Brush(0x9C, 0x1C, 0x1C), LightText),
        _ => (Brush(0x24, 0x29, 0x31), NeutralText),
    };

    private static readonly Dictionary<int, Brush> BrushCache = [];

    private static Brush Brush(byte r, byte g, byte b)
    {
        var key = (r << 16) | (g << 8) | b;
        if (!BrushCache.TryGetValue(key, out var brush))
        {
            brush = Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));
            BrushCache[key] = brush;
        }

        return brush;
    }

    public SectorStripControl()
    {
        ClipToBounds = true;
        ToolTipService.SetInitialShowDelay(this, 300);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width, 40);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        var tiles = Tiles;
        if (tiles is not { Count: > 0 })
        {
            return;
        }

        var tileWidth = (width - TileGap * (tiles.Count + 1)) / tiles.Count;
        if (tileWidth < 30)
        {
            return;
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var rect = new Rect(TileGap + i * (tileWidth + TileGap), 2, tileWidth, height - 4);
            var (background, text) = Colors(tile.Level);
            dc.DrawRoundedRectangle(background, BorderPen, rect, 3, 3);

            // Góra: symbol + nazwa (przycinana); dół: waga po lewej, zmiana od otwarcia po prawej.
            var icon = new FormattedText(tile.Sector.Icon, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, EmojiTypeface, 12, text, pixelsPerDip);
            dc.DrawText(icon, new Point(rect.X + 5, rect.Y + 2));
            var name = Text(tile.Sector.ShortName, 10.5, text, UiTypeface, pixelsPerDip, Math.Max(1, rect.Width - icon.Width - 12));
            dc.DrawText(name, new Point(rect.X + icon.Width + 8, rect.Y + 3));

            var weight = Text($"{tile.Weight.ToString("0.0", CultureInfo.CurrentCulture)}%", 10, text, UiTypeface, pixelsPerDip);
            var change = Text(tile.ChangePercent is { } c
                    ? (c >= 0 ? "+" : "−") + Math.Abs(c).ToString("0.00", CultureInfo.CurrentCulture) + "%"
                    : "–",
                12, text, BoldTypeface, pixelsPerDip);
            var bottom = rect.Bottom - change.Height - 1;
            dc.PushOpacity(0.8);
            dc.DrawText(weight, new Point(rect.X + 6, bottom + (change.Height - weight.Height)));
            dc.Pop();
            dc.DrawText(change, new Point(rect.Right - change.Width - 6, bottom));
        }
    }

    /// <summary>Podpowiedź kafelka pod kursorem.</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var tiles = Tiles;
        if (tiles is not { Count: > 0 })
        {
            ToolTip = null;
            return;
        }

        var tileWidth = (ActualWidth - TileGap * (tiles.Count + 1)) / tiles.Count;
        var index = (int)Math.Floor((e.GetPosition(this).X - TileGap / 2) / (tileWidth + TileGap));
        var tip = index >= 0 && index < tiles.Count ? tiles[index].ToolTip : null;
        if (!Equals(ToolTip, tip))
        {
            ToolTip = tip;
        }
    }

    private static FormattedText Text(string text, double size, Brush brush, Typeface typeface, double pixelsPerDip, double? maxWidth = null)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, pixelsPerDip);
        if (maxWidth is { } max)
        {
            formatted.MaxTextWidth = max;
            formatted.MaxLineCount = 1;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
        }

        return formatted;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
