using System.Windows;
using System.Windows.Media;
using MarketProfile.App.Infrastructure;
using MarketProfile.App.Settings;
using MarketProfile.Core;

namespace MarketProfile.App.Controls;

/// <summary>
/// Kolory jednej sesji gotowe do rysowania (pędzle tworzone raz, a nie przy każdym odświeżeniu).
/// </summary>
public sealed class SessionStyle
{
    /// <summary>Pędzle liter: przy kolorach "tęczy" – po jednym na okres, przy jednym kolorze – jeden.</summary>
    public required Brush[] LetterBrushes { get; init; }

    public required Brush PocBrush { get; init; }
    public required Pen PocPen { get; init; }
    public required Brush ValueAreaBrush { get; init; }
    public required Brush IbBrush { get; init; }

    /// <summary>Pędzel litery danego okresu TPO.</summary>
    public Brush LetterBrush(int period) => LetterBrushes[period % LetterBrushes.Length];

    /// <summary>Tworzy styl sesji z ustawień kolorów.</summary>
    public static SessionStyle Create(SessionColorScheme scheme)
    {
        var poc = ColorUtil.ToBrush(scheme.PocColor);
        var pocPen = new Pen(poc, 1);
        pocPen.Freeze();

        return new SessionStyle
        {
            LetterBrushes = scheme.LetterMode == LetterColorMode.Rainbow
                ? CreateRainbow()
                : [ColorUtil.ToBrush(scheme.LetterColor)],
            PocBrush = poc,
            PocPen = pocPen,
            ValueAreaBrush = ColorUtil.ToBrush(scheme.ValueAreaColor, scheme.ValueAreaOpacity),
            IbBrush = ColorUtil.ToBrush(scheme.IbColor),
        };
    }

    /// <summary>Paleta "tęczy" – 16 kolorów dla kolejnych okresów TPO (przejście przez barwy koła kolorów).</summary>
    private static Brush[] CreateRainbow()
    {
        var brushes = new Brush[16];
        for (var i = 0; i < brushes.Length; i++)
        {
            var brush = new SolidColorBrush(FromHsv(i * 360.0 / brushes.Length, 0.55, 0.95));
            brush.Freeze();
            brushes[i] = brush;
        }

        return brushes;
    }

    /// <summary>Zamienia kolor z modelu HSV (barwa, nasycenie, jasność) na RGB.</summary>
    private static Color FromHsv(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = (hue / 60) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}

/// <summary>
/// Styl wykresu: wygląd profilu wolumenowego i kolory profili poszczególnych sesji.
/// Tworzony z ustawień przez model widoku i przekazywany do kontrolki wykresu.
/// </summary>
public sealed class ChartStyle
{
    public double VolumeGap { get; init; } = 6;
    public double VolumeWidth { get; init; } = 70;
    public bool VolumeMirrored { get; init; }
    public VolumeScaleMode VolumeScale { get; init; } = VolumeScaleMode.Linear;

    /// <summary>Limit wolumenu w procentach maksimum (100 = bez limitu).</summary>
    public double VolumeCapPercent { get; init; } = 100;

    /// <summary>
    /// Długość słupka wolumenu w pikselach z uwzględnieniem skali i limitu.
    /// Zwraca też informację, czy słupek został obcięty (wolumen powyżej limitu).
    /// </summary>
    public (double Length, bool Clipped) VolumeBarLength(long volume, long maxVolume)
    {
        var cap = Math.Max(1.0, maxVolume * VolumeCapPercent / 100.0);
        var clipped = volume > cap;
        var value = Math.Min(volume, cap);

        double Scale(double v) => VolumeScale switch
        {
            VolumeScaleMode.SquareRoot => Math.Sqrt(v),
            VolumeScaleMode.Logarithmic => Math.Log(1 + v),
            _ => v,
        };

        return (Math.Max(1, VolumeWidth * Scale(value) / Scale(cap)), clipped);
    }
    // --- Elementy profilu TPO ---

    /// <summary>Czy rysować pasek Initial Balance.</summary>
    public bool ShowInitialBalance { get; init; } = true;

    /// <summary>Czy rysować POC (ramka wiersza, wyróżnione litery, wartość w nagłówku).</summary>
    public bool ShowPoc { get; init; } = true;

    // --- Strzałki otwarcia i zamknięcia profilu ---
    public bool ShowOpenArrow { get; init; }
    public bool ShowCloseArrow { get; init; }
    public Brush OpenArrowBrush { get; init; } = Brushes.DeepSkyBlue;
    public Brush CloseArrowBrush { get; init; } = Brushes.Orange;
    public double ArrowThickness { get; init; } = 2;
    public double ArrowHeadSize { get; init; } = 7;
    public double ArrowLength { get; init; } = 14;

    /// <summary>Miejsce zarezerwowane po lewej stronie profilu na strzałkę otwarcia (0, gdy ukryta).</summary>
    public double OpenArrowSpace => ShowOpenArrow ? ArrowLength + 4 : 0;

    /// <summary>Miejsce zarezerwowane po prawej stronie liter na strzałkę zamknięcia (0, gdy ukryta).</summary>
    public double CloseArrowSpace => ShowCloseArrow ? ArrowLength + 4 : 0;

    // --- Zegar bieżącego okresu TPO ---
    public bool TimerEnabled { get; init; } = true;
    public TimerPosition TimerPosition { get; init; } = TimerPosition.TopRight;
    public double TimerFontSize { get; init; } = 16;
    public FontWeight TimerFontWeight { get; init; } = FontWeights.Bold;
    public Brush TimerBrush { get; init; } = Brushes.LightGreen;

    // --- Wskaźnik tempa budowania TPO ---
    public bool TempoGaugeEnabled { get; init; } = true;
    public TempoGaugePosition TempoGaugePosition { get; init; } = TempoGaugePosition.UnderTimer;

    /// <summary>Pióro kreski połowy zakończonego okresu w trybie Split (null = kreska wyłączona).</summary>
    public Pen? SplitMidlinePen { get; init; }

    /// <summary>Pióro linii HalfBack – połowy wysokości profilu (null = linia wyłączona).</summary>
    public Pen? HalfBackPen { get; init; }

    /// <summary>Tło profilu wybranego w tabeli „Parametry profili”.</summary>
    public Brush SelectedProfileBrush { get; init; } = Brushes.Transparent;

    /// <summary>Wzór kreskowania dla stylu linii (wspólny dla linii cenowych i HalfBack).</summary>
    public static DashStyle ToDashStyle(LineStyle style) => style switch
    {
        LineStyle.Dashed => new DashStyle([4, 3], 0),
        LineStyle.Dotted => new DashStyle([1, 2], 0),
        _ => DashStyles.Solid,
    };

    public required Brush VolumeBrush { get; init; }
    public required Brush VolumePocBrush { get; init; }

    /// <summary>
    /// Obrys słupków wolumenu – w kolorze słupków, ale wyraźniejszy (min. 70% krycia), żeby profil wolumenowy
    /// był widoczny także przy niskim kryciu wypełnienia.
    /// </summary>
    public Pen? VolumeOutlinePen { get; init; }

    // --- Profil wolumenowy bieżącego słupka TPO ---
    public double PeriodVolumeGapFromTpo { get; init; } = 6;
    public double PeriodVolumeGapToSession { get; init; } = 10;
    public double PeriodVolumeWidth { get; init; } = 40;
    public Brush PeriodVolumeBrush { get; init; } = Brushes.DeepSkyBlue;
    public Pen? PeriodVolumeOutlinePen { get; init; }

    // --- Liczby wolumenu przy słupkach ---
    public bool VolumeLabelsOnSession { get; init; }
    public bool VolumeLabelsOnPeriod { get; init; }
    public VolumeLabelPosition VolumeLabelPosition { get; init; } = VolumeLabelPosition.OnBar;
    public Brush VolumeLabelBrush { get; init; } = Brushes.White;
    public double VolumeLabelFontSize { get; init; } = 9;
    public FontWeight VolumeLabelFontWeight { get; init; } = FontWeights.Normal;

    /// <summary>Zamiana ustawienia grubości czcionki na wartość WPF.</summary>
    public static FontWeight ToFontWeight(FontWeightKind kind) => kind switch
    {
        FontWeightKind.Bold => FontWeights.Bold,
        FontWeightKind.SemiBold => FontWeights.SemiBold,
        _ => FontWeights.Normal,
    };

    /// <summary>Kolory sesji według nazwy sesji ("Dzienna", "Nocna").</summary>
    public required IReadOnlyDictionary<string, SessionStyle> Sessions { get; init; }

    /// <summary>Kolory używane dla sesji bez własnego schematu.</summary>
    public required SessionStyle DefaultSession { get; init; }

    /// <summary>Kolory sesji o podanej nazwie.</summary>
    public SessionStyle ForSession(string sessionName) =>
        Sessions.TryGetValue(sessionName, out var style) ? style : DefaultSession;

    /// <summary>Cienkie pióro obrysu w podanym kolorze i kryciu.</summary>
    private static Pen CreateOutline(string color, int opacityPercent)
    {
        var pen = new Pen(ColorUtil.ToBrush(color, opacityPercent), 1);
        pen.Freeze();
        return pen;
    }

    /// <summary>Tworzy styl wykresu z ustawień aplikacji.</summary>
    public static ChartStyle Create(AppSettings settings)
    {
        var day = SessionStyle.Create(settings.DayColors);
        var volume = settings.VolumeStyle;

        Pen? midlinePen = null;
        if (settings.SplitMidline.Enabled)
        {
            midlinePen = new Pen(
                ColorUtil.ToBrush(settings.SplitMidline.Color, settings.SplitMidline.Opacity),
                Math.Max(1, settings.SplitMidline.Thickness));
            midlinePen.Freeze();
        }

        Pen? halfBackPen = null;
        if (settings.HalfBack.Enabled)
        {
            halfBackPen = new Pen(
                ColorUtil.ToBrush(settings.HalfBack.Color, settings.HalfBack.Opacity),
                Math.Clamp(settings.HalfBack.Thickness, 1, 10))
            {
                DashStyle = ToDashStyle(settings.HalfBack.Style),
            };
            halfBackPen.Freeze();
        }

        var timer = settings.PeriodTimer;

        return new ChartStyle
        {
            ShowInitialBalance = settings.ShowInitialBalance,
            ShowPoc = settings.ShowPoc,
            ShowOpenArrow = settings.Arrows.ShowOpen,
            ShowCloseArrow = settings.Arrows.ShowClose,
            OpenArrowBrush = ColorUtil.ToBrush(settings.Arrows.OpenColor),
            CloseArrowBrush = ColorUtil.ToBrush(settings.Arrows.CloseColor),
            ArrowThickness = Math.Clamp(settings.Arrows.Thickness, 1, 10),
            ArrowHeadSize = Math.Clamp(settings.Arrows.HeadSize, 2, 30),
            ArrowLength = Math.Clamp(settings.Arrows.Length, 4, 80),
            TimerEnabled = timer.Enabled,
            TimerPosition = timer.Position,
            TimerFontSize = Math.Clamp(timer.FontSize, 6, 72),
            TimerFontWeight = timer.FontWeight switch
            {
                FontWeightKind.Bold => FontWeights.Bold,
                FontWeightKind.SemiBold => FontWeights.SemiBold,
                _ => FontWeights.Normal,
            },
            TimerBrush = ColorUtil.ToBrush(timer.Color),
            TempoGaugeEnabled = settings.TempoGauge,
            TempoGaugePosition = settings.TempoGaugePosition,
            SplitMidlinePen = midlinePen,
            HalfBackPen = halfBackPen,
            SelectedProfileBrush = ColorUtil.ToBrush(settings.SelectedProfileHighlight.Color,
                Math.Clamp(settings.SelectedProfileHighlight.Opacity, 5, 100)),
            VolumeGap = volume.Gap,
            VolumeWidth = volume.Width,
            VolumeMirrored = volume.Mirrored,
            VolumeScale = volume.Scale,
            VolumeCapPercent = volume.CapPercent,
            VolumeBrush = ColorUtil.ToBrush(volume.BarColor, volume.Opacity),
            VolumeOutlinePen = CreateOutline(volume.BarColor, Math.Max(volume.Opacity, 70)),
            VolumePocBrush = ColorUtil.ToBrush(volume.VpocColor, Math.Max(volume.Opacity, 80)),
            VolumeLabelsOnSession = settings.VolumeLabels.ShowOnSessionVolume,
            VolumeLabelsOnPeriod = settings.VolumeLabels.ShowOnPeriodVolume,
            VolumeLabelPosition = settings.VolumeLabels.Position,
            VolumeLabelBrush = ColorUtil.ToBrush(settings.VolumeLabels.Color, settings.VolumeLabels.Opacity),
            VolumeLabelFontSize = Math.Clamp(settings.VolumeLabels.FontSize, 5, 40),
            VolumeLabelFontWeight = ToFontWeight(settings.VolumeLabels.FontWeight),
            PeriodVolumeGapFromTpo = Math.Clamp(settings.PeriodVolume.GapFromTpo, 0, 200),
            PeriodVolumeGapToSession = Math.Clamp(settings.PeriodVolume.GapToSessionVolume, 0, 200),
            PeriodVolumeWidth = Math.Clamp(settings.PeriodVolume.Width, 5, 400),
            PeriodVolumeBrush = ColorUtil.ToBrush(settings.PeriodVolume.BarColor, settings.PeriodVolume.Opacity),
            PeriodVolumeOutlinePen = CreateOutline(settings.PeriodVolume.BarColor, Math.Max(settings.PeriodVolume.Opacity, 70)),
            Sessions = new Dictionary<string, SessionStyle>
            {
                ["Dzienna"] = day,
                ["Nocna"] = SessionStyle.Create(settings.NightColors),
            },
            DefaultSession = day,
        };
    }

    /// <summary>Styl domyślny (przed wczytaniem ustawień).</summary>
    public static ChartStyle Default { get; } = Create(new AppSettings());
}
