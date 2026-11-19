using System.Windows.Media;
using MarketProfile.App.Controls;
using MarketProfile.App.Settings;
using MarketProfile.Core;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Jeden wiersz karty „Otwarcie”: etykieta i wartość (zawsze od wielkiej litery), opcjonalny akcent (np. strzałka kierunku
/// korekty inwentarza) w kolorze, przygaszenie (wartość bez znaczenia) i mini-wykres położenia otwarcia.
/// </summary>
public sealed record OpeningCardLine(
    string Label,
    string Value,
    string Accent = "",
    Brush? AccentBrush = null,
    bool IsDim = false,
    OpeningGaugeData? Gauge = null)
{
    public bool HasAccent => Accent.Length > 0;
    public bool HasGauge => Gauge is not null;
}

/// <summary>
/// Karta „Otwarcie” – gdy zaczyna się nowy profil (w symulacji po skoku ⏭ / ⏮ albo w trakcie odtwarzania, na żywo przy
/// otwarciu sesji): gdzie profil się otwiera względem poprzedniego dnia (z mini-wykresem), GAP, inwentarz (istotny tylko
/// przy dużym odstępie od SETTLE – wtedy strzałka kierunku spodziewanej korekty), otwarcie względem nocy, HB, typ otwarcia,
/// najbliższe nietknięte POC. Treść odświeża się na bieżąco (typ otwarcia po literze B, wyrównanie inwentarza)
/// aż do zamknięcia karty albo następnego profilu.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly Brush UpBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x64)));   // zielona ↑
    private static readonly Brush DownBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x52))); // czerwona ↓

    private (string Symbol, string Session, DateTime Start)? _openingSeen; // najnowszy profil przy ostatnim przeliczeniu
    private bool _openingIsNew;      // najnowszy profil zaczął się na naszych oczach (nie przy wczytaniu danych)
    private bool _openingClosed;     // użytkownik zamknął kartę tego profilu
    private bool _openingCardVisible;
    private string _openingCardTitle = string.Empty;
    private IReadOnlyList<OpeningCardLine> _openingCardLines = [];

    /// <summary>Czy karta „Otwarcie” jest widoczna.</summary>
    public bool OpeningCardVisible
    {
        get => _openingCardVisible;
        private set => SetProperty(ref _openingCardVisible, value);
    }

    /// <summary>Tytuł karty, np. „Dzienna 18.11 · 15:30 · litera A”.</summary>
    public string OpeningCardTitle
    {
        get => _openingCardTitle;
        private set => SetProperty(ref _openingCardTitle, value);
    }

    /// <summary>Wiersze karty.</summary>
    public IReadOnlyList<OpeningCardLine> OpeningCardLines
    {
        get => _openingCardLines;
        private set => SetProperty(ref _openingCardLines, value);
    }

    /// <summary>Menu Wyświetlanie: karta „Otwarcie” przy nowym profilu – zapamiętywane.</summary>
    public bool ShowOpeningCard
    {
        get => _settings.ShowOpeningCard;
        set
        {
            if (value == _settings.ShowOpeningCard)
            {
                return;
            }

            _settings = _settings with { ShowOpeningCard = value };
            SettingsStore.Save(_settings);
            OnPropertyChanged();
            UpdateOpeningCard(_profileStats);
        }
    }

    /// <summary>✕ – zamyka kartę do następnego profilu.</summary>
    public void CloseOpeningCard()
    {
        _openingClosed = true;
        OpeningCardVisible = false;
    }

    /// <summary>Zapisuje położenie karty (po przesunięciu myszą).</summary>
    public void SaveOpeningPanelPosition(double left, double top)
    {
        _settings = _settings with { OpeningPanelLeft = left, OpeningPanelTop = top };
        SettingsStore.Save(_settings);
    }

    /// <summary>
    /// Po przeliczeniu parametrów profili: nowy najnowszy profil → pokaż kartę; ten sam → odśwież treść.
    /// Pierwsze wczytanie danych (start aplikacji, zmiana instrumentu) tylko zapamiętuje profil – bez karty.
    /// </summary>
    private void UpdateOpeningCard(IReadOnlyList<ProfileStats> stats)
    {
        if (stats.Count == 0)
        {
            OpeningCardVisible = false;
            return;
        }

        var current = stats[^1];
        var key = (_settings.InstrumentSymbol, current.Session, current.SessionStart);
        if (_openingSeen is not { } seen || seen.Symbol != key.InstrumentSymbol)
        {
            _openingSeen = key;
            _openingIsNew = false;
        }
        else if (seen != key)
        {
            _openingSeen = key;
            _openingIsNew = true;
            _openingClosed = false;
        }

        if (!_openingIsNew || _openingClosed || !ShowOpeningCard)
        {
            OpeningCardVisible = false;
            return;
        }

        var tick = _settings.Instrument.TickSize;
        var previousDay = stats.LastOrDefault(s => s.IsDaySession && s.SessionStart < current.SessionStart);
        OpeningCardTitle = $"{current.Session} {current.TradingDate:dd.MM} · {current.SessionStart:HH:mm}" +
                           (current.LastLetter is { } letter ? $" · litera {letter}" : string.Empty);
        var lines = current.IsDaySession
            ? DayOpeningLines(current, previousDay is null ? null : Row(previousDay, tick).Stats, tick)
            : NightOpeningLines(current, previousDay is null ? null : Row(previousDay, tick).Stats, tick);
        OpeningCardLines = [.. lines.Select(l => l with { Value = Capitalize(l.Value) })];
        OpeningCardVisible = true;
    }

    private ProfileStatsRow Row(ProfileStats stats, decimal tick) =>
        new(stats, tick, stats.IsDaySession ? GetSettlement(stats.TradingDate) : null);

    /// <summary>Sesja dzienna – relacje policzone przez analizator profili.</summary>
    /// <param name="p">Poprzedni dzień (SETTLE już z wpisem ręcznym / Close).</param>
    private List<OpeningCardLine> DayOpeningLines(ProfileStats s, ProfileStats? p, decimal tick)
    {
        string P(decimal? v) => v is { } x ? PriceFormat.Format(x, tick) : "–";
        var row = Row(s, tick);
        var lines = new List<OpeningCardLine> { new("Open", P(s.Open)) };

        if (p is not null)
        {
            lines.Add(LocationLine("Otwarcie vs VA poprz.", s.OpenVsPreviousValue, s, p, tick));
            lines.Add(s.Gap is null
                ? new("GAP", "brak – otwarcie w zakresie poprz. dnia", IsDim: true)
                : new("GAP", row.Gap));
            lines.Add(InventoryLine("Inwentarz", s.Inventory, s.InventorySize, s.PreviousSettle, p, s.InventoryCorrected,
                s.InventoryCorrectedLetter, tick));
            lines.Add(new("Otwarcie vs HB poprz.", row.OpenVsPreviousHb));
        }
        else
        {
            lines.Add(new("Poprzedni dzień", "brak danych – relacje z poprzednim dniem niedostępne", IsDim: true));
        }

        lines.Add(s.OvernightHigh is null
            ? new("Otwarcie vs noc", "brak sesji nocnej przed dniem", IsDim: true)
            : new("Otwarcie vs noc", $"{OvernightText(s.OpenVsOvernight)}  (ONL {P(s.OvernightLow)} – ONH {P(s.OvernightHigh)}, ONPOC {P(s.OvernightPoc)})"));
        lines.Add(s.Periods < 2 ? new("Typ otwarcia", "po literze B", IsDim: true) : new("Typ otwarcia", row.OpenType));

        if (s.NakedPocs.Count > 0)
        {
            var above = s.NakedPocs.Where(x => x > s.Open).Select(x => (decimal?)x).Min();
            var below = s.NakedPocs.Where(x => x < s.Open).Select(x => (decimal?)x).Max();
            lines.Add(new("Nietknięte POC", $"↑ {P(above)} · ↓ {P(below)}  (najbliższe nad / pod otwarciem)"));
        }

        return lines;
    }

    /// <summary>
    /// Sesja nocna – analizator nie liczy dla niej relacji, więc liczymy je tu względem poprzedniego dnia
    /// (te same reguły: <see cref="ProfileAnalyzer.LocateOpen"/>, GAP od High / Low, inwentarz od SETTLE, HB).
    /// </summary>
    private List<OpeningCardLine> NightOpeningLines(ProfileStats s, ProfileStats? p, decimal tick)
    {
        string P(decimal? v) => v is { } x ? PriceFormat.Format(x, tick) : "–";
        var lines = new List<OpeningCardLine> { new("Open", P(s.Open)) };
        if (p is null)
        {
            lines.Add(new("Poprzedni dzień", "brak danych – relacje z poprzednim dniem niedostępne", IsDim: true));
            return lines;
        }

        var o = s.Open;
        var settle = p.Settle ?? p.Close;
        var relations = s with
        {
            Gap = o > p.High ? o - p.High : o < p.Low ? o - p.Low : null,
            PreviousHalfback = p.Halfback,
            OpenVsPreviousHb = Math.Sign(o - p.Halfback),
        };
        var row = Row(relations, tick);
        var location = ProfileAnalyzer.LocateOpen(o, p.High, p.Low, p.ValueAreaHigh, p.ValueAreaLow, p.RowHeight);
        var inventory = o > settle ? OpeningInventory.Long : o < settle ? OpeningInventory.Short : OpeningInventory.Neutral;

        lines.Add(LocationLine("Otwarcie vs VA dnia", location, s, p, tick));
        lines.Add(relations.Gap is null
            ? new("GAP", "brak – otwarcie w zakresie dnia", IsDim: true)
            : new("GAP", row.Gap));
        lines.Add(InventoryLine("Inwentarz vs SETTLE dnia", inventory, o - settle, settle, p, null, null, tick));
        lines.Add(new("Otwarcie vs HB dnia", row.OpenVsPreviousHb));
        return lines;
    }

    /// <summary>
    /// Położenie otwarcia względem VA i zakresu poprzedniego dnia: krótki tekst z odległością od najbliższego poziomu
    /// i mini-wykres (zakres, VA, POC, noc, otwarcie).
    /// </summary>
    private static OpeningCardLine LocationLine(string label, ValueLocation location, ProfileStats s, ProfileStats p, decimal tick)
    {
        string Signed(decimal d) => d >= 0 ? $"+{PriceFormat.Format(d, tick)}" : $"−{PriceFormat.Format(-d, tick)}";
        var o = s.Open;
        var text = location switch
        {
            ValueLocation.AboveRange => $"nad High – poza zakresem ({Signed(o - p.High)} od High)",
            ValueLocation.AboveValue => $"nad VA ({Signed(o - p.ValueAreaHigh)} od VAH)",
            ValueLocation.InValue => $"w VA ({Signed(o - p.Poc)} od POC)",
            ValueLocation.BelowValue => $"pod VA ({Signed(o - p.ValueAreaLow)} od VAL)",
            ValueLocation.BelowRange => $"pod Low – poza zakresem ({Signed(o - p.Low)} od Low)",
            _ => "–",
        };

        var gauge = new OpeningGaugeData(
            (double)p.Low, (double)p.High, (double)p.ValueAreaLow, (double)(p.ValueAreaHigh + p.RowHeight), (double)p.Poc,
            s.OvernightLow is { } onl ? (double)onl : double.NaN,
            s.OvernightHigh is { } onh ? (double)onh : double.NaN,
            (double)o);
        return new OpeningCardLine(label, text, Gauge: gauge);
    }

    /// <summary>
    /// Inwentarz: istotny, gdy |Open − SETTLE| ≥ <see cref="AppSettings.InventorySignificantPercent"/> % zakresu poprzedniego
    /// dnia – wtedy strzałka kierunku spodziewanej korekty do SETTLE (długi → ↓ czerwona, krótki → ↑ zielona), a po
    /// wyrównaniu ✓. Mniejszy odstęp – przygaszony, bez znaczenia.
    /// </summary>
    private OpeningCardLine InventoryLine(string label, OpeningInventory kind, decimal? size, decimal? settle, ProfileStats p,
        bool? corrected, char? correctedLetter, decimal tick)
    {
        string Signed(decimal d) => d >= 0 ? $"+{PriceFormat.Format(d, tick)}" : $"−{PriceFormat.Format(-d, tick)}";
        var settleText = settle is { } st ? $"SETTLE {PriceFormat.Format(st, tick)}" : "SETTLE –";
        if (size is not { } sz || kind is OpeningInventory.None or OpeningInventory.Neutral || sz == 0)
        {
            return new(label, $"brak – Open = {settleText}", IsDim: true);
        }

        var range = p.High - p.Low;
        var percent = range > 0 ? (double)(Math.Abs(sz) / range) * 100 : 0;
        var share = $"{percent:0}% zakresu";
        if (percent < _settings.InventorySignificantPercent)
        {
            return new(label, $"mały {Signed(sz)} ({share}) vs {settleText} – bez znaczenia", IsDim: true);
        }

        var name = kind == OpeningInventory.Long ? "długi" : "krótki";
        if (corrected == true)
        {
            return new(label, $"wyrównany ({correctedLetter}) – był {name} {Signed(sz)} ({share}) vs {settleText}", "✓", UpBrush);
        }

        return kind == OpeningInventory.Long
            ? new(label, $"długi {Signed(sz)} ({share}) vs {settleText} – spodziewana korekta w dół", "↓", DownBrush)
            : new(label, $"krótki {Signed(sz)} ({share}) vs {settleText} – spodziewana korekta w górę", "↑", UpBrush);
    }

    /// <summary>Otwarcie względem nocy – krótko (zakres nocy jest podany obok w nawiasie).</summary>
    private static string OvernightText(OvernightLocation location) => location switch
    {
        OvernightLocation.AboveOnh => "nad ONH",
        OvernightLocation.InOvernight => "w nocy",
        OvernightLocation.BelowOnl => "pod ONL",
        _ => "–",
    };

    /// <summary>Pierwsza litera wielka (gdy wartość zaczyna się od litery).</summary>
    private static string Capitalize(string text) =>
        text.Length > 0 && char.IsLower(text[0]) ? char.ToUpper(text[0], System.Globalization.CultureInfo.CurrentCulture) + text[1..] : text;

    private static T Frozen<T>(T freezable) where T : System.Windows.Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
