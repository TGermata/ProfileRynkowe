using System.Windows;
using System.Windows.Media;
using MarketProfile.App.Settings;
using MarketProfile.App.ViewModels;
using MarketProfile.Core;

namespace MarketProfile.App.Controls;

/// <summary>
/// Opisy analizy na profilach dziennych (włączane osobno w menu „Wyświetlanie”):
///  - znaczniki punktów charakterystycznych: HB (halfback), S (SETTLE – Close sesji albo wpis ręczny, przerywany),
///    PBL / PBH (PULLBACK LOW / HIGH dnia trendowego),
///  - ONH / ONL / ONPOC – High, Low i POC poprzedzającej sesji nocnej: na ostatnim dniu, na wszystkich dniach albo wcale,
///  - pod profilem jedna linia opisu: relacja wartości / dzień trendowy, otwarcie względem nocy, luka (GAP) względem High / Low dnia poprzedniego.
/// Open i Close mają osobne strzałki (menu „Wyświetlanie” → Profil).
/// </summary>
public sealed partial class ProfileChartControl
{
    public static readonly DependencyProperty ProfileNotesProperty = DependencyProperty.Register(
        nameof(ProfileNotes), typeof(IReadOnlyList<ProfileStats>), typeof(ProfileChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((ProfileChartControl)d)._notesByProfile = null));

    public static readonly DependencyProperty NotesStyleProperty = DependencyProperty.Register(
        nameof(NotesStyle), typeof(ChartNotesStyle), typeof(ProfileChartControl),
        new FrameworkPropertyMetadata(new ChartNotesStyle(), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush HalfbackBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xB8, 0xC0, 0xCC)));
    private static readonly Brush SettleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xB8, 0x32)));
    private static readonly Brush OvernightBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xB0, 0x8C, 0xF0)));
    private static readonly Brush PullbackBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x45)));
    private static readonly Brush NoteTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xD8, 0xDC, 0xE2)));
    private static readonly Brush NoteBackBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x14, 0x17, 0x1C)));
    private static readonly Pen HalfbackPen = Frozen(new Pen(HalfbackBrush, 1) { DashStyle = DashStyles.Dot });
    private static readonly Pen SettlePen = Frozen(new Pen(SettleBrush, 1.5));
    private static readonly Pen ManualSettlePen = Frozen(new Pen(SettleBrush, 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Pen OvernightPen = Frozen(new Pen(OvernightBrush, 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen PullbackPen = Frozen(new Pen(PullbackBrush, 1.5) { DashStyle = DashStyles.DashDot });

    private Dictionary<(string Session, DateTime Start), ProfileStats>? _notesByProfile;

    /// <summary>Parametry profili (analiza) – źródło opisów.</summary>
    public IReadOnlyList<ProfileStats>? ProfileNotes
    {
        get => (IReadOnlyList<ProfileStats>?)GetValue(ProfileNotesProperty);
        set => SetValue(ProfileNotesProperty, value);
    }

    /// <summary>Które opisy rysować.</summary>
    public ChartNotesStyle NotesStyle
    {
        get => (ChartNotesStyle)GetValue(NotesStyleProperty);
        set => SetValue(NotesStyleProperty, value);
    }

    /// <summary>Menu profilu dziennego: wpis ręczny SETTLE (data handlowa, podpowiedź = Close).</summary>
    public event Action<DateOnly, decimal>? SettleRequested;

    private ProfileStats? NotesFor(ProfileSnapshot profile)
    {
        if (ProfileNotes is not { } notes)
        {
            return null;
        }

        _notesByProfile ??= notes.GroupBy(s => (s.Session, s.SessionStart)).ToDictionary(g => g.Key, g => g.Last());
        return _notesByProfile.GetValueOrDefault((profile.Session.Name, profile.SessionStart));
    }

    /// <summary>Początek najnowszego profilu dziennego na wykresie (dla trybu „poziomy nocy tylko na ostatnim dniu”).</summary>
    private DateTime? LatestDayStart() =>
        Profiles?.Where(p => SessionNames.IsDay(p.Snapshot.Session.Name) && !p.Snapshot.IsEmpty)
            .Select(p => (DateTime?)p.Snapshot.SessionStart)
            .Max();

    /// <summary>Znaczniki i opis na kolumnie profilu dziennego (tylko ostatnia część rozbitego profilu).</summary>
    private void DrawProfileNotes(DrawingContext dc, ProfileSnapshot profile, double x, double width, DateTime? latestDayStart)
    {
        var style = NotesStyle;
        if (!SessionNames.IsDay(profile.Session.Name) || profile.PartNumber < profile.PartCount ||
            profile.IsEmpty || NotesFor(profile) is not { } stats)
        {
            return;
        }

        var left = x + OpenSpace;
        var right = x + width - Gap;
        if (right - left < 20)
        {
            return; // za wąska kolumna (duże oddalenie)
        }

        var labels = _compact > 0.45;
        void Marker(decimal? price, Pen pen, string label, Brush brush)
        {
            if (price is not { } p)
            {
                return;
            }

            var y = Math.Round(PriceToY(ToRow(p, profile.RowHeight))) + 0.5;
            dc.DrawLine(pen, new Point(left, y), new Point(right, y));
            if (labels)
            {
                var text = CreateText(label, 9, brush, UiTypeface);
                dc.DrawText(text, new Point(right - text.Width - 1, y - text.Height));
            }
        }

        var showOvernight = style.OvernightLevels == OvernightLevelsMode.AllDays ||
                            style.OvernightLevels == OvernightLevelsMode.LastDay && profile.SessionStart == latestDayStart;
        if (showOvernight)
        {
            Marker(stats.OvernightHigh, OvernightPen, "ONH", OvernightBrush);
            Marker(stats.OvernightLow, OvernightPen, "ONL", OvernightBrush);
            Marker(stats.OvernightPoc, OvernightPen, "ONPOC", OvernightBrush);
        }

        if (style.ShowHalfback)
        {
            Marker(stats.Halfback, HalfbackPen, "HB", HalfbackBrush);
        }

        if (style.ShowSettle)
        {
            var manual = stats.SettleSource == Settlement.SourceManual;
            Marker(stats.Settle, manual ? ManualSettlePen : SettlePen, manual ? "S ✎" : "S", SettleBrush);
        }

        if (style.ShowPullback)
        {
            Marker(stats.PullbackLow, PullbackPen, $"PBL {stats.PullbackLetter}", PullbackBrush);
            Marker(stats.PullbackHigh, PullbackPen, $"PBH {stats.PullbackLetter}", PullbackBrush);
        }

        if (style.ShowNoteText && labels)
        {
            DrawNoteText(dc, profile, stats, left, right);
        }
    }

    /// <summary>Opis pod profilem, np. „VA ↑ wyżej · Open &gt; ONH · GAP ↑ +6.25” albo „Trend ↑ B–K · …”.</summary>
    private void DrawNoteText(DrawingContext dc, ProfileSnapshot profile, ProfileStats stats, double left, double right)
    {
        var parts = new List<string>();
        if (stats.IsTrendDay)
        {
            parts.Add($"Trend {(stats.TrendDirection > 0 ? "↑" : "↓")} {stats.IntervalFrom}–{stats.IntervalTo}");
        }
        else if (ProfileStatsRow.ValueMigrationShort(stats.ValueMigration) is { Length: > 0 } va)
        {
            parts.Add(va);
        }

        switch (stats.OpenVsOvernight)
        {
            case OvernightLocation.AboveHigh:
                parts.Add("Open > ONH");
                break;
            case OvernightLocation.BelowLow:
                parts.Add("Open < ONL");
                break;
            case OvernightLocation.InRange:
                parts.Add("Open w nocy");
                break;
        }

        if (stats.InventorySize is { } inventory && stats.OpeningInventory is OpeningInventory.Long or OpeningInventory.Short)
        {
            var size = stats.OpeningInventory == OpeningInventory.Long ? $"↑ +{FormatPrice(inventory)}" : $"↓ −{FormatPrice(-inventory)}";
            var state = stats.InventoryCorrected == true ? $" ✓{stats.InventoryCorrectedLetter}" : stats.IsComplete ? " ✗" : " …";
            parts.Add($"Inw. {size}{state}");
        }

        if (stats.Gap is { } gap && gap != 0)
        {
            parts.Add(gap > 0 ? $"GAP ↑ +{FormatPrice(gap)}" : $"GAP ↓ −{FormatPrice(-gap)}");
        }

        if (parts.Count == 0)
        {
            return;
        }

        var note = CreateText(string.Join(" · ", parts), 10, NoteTextBrush, UiTypeface);
        note.MaxTextWidth = Math.Max(10, right - left - 4);
        note.MaxLineCount = 2;
        note.Trimming = TextTrimming.CharacterEllipsis;
        var top = PriceToY(ToRow(profile.Low, profile.RowHeight)) + _rowPx / 2 + 6;
        top = Math.Min(top, ActualHeight - note.Height - 4);
        dc.DrawRectangle(NoteBackBrush, null, new Rect(left, top - 1, note.Width + 4, note.Height + 2));
        dc.DrawText(note, new Point(left + 2, top));
    }
}
