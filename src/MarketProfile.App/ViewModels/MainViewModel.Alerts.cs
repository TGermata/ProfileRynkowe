using MarketProfile.App.Infrastructure;
using MarketProfile.App.Settings;
using MarketProfile.Core;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Poziomy alarmowe: linie cenowe z alarmem dźwiękowym. Ticki (wątek źródła danych) zapisują zakres cen, a zegar
/// odświeżania (co 100 ms, wątek UI) sprawdza przebicia, odtwarza dźwięk i pokazuje komunikat.
/// Historia i dogonienie punktu startu symulacji nie uruchamiają alarmów – liczą się tylko dane po starcie.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Jak długo miga poziom, który właśnie zadziałał.</summary>
    private static readonly TimeSpan AlertFlashDuration = TimeSpan.FromSeconds(5);

    /// <summary>Najmniejszy odstęp między dźwiękami (szybka symulacja może przebić kilka poziomów naraz).</summary>
    private static readonly TimeSpan AlertSoundGap = TimeSpan.FromMilliseconds(300);

    private readonly PriceAlertMonitor _alertMonitor = new();
    private readonly Lock _alertRangeLock = new();
    private decimal? _alertLow;
    private decimal? _alertHigh;
    private decimal _alertLast;
    private volatile bool _alertsLive;
    private DateTime _lastAlertSound = DateTime.MinValue;
    private readonly Dictionary<Guid, DateTime> _alertFlashUntil = [];
    private IReadOnlySet<Guid> _flashingAlerts = new HashSet<Guid>();

    /// <summary>Poziomy alarmowe, które właśnie zadziałały – migają na wykresie.</summary>
    public IReadOnlySet<Guid> FlashingAlerts
    {
        get => _flashingAlerts;
        private set => SetProperty(ref _flashingAlerts, value);
    }

    // --- Menu „Wyświetlanie” ---

    /// <summary>Alarmy dźwiękowe poziomów cenowych włączone.</summary>
    public bool PriceAlertsEnabled
    {
        get => _settings.PriceAlerts.Enabled;
        set => SavePriceAlertsSettings(_settings.PriceAlerts with { Enabled = value });
    }

    /// <summary>Alarmy działają także w symulacji.</summary>
    public bool PriceAlertsInSimulation
    {
        get => _settings.PriceAlerts.InSimulation;
        set => SavePriceAlertsSettings(_settings.PriceAlerts with { InSimulation = value });
    }

    private void SavePriceAlertsSettings(PriceAlertsSettings alerts)
    {
        if (alerts == _settings.PriceAlerts)
        {
            return;
        }

        _settings = _settings with { PriceAlerts = alerts };
        SettingsStore.Save(_settings);
        OnPropertyChanged(nameof(PriceAlertsEnabled));
        OnPropertyChanged(nameof(PriceAlertsInSimulation));
    }

    // --- Zakres cen od ostatniego sprawdzenia (wątek źródła danych) ---

    /// <summary>Cena z ticka albo zakres ze świecy – do wykrywania przebić.</summary>
    private void TrackAlertPrice(decimal low, decimal high, decimal last)
    {
        if (!_alertsLive)
        {
            return;
        }

        lock (_alertRangeLock)
        {
            _alertLow = _alertLow is { } l ? Math.Min(l, low) : low;
            _alertHigh = _alertHigh is { } h ? Math.Max(h, high) : high;
            _alertLast = last;
        }
    }

    /// <summary>Włącza / wyłącza wykrywanie (start po wczytaniu historii, stop, zmiana instrumentu); zawsze od nowa.</summary>
    private void SetAlertsLive(bool live)
    {
        _alertsLive = false;
        ResetAlertTracking();
        if (!live)
        {
            // Bez danych zegar odświeżania stoi i nie wygasiłby migania – linia migałaby bez końca.
            _alertFlashUntil.Clear();
            UpdateAlertFlashing();
        }

        _alertsLive = live;
    }

    private void ResetAlertTracking()
    {
        lock (_alertRangeLock)
        {
            _alertLow = null;
            _alertHigh = null;
        }

        _alertMonitor.Reset();
    }

    // --- Sprawdzanie (wątek UI, co 100 ms) ---

    private void CheckPriceAlerts()
    {
        UpdateAlertFlashing();

        decimal low, high, last;
        lock (_alertRangeLock)
        {
            if (_alertLow is not { } l || _alertHigh is not { } h)
            {
                return;
            }

            (low, high, last) = (l, h, _alertLast);
            _alertLow = null;
            _alertHigh = null;
        }

        // Nawigacja w symulacji (skok, cofanie) to nie ruch rynku – nowy punkt odniesienia bez alarmów.
        // Wyłączone alarmy (albo wyciszone w symulacji) nie „zużywają” poziomów jednokrotnych.
        var muted = !_settings.PriceAlerts.Enabled || IsSimulationMode && !_settings.PriceAlerts.InSimulation;
        if (muted || _simNavigating || _viewCutoff is not null)
        {
            _alertMonitor.Reset();
            return;
        }

        var hits = _alertMonitor.Check(Lines, low, high, last, _settings.Instrument.TickSize);
        if (hits.Count == 0)
        {
            return;
        }

        // Jednokrotne wyłączają się (zapis w pliku – po restarcie dalej „zadziałał”).
        var dataTicks = Interlocked.Read(ref _lastTickUtcTicks);
        DateTimeOffset? now = dataTicks > 0 ? new DateTimeOffset(dataTicks, TimeSpan.Zero) : null;
        var hitIds = hits.Select(h => h.Line.Id).ToHashSet();
        Lines = Lines.Select(l => hitIds.Contains(l.Id) && l.Alert is { } a
                ? l with { Alert = a with { IsTriggered = a.Repeat == AlertRepeat.Once, LastTriggered = now } }
                : l)
            .ToList();
        SaveDrawings();

        foreach (var hit in hits)
        {
            _alertFlashUntil[hit.Line.Id] = DateTime.UtcNow + AlertFlashDuration;
        }

        UpdateAlertFlashing();

        var message = string.Join("   ", hits.Select(DescribeHit));
        if (DateTime.UtcNow - _lastAlertSound >= AlertSoundGap)
        {
            _lastAlertSound = DateTime.UtcNow;
            var sound = hits[0].Line.Alert!.Sound;
            if (!AlertSoundPlayer.Play(sound))
            {
                message += $"   (nie znaleziono pliku dźwięku {AlertSoundPlayer.Describe(sound)} – zagrał dźwięk systemowy)";
            }
        }

        Hint(message);
    }

    private string DescribeHit(PriceAlertHit hit)
    {
        var price = PriceFormat.Format(hit.Line.Price, _settings.Instrument.TickSize);
        var label = string.IsNullOrWhiteSpace(hit.Line.Label) ? string.Empty : $" ({hit.Line.Label})";
        return $"🔔 {_settings.InstrumentSymbol} {(hit.Upward ? "▲ przebił w górę" : "▼ przebił w dół")} {price}{label}";
    }

    /// <summary>Usuwa wygasłe miganie i przekazuje aktualną listę do wykresu.</summary>
    private void UpdateAlertFlashing()
    {
        if (_alertFlashUntil.Count == 0 && _flashingAlerts.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var id in _alertFlashUntil.Where(f => f.Value <= now).Select(f => f.Key).ToList())
        {
            _alertFlashUntil.Remove(id);
        }

        if (!_flashingAlerts.SetEquals(_alertFlashUntil.Keys))
        {
            FlashingAlerts = _alertFlashUntil.Keys.ToHashSet();
        }
    }

    // --- Edycja poziomów (menu wykresu) ---

    /// <summary>Nowy poziom alarmowy na podanej cenie (ustawienia alarmu jak w ostatnio zapisanym poziomie).</summary>
    public PriceLine CreateAlertTemplate(decimal price) => new()
    {
        Price = price,
        Color = _alertTemplate.Color,
        Thickness = _alertTemplate.Thickness,
        LabelPosition = _alertTemplate.LabelPosition,
        Alert = (_alertTemplate.Alert ?? new PriceAlert()) with { IsTriggered = false, LastTriggered = null, IsEnabled = true },
    };

    private PriceLine _alertTemplate = new() { Color = "#FF5252", Thickness = 2, Alert = new PriceAlert() };

    /// <summary>Zwykła linia → poziom alarmowy (z domyślnymi ustawieniami alarmu).</summary>
    public PriceLine ToAlert(PriceLine line) => line with { Alert = CreateAlertTemplate(line.Price).Alert };

    /// <summary>Uzbraja ponownie poziom, który zadziałał.</summary>
    public void RearmAlert(PriceLine line) =>
        ReplaceAlert(line.Id, a => a with { IsTriggered = false, IsEnabled = true });

    /// <summary>Włącza / wyłącza alarm poziomu.</summary>
    public void ToggleAlert(PriceLine line) =>
        ReplaceAlert(line.Id, a => a with { IsEnabled = !a.IsEnabled, IsTriggered = false });

    private void ReplaceAlert(Guid id, Func<PriceAlert, PriceAlert> change)
    {
        Lines = Lines.Select(l => l.Id == id && l.Alert is { } a ? l with { Alert = change(a) } : l).ToList();
        _alertMonitor.Rearm(id);
        SaveDrawings();
    }

    /// <summary>Po edycji poziomu (cena / ustawienia) alarm jest uzbrojony od nowa.</summary>
    private PriceLine RearmedAfterEdit(PriceLine line)
    {
        if (line.Alert is not { } alert)
        {
            return line;
        }

        _alertMonitor.Rearm(line.Id);
        _alertTemplate = line;
        return line with { Alert = alert with { IsTriggered = false } };
    }
}
