using System.Globalization;
using MarketProfile.App.Controls;
using MarketProfile.App.Settings;
using MarketProfile.Core;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Zakładka „Szeroki rynek”: wykres świec 30 min rynku szerokiego (domyślnie indeks SPX) ze średnimi EMA/SMA
/// z ustawień; nad wykresem wskaźnik „Oddech rynku” (SPY), pod nim dwa wiersze na przyszłe wskaźniki.
///
/// Świece są tylko z sesji kasowej: rynek szeroki to zawsze indeks (wybór w ustawieniach ograniczony do indeksów),
/// a świece indeksów są pobierane z IB z <c>useRTH = 1</c>. Dane pobiera istniejący mechanizm – pobieranie
/// w tle i odświeżanie świec z IB co kilka minut (<see cref="RefreshCandlesFromIbAsync"/>); bieżąca świeca – na żywo.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Indeks zakładki „Szeroki rynek” (kolejność jak w MainWindow.xaml).</summary>
    public const int BroadTab = 3;

    private IReadOnlyList<Bar> _broadBars = [];
    private string _broadInfo = string.Empty;
    private string _broadEmptyText = string.Empty;
    private CandleChartAppearance? _broadAppearance;
    private int _broadLoadVersion;

    /// <summary>Świece 30 min rynku szerokiego (z bazy, sesja kasowa) z bieżącą świecą na żywo.</summary>
    public IReadOnlyList<Bar> BroadBars
    {
        get => _broadBars;
        private set => SetProperty(ref _broadBars, value);
    }

    /// <summary>Wygląd wykresu z ustawień „Szeroki rynek” (kolory świec, średnie EMA/SMA).</summary>
    public CandleChartAppearance BroadAppearance => _broadAppearance ??= CandleChartAppearance.Create(_settings.BroadMarketChart);

    /// <summary>Wolne miejsce za najnowszą świecą (%) – przeciąganie znacznika na wykresie, zapisywane w ustawieniach.</summary>
    public double BroadRightMargin
    {
        get => _settings.BroadMarketChart.RightMarginPercent;
        set
        {
            var percent = (int)Math.Round(Math.Clamp(value, 0, 50));
            if (percent == _settings.BroadMarketChart.RightMarginPercent)
            {
                return;
            }

            _settings = _settings with { BroadMarketChart = _settings.BroadMarketChart with { RightMarginPercent = percent } };
            SettingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>Wielkość ticka rynku szerokiego (format cen).</summary>
    public decimal BroadTickSize => BroadMarket?.TickSize ?? 0.01m;

    /// <summary>Opis na pasku zakładki: instrument, liczba świec, zakres dat, stan odświeżania z IB.</summary>
    public string BroadInfo
    {
        get => _broadInfo;
        private set => SetProperty(ref _broadInfo, value);
    }

    /// <summary>Tekst na pustym wykresie.</summary>
    public string BroadEmptyText
    {
        get => _broadEmptyText;
        private set => SetProperty(ref _broadEmptyText, value);
    }

    /// <summary>Tytuł wykresu, np. „SPX – 30 min (sesja kasowa)”.</summary>
    public string BroadTitle => BroadMarket is { } broad ? $"{broad.Symbol} – 30 min (sesja kasowa)" : "Szeroki rynek";

    /// <summary>Strefa czasowa godzin świec (giełda rynku szerokiego, np. Nowy Jork).</summary>
    public TimeZoneInfo BroadTimeZone => BroadMarket?.TimeZone ?? TimeZoneInfo.Utc;

    /// <summary>Zakładka pokazana (wybrana albo w osobnym oknie) – wczytanie świec.</summary>
    private void OnBroadTabShown() => RunSafely(LoadBroadBarsAsync());

    /// <summary>Zmieniono ustawienia – wygląd budowany od nowa, a przy zmianie rynku szerokiego – nowe świece.</summary>
    private void OnBroadSettingsChanged()
    {
        _broadAppearance = null;
        OnPropertyChanged(nameof(BroadAppearance));
        OnPropertyChanged(nameof(BroadTickSize));
        OnPropertyChanged(nameof(BroadTitle));
        OnPropertyChanged(nameof(BroadTimeZone));
        OnPropertyChanged(nameof(ShowBreathGauge));

        // Inny rynek szeroki przy działającym połączeniu na żywo – połączenia dla nowego indeksu.
        if (_broadFeeds is not null && _broadLiveSymbol != BroadMarket?.Symbol)
        {
            RunSafely(StartBroadLiveAsync(), "Szeroki rynek – połączenie na żywo");
        }

        if (IsTabShown(BroadTab))
        {
            RunSafely(LoadBroadBarsAsync());
        }
    }

    /// <summary>W bazie mogły pojawić się nowe świece rynku szerokiego (pobieranie w tle albo odświeżenie z IB).</summary>
    private void OnBroadBarsUpdated(string? symbol = null)
    {
        if (IsTabShown(BroadTab) && (symbol is null || symbol == BroadMarket?.Symbol))
        {
            RunSafely(LoadBroadBarsAsync());
        }
    }

    /// <summary>Wczytuje świece 30 min rynku szerokiego z bazy (w tle) – tylko z sesji kasowej.</summary>
    private async Task LoadBroadBarsAsync()
    {
        var version = ++_broadLoadVersion;
        if (BroadMarket is not { } broad)
        {
            _broadStored = [];
            BroadBars = [];
            BroadInfo = "Nieznany instrument rynku szerokiego – wybierz go w Ustawieniach → Szeroki rynek";
            BroadEmptyText = BroadInfo;
            return;
        }

        var store = GetStore();
        var bars = await Task.Run(() => (IReadOnlyList<Bar>)store.ReadOhlcBars(broad.Symbol, BarInterval.Minutes30)
            .Where(b => CashSession.Contains(broad, b.Time))
            .ToList());
        if (version != _broadLoadVersion)
        {
            return; // w międzyczasie zmieniono rynek szeroki
        }

        _broadStored = bars;
        _broadLiveVersion = -1; // świece na żywo – scalone od nowa z nowymi świecami z bazy
        UpdateBroadLiveBars();
        UpdateBroadInfo(broad);
        UpdateNyseBreath();

        if (_broadFeeds is null)
        {
            await LoadBreathFromStoreAsync(); // bez połączenia na żywo – ostatnia sesja z zapisanych ticków SPY
        }
    }

    private void UpdateBroadInfo(Instrument broad)
    {
        var bars = _broadBars;
        var text = bars.Count == 0
            ? $"{broad.Symbol} – {broad.Description} – świece 30 min (sesja kasowa): brak świec w bazie"
            : $"{broad.Symbol} – {broad.Description} – świece 30 min (sesja kasowa): " +
              $"{bars.Count.ToString("N0", CultureInfo.CurrentCulture)} świec, " +
              $"{TimeZoneInfo.ConvertTime(bars[0].Time, broad.TimeZone):dd.MM.yyyy} – {TimeZoneInfo.ConvertTime(bars[^1].Time, broad.TimeZone):dd.MM.yyyy}";

        if (!string.IsNullOrEmpty(_broadDataStatus))
        {
            text += $"   ·   {_broadDataStatus}";
        }

        if (_candleIbNotes.TryGetValue(broad.Symbol, out var note))
        {
            text += $"   ·   {note}";
        }

        BroadInfo = text;
        BroadEmptyText = note is not null && note.StartsWith("pobieranie", StringComparison.Ordinal)
            ? "Pobieranie świec z IBKR..."
            : $"Brak świec 30 min {broad.Symbol} w bazie – zostaną pobrane z IBKR po połączeniu z TWS (tryb „Na żywo”)";
    }

    // =====================================================================
    // Na żywo: bieżąca świeca 30 min rynku szerokiego i „Oddech rynku” (SPY)
    // =====================================================================

    private IReadOnlyList<Bar> _broadStored = [];
    private LiveIntervalBarBuilder? _broadLive;
    private long _broadLiveVersion = -1;
    private bool _broadIndexLive;
    private DateTimeOffset _broadLiveStartedAt;
    private string _broadDataStatus = string.Empty;
    private bool _broadDataLive;

    /// <summary>
    /// Stan notowań rynku szerokiego na żywo (obok tytułu wykresu): „na żywo”, „dane opóźnione (ok. 15 min)”,
    /// „brak notowań z IB…”, „sesja zamknięta”; pusty – bez połączenia na żywo.
    /// </summary>
    public string BroadDataStatus
    {
        get => _broadDataStatus;
        private set => SetProperty(ref _broadDataStatus, value);
    }

    /// <summary>Notowania płyną na żywo (kolor stanu: zielony; w pozostałych przypadkach – ostrzegawczy).</summary>
    public bool BroadDataLive
    {
        get => _broadDataLive;
        private set => SetProperty(ref _broadDataLive, value);
    }
    private BroadMarketLiveFeeds? _broadFeeds;
    private MarketBreathCalculator? _breath;
    private string? _broadMainSymbol;   // instrument główny, gdy jest nim rynek szeroki albo SPY (ticki z głównego źródła)
    private decimal? _breathValue;
    private string _breathDetail = string.Empty;
    private decimal? _nyseBreathValue;
    private string _nyseBreathDetail = string.Empty;
    private (decimal Up, decimal Down)? _nyseVolume;   // ostatni odczyt VOL-NYSE (z wątku źródła danych)
    private readonly Lock _nyseVolumeLock = new();
    private DateTime _nextBroadUpdate;
    private int _broadLiveGeneration;
    private string? _broadLiveSymbol;   // rynek szeroki połączeń na żywo

    /// <summary>Bieżąca wartość „Oddechu rynku” (null – brak danych).</summary>
    public decimal? BreathValue
    {
        get => _breathValue;
        private set
        {
            if (SetProperty(ref _breathValue, value))
            {
                OnPropertyChanged(nameof(BreathText));
            }
        }
    }

    /// <summary>Wartość jako tekst, np. „+1,25” / „−1,25”; „–” bez danych.</summary>
    public string BreathText => _breathValue is { } v
        ? (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("0.00", CultureInfo.CurrentCulture)
        : "–";

    /// <summary>Opis: instrument, wolumen kupna i sprzedaży, sesja.</summary>
    public string BreathDetail
    {
        get => _breathDetail;
        private set => SetProperty(ref _breathDetail, value);
    }

    /// <summary>„Oddech rynku · NYSE” z VOL-NYSE: wolumen spółek rosnących / spadających (null – brak danych).</summary>
    public decimal? NyseBreathValue
    {
        get => _nyseBreathValue;
        private set => SetProperty(ref _nyseBreathValue, value);
    }

    /// <summary>Opis: wolumen w górę / w dół całej NYSE.</summary>
    public string NyseBreathDetail
    {
        get => _nyseBreathDetail;
        private set => SetProperty(ref _nyseBreathDetail, value);
    }

    /// <summary>Czy pokazywać wskaźnik graficzny (pasek) – Ustawienia → Szeroki rynek.</summary>
    public bool ShowBreathGauge => _settings.BroadMarketChart.ShowBreathGauge;

    /// <summary>Instrument „Oddechu rynku” (SPY – z listy instrumentów albo wbudowany).</summary>
    private Instrument? BreathInstrument => Instrument.Find(_settings.Instruments, BroadMarketLiveFeeds.BreathSymbol);

    /// <summary>
    /// Start po połączeniu głównego źródła IB: wskaźnik z bazy od otwarcia sesji, potem dodatkowe połączenia na żywo
    /// (rynek szeroki i SPY – bez tych, które są instrumentem głównym; ich ticki przekazuje <see cref="RouteTickToBroadMarket"/>).
    /// </summary>
    private async Task StartBroadLiveAsync()
    {
        await StopBroadLiveAsync();
        var generation = _broadLiveGeneration;
        if (BroadMarket is not { } broad || BreathInstrument is not { } spy)
        {
            return;
        }

        var main = _settings.InstrumentSymbol;
        _broadMainSymbol = main == broad.Symbol || main == spy.Symbol ? main : null;
        _broadLive = new LiveIntervalBarBuilder(broad, BarInterval.Minutes30);
        _broadLiveSymbol = broad.Symbol;
        _broadIndexLive = true;
        _broadLiveStartedAt = DateTimeOffset.UtcNow;
        var breath = _breath = new MarketBreathCalculator(spy);

        // Wolumen od 9:30 z bazy (ticki SPY zapisane wcześniej, ze stroną transakcji) – restart nie zeruje wskaźnika.
        var store = GetStore();
        var now = DateTimeOffset.UtcNow;
        var session = CashSession.Latest(spy, now);
        await Task.Run(() => breath.Load(store.ReadTicks(spy.Symbol, session.Start, now, MarketDataStore.SourceBroker), now));
        if (generation != _broadLiveGeneration)
        {
            return; // w międzyczasie zatrzymano źródło danych
        }

        var feeds = _broadFeeds = new BroadMarketLiveFeeds();
        var live = _broadLive;
        feeds.IndexTick += live.Add;
        feeds.BreathTick += breath.Add;
        feeds.NyseVolume += (up, down) =>
        {
            lock (_nyseVolumeLock)
            {
                _nyseVolume = (up, down);
            }
        };
        feeds.Problem += message => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(feeds, _broadFeeds))
            {
                ShowError(message);
            }
        });

        var connection = _settings.ToIbSettings();
        var recordTo = _settings.RecordToDatabase ? store : null;
        await Task.Run(() => feeds.StartAsync(connection,
            main == broad.Symbol ? null : broad,
            main == spy.Symbol ? null : spy,
            recordTo, CancellationToken.None));
        if (generation != _broadLiveGeneration)
        {
            await feeds.DisposeAsync(); // zatrzymano w trakcie łączenia – połączenia już niepotrzebne
            return;
        }

        UpdateBreath();
    }

    /// <summary>Zatrzymanie dodatkowych połączeń (wartości zostają na ekranie).</summary>
    private async Task StopBroadLiveAsync()
    {
        _broadLiveGeneration++;
        _broadMainSymbol = null;
        _broadIndexLive = false;
        BroadDataStatus = string.Empty;
        BroadDataLive = false;
        if (_broadFeeds is { } feeds)
        {
            _broadFeeds = null;
            await feeds.DisposeAsync();
        }
    }

    /// <summary>Tick głównego źródła – gdy instrumentem głównym jest rynek szeroki albo SPY (wątek źródła danych).</summary>
    private void RouteTickToBroadMarket(Tick tick)
    {
        var symbol = _broadMainSymbol;
        if (symbol is null)
        {
            return;
        }

        if (symbol == BroadMarket?.Symbol)
        {
            _broadLive?.Add(tick);
        }
        else if (symbol == BroadMarketLiveFeeds.BreathSymbol)
        {
            _breath?.Add(tick);
        }
    }

    /// <summary>Z zegara odświeżania (co sekundę): bieżąca świeca 30 min i wartość wskaźnika.</summary>
    private void OnBroadTimer()
    {
        if (!IsTabShown(BroadTab) || DateTime.UtcNow < _nextBroadUpdate)
        {
            return;
        }

        _nextBroadUpdate = DateTime.UtcNow.AddSeconds(1);

        UpdateBroadLiveBars();
        UpdateBreath();
        UpdateNyseBreath();
        UpdateBroadDataStatus();
    }

    /// <summary>Stan notowań indeksu: czy przychodzą, na żywo czy z opóźnieniem.</summary>
    private void UpdateBroadDataStatus()
    {
        var now = DateTimeOffset.UtcNow;
        var live = false;
        string status;
        if (_broadFeeds is not { } feeds || BroadMarket is not { } broad || !_broadIndexLive)
        {
            status = string.Empty;
        }
        else if (_broadMainSymbol == broad.Symbol)
        {
            status = "notowania z głównego połączenia";
            live = true;
        }
        else if (!feeds.IndexFeedRunning)
        {
            status = $"brak połączenia z notowaniami {broad.Symbol}";
        }
        else if (feeds.IndexDelayed == true)
        {
            status = $"{broad.Symbol}: dane opóźnione (ok. 15 min)";
        }
        else if (!CashSession.Contains(broad, now))
        {
            status = "sesja zamknięta";
        }
        else if (feeds.LastIndexTickAt is { } at && now - at < TimeSpan.FromMinutes(1))
        {
            status = $"{broad.Symbol}: na żywo";
            live = true;
        }
        else if (now - _broadLiveStartedAt < TimeSpan.FromSeconds(30))
        {
            status = $"{broad.Symbol}: oczekiwanie na notowania...";
        }
        else
        {
            status = feeds.LastIndexTickAt is { } last
                ? $"{broad.Symbol}: brak nowych notowań od {TimeZoneInfo.ConvertTime(last, broad.TimeZone):HH:mm}"
                : $"{broad.Symbol}: brak notowań z IB (ani na żywo, ani opóźnionych) – bieżąca świeca odświeżana z IB co kilka minut";
        }

        BroadDataLive = live;
        if (status != _broadDataStatus)
        {
            BroadDataStatus = status;
            if (BroadMarket is { } market)
            {
                UpdateBroadInfo(market);
            }
        }
    }

    private void UpdateNyseBreath()
    {
        (decimal Up, decimal Down)? volume;
        lock (_nyseVolumeLock)
        {
            volume = _nyseVolume;
        }

        if (volume is not { } v)
        {
            NyseBreathValue = null;
            NyseBreathDetail = _broadFeeds is null
                ? "NYSE – brak danych (połącz się z IB w trybie „Na żywo”)"
                : "NYSE – czekam na dane VOL-NYSE z IB";
            return;
        }

        NyseBreathValue = MarketBreathCalculator.SignedRatio((long)v.Up, (long)v.Down);
        NyseBreathDetail = $"NYSE · w górę {FormatVolume((long)v.Up)} / w dół {FormatVolume((long)v.Down)}" +
                           (_broadFeeds is null ? string.Empty : " · na żywo");
    }

    private void UpdateBroadLiveBars()
    {
        var live = _broadLive;
        var version = live?.Version ?? 0;
        if (version == _broadLiveVersion && _broadLiveVersion >= 0)
        {
            return;
        }

        _broadLiveVersion = version;
        BroadBars = live is null ? _broadStored : live.Merge(_broadStored);
    }

    /// <summary>Bez połączenia na żywo: wskaźnik z zapisanych ticków SPY ostatniej sesji.</summary>
    private async Task LoadBreathFromStoreAsync()
    {
        if (BreathInstrument is not { } spy)
        {
            return;
        }

        var breath = new MarketBreathCalculator(spy);
        var store = GetStore();
        var now = DateTimeOffset.UtcNow;
        var session = CashSession.Latest(spy, now);
        await Task.Run(() => breath.Load(store.ReadTicks(spy.Symbol, session.Start, session.End, MarketDataStore.SourceBroker), now));
        if (_broadFeeds is null)
        {
            _breath = breath;
            UpdateBreath();
        }
    }

    private void UpdateBreath()
    {
        if (_breath is not { } breath)
        {
            BreathValue = null;
            BreathDetail = "SPY – brak danych (połącz się z IB w trybie „Na żywo”)";
            return;
        }

        BreathValue = breath.Value;
        var buy = breath.BuyVolume;
        var sell = breath.SellVolume;
        var session = breath.SessionDate is { } date ? $"sesja {date:dd.MM}" : "brak transakcji w sesji";
        BreathDetail = $"{breath.Instrument.Symbol} · kupno {FormatVolume(buy)} / sprzedaż {FormatVolume(sell)} · {session}" +
                       (_broadFeeds is null ? string.Empty : " · na żywo");
    }

    private static string FormatVolume(long volume) => volume switch
    {
        >= 1_000_000_000 => (volume / 1_000_000_000m).ToString("0.0", CultureInfo.CurrentCulture) + " mld",
        >= 1_000_000 => (volume / 1_000_000m).ToString("0.0", CultureInfo.CurrentCulture) + " mln",
        _ => volume.ToString("N0", CultureInfo.CurrentCulture),
    };
}
