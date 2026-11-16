using System.Windows.Media;
using MarketProfile.App.Infrastructure;
using MarketProfile.App.Settings;

namespace MarketProfile.App.Controls;

/// <summary>Średnia krocząca do narysowania: okres i pióro linii.</summary>
public sealed record MovingAverageLine(int Period, Pen Pen, Brush Brush);

/// <summary>
/// Wygląd wykresu D/W/M przygotowany z ustawień (<see cref="CandleChartStyle"/>): zamrożone pędzle i pióra,
/// żeby kontrolka nie tworzyła ich przy każdym rysowaniu.
/// </summary>
public sealed class CandleChartAppearance
{
    public static CandleChartAppearance Default { get; } = Create(new CandleChartStyle());

    public CandleChartType Type { get; private init; }
    public Brush UpBrush { get; private init; } = Brushes.Green;
    public Brush DownBrush { get; private init; } = Brushes.Red;
    public Pen UpPen { get; private init; } = new(Brushes.Green, 1);
    public Pen DownPen { get; private init; } = new(Brushes.Red, 1);
    public Pen UpThickPen { get; private init; } = new(Brushes.Green, 2);   // słupki OHLC przy szerokich świecach
    public Pen DownThickPen { get; private init; } = new(Brushes.Red, 2);
    public bool ShowVolume { get; private init; }
    public Brush UpVolumeBrush { get; private init; } = Brushes.Green;
    public Brush DownVolumeBrush { get; private init; } = Brushes.Red;
    public IReadOnlyList<MovingAverageLine> MovingAverages { get; private init; } = [];

    public static CandleChartAppearance Create(CandleChartStyle style)
    {
        var up = ColorUtil.ToBrush(style.UpColor, 100, "#30C070");
        var down = ColorUtil.ToBrush(style.DownColor, 100, "#E05050");
        var volumeOpacity = Math.Clamp(style.VolumeOpacity, 5, 100);

        return new CandleChartAppearance
        {
            Type = style.Type,
            UpBrush = Frozen(up),
            DownBrush = Frozen(down),
            UpPen = Frozen(new Pen(up, 1)),
            DownPen = Frozen(new Pen(down, 1)),
            UpThickPen = Frozen(new Pen(up, 2)),
            DownThickPen = Frozen(new Pen(down, 2)),
            ShowVolume = style.ShowVolume,
            UpVolumeBrush = Frozen(ColorUtil.ToBrush(style.UpColor, volumeOpacity, "#30C070")),
            DownVolumeBrush = Frozen(ColorUtil.ToBrush(style.DownColor, volumeOpacity, "#E05050")),
            MovingAverages = style.MovingAverages
                .Where(m => m.Enabled && m.Period >= 1)
                .Select(m =>
                {
                    var brush = Frozen(ColorUtil.ToBrush(m.Color, 100, "#F0C030"));
                    var pen = Frozen(new Pen(brush, Math.Clamp(m.Thickness, 1, 10)) { LineJoin = PenLineJoin.Round });
                    return new MovingAverageLine(m.Period, pen, brush);
                })
                .ToList(),
        };
    }

    private static T Frozen<T>(T freezable) where T : System.Windows.Freezable
    {
        if (freezable.CanFreeze)
        {
            freezable.Freeze();
        }

        return freezable;
    }
}
