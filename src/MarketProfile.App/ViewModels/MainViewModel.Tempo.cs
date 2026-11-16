using System.Globalization;
using MarketProfile.Core;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Tempo budowania TPO najnowszego profilu (<see cref="TpoTempo"/>): liczba do odwołań w kodzie (<see cref="CurrentTempo"/>,
/// zdarzenie <see cref="TempoUpdated"/> – np. dla automatycznego tradingu) oraz opis przy zegarze TPO i w pasku na dole.
/// "Teraz" to czas danych (zegar symulacji / ostatni tick / odcięcie podglądu) – analiza nie widzi przyszłości.
/// </summary>
public sealed partial class MainViewModel
{
    private TempoAnalysis _tempoAnalysis = TempoAnalysis.Empty;
    private TempoReading _currentTempo = TempoReading.Empty;
    private (string Session, DateTime Start, int Count) _tempoReferenceKey;
    private TempoReference _tempoReference = TempoReference.None;
    private double _tempoValue = double.NaN;
    private string _tempoStateLabel = string.Empty;
    private string _tempoSummary = string.Empty;

    /// <summary>Linia tempa w pasku na dole: wartość, stan i ostatnie zatrzymanie (pusta = tempo wyłączone).</summary>
    public string TempoSummary
    {
        get => _tempoSummary;
        private set => SetProperty(ref _tempoSummary, value);
    }

    /// <summary>Bieżący odczyt tempa najnowszego profilu (liczby: Tempo, TempoChange, NewRowsPerPeriod, stan, poziomy).</summary>
    public TempoReading CurrentTempo
    {
        get => _currentTempo;
        private set => SetProperty(ref _currentTempo, value);
    }

    /// <summary>Pełna analiza tempa najnowszego profilu: tempo każdej litery i zdarzenia zatrzymania.</summary>
    public TempoAnalysis TempoAnalysis => _tempoAnalysis;

    /// <summary>Wywoływane, gdy zmienił się odczyt tempa (inna wartość lub stan, nie sam upływ czasu).</summary>
    public event EventHandler<TempoReading>? TempoUpdated;

    /// <summary>Tempo dla wskaźnika ze wskazówką i koloru (× normy, ze znakiem; NaN = brak tempa).</summary>
    public double TempoValue
    {
        get => _tempoValue;
        private set => SetProperty(ref _tempoValue, value);
    }

    /// <summary>Czy tempo jest mierzone – na początku sesji (stan <see cref="TempoState.Starting"/>) wskaźnik jest szary.</summary>
    public bool TempoActive
    {
        get => _tempoActive;
        private set => SetProperty(ref _tempoActive, value);
    }

    private bool _tempoActive = true;

    /// <summary>Opis stanu tempa pod wskaźnikiem, np. „Zwalnia ↑”, „Zatrzymanie przy 7674.75”.</summary>
    public string TempoStateLabel
    {
        get => _tempoStateLabel;
        private set => SetProperty(ref _tempoStateLabel, value);
    }

    /// <summary>
    /// Na żywo: po takim czasie bez transakcji „teraz” to zegar komputera (rynek zamknięty / przerwa).
    /// Więcej niż opóźnienie danych opóźnionych IB (ok. 15 min), żeby nie mylić opóźnienia z brakiem handlu.
    /// </summary>
    private static readonly TimeSpan LiveQuietAfter = TimeSpan.FromMinutes(20);

    /// <summary>Moment „teraz” w czasie danych: zegar symulacji, ostatni tick na żywo albo odcięcie podglądu (null = brak).</summary>
    private DateTimeOffset? DataNow()
    {
        if (SimFeed is { } sim)
        {
            return sim.CurrentTime;
        }

        if (_viewCutoff is { } cutoff)
        {
            return cutoff;
        }

        if (IsRunning)
        {
            // Na żywo: czas ostatniej transakcji (dane opóźnione też mają czas giełdy). Gdy transakcji od dawna nie ma
            // (rynek zamknięty, przerwa) – zegar komputera, żeby tempo spadło do 0 zamiast pokazywać ostatnią literę.
            var wallClock = DateTimeOffset.UtcNow;
            var ticks = Interlocked.Read(ref _lastTickUtcTicks);
            if (ticks > 0 && wallClock - new DateTimeOffset(ticks, TimeSpan.Zero) < LiveQuietAfter)
            {
                return new DateTimeOffset(ticks, TimeSpan.Zero);
            }

            return wallClock;
        }

        return null;
    }

    /// <summary>Przelicza tempo najnowszego profilu (wywoływane przy zmianie profili i co odświeżenie zegara).</summary>
    private void UpdateTempo()
    {
        if (_builder is null)
        {
            SetTempo(TempoAnalysis.Empty);
            return;
        }

        var now = DataNow();
        var profiles = _builder.Profiles;
        SessionProfile? current = null;
        var toPeriod = int.MaxValue;
        DateTime? nowLocal = null;

        for (var i = profiles.Count - 1; i >= 0; i--)
        {
            var profile = profiles[i];
            var limit = int.MaxValue;
            if (now is { } n)
            {
                var local = TimeZoneInfo.ConvertTime(n, profile.Settings.TimeZone).DateTime;
                if (local <= profile.SessionStart)
                {
                    continue; // profil zaczyna się po „teraz”
                }

                nowLocal = local;
                if (local < profile.SessionEnd)
                {
                    limit = profile.GetPeriodIndex(local.AddTicks(-1));
                }
            }

            if (profile.GetTempoSession(limit).Periods.Count > 0)
            {
                current = profile;
                toPeriod = limit;
                break;
            }
        }

        if (current is null)
        {
            SetTempo(TempoAnalysis.Empty);
            return;
        }

        // Norma z poprzednich sesji tego samego typu – liczona raz (zmienia się dopiero z nową sesją lub historią).
        var history = profiles.Where(p => p.Session.Name == current.Session.Name && p.SessionStart < current.SessionStart).ToList();
        var key = (current.Session.Name, current.SessionStart, history.Count);
        if (key != _tempoReferenceKey)
        {
            _tempoReferenceKey = key;
            _tempoReference = TempoReference.FromHistory(
                history.Select(p => p.GetTempoSession()), TempoOptions.Default.HistorySessions, TempoOptions.Default.WarmupPeriods);
        }

        SetTempo(TpoTempo.Analyze(current.GetTempoSession(toPeriod), _tempoReference, nowLocal));
    }

    private void SetTempo(TempoAnalysis analysis)
    {
        _tempoAnalysis = analysis;
        var reading = analysis.Current;
        var changed = (reading with { Time = default }) != (_currentTempo with { Time = default });
        CurrentTempo = reading;
        TempoValue = reading.State == TempoState.None ? double.NaN : reading.Tempo;
        TempoActive = reading.State != TempoState.Starting;
        TempoStateLabel = reading.State == TempoState.None ? string.Empty : TempoStateText(reading);

        if (changed)
        {
            TempoUpdated?.Invoke(this, reading);
        }

        TempoSummary = TempoSummaryText();
    }

    private string FormatPrice(decimal price) => PriceFormat.Format(price, _settings.Instrument.TickSize);

    /// <summary>Opis stanu tempa, np. „Zwalnia ↑”, „Zatrzymanie przy 7655.50”, „Równowaga 7640.00–7655.50”.</summary>
    private string TempoStateText(TempoReading r)
    {
        var arrow = r.Direction > 0 ? "↑" : r.Direction < 0 ? "↓" : string.Empty;
        return r.State switch
        {
            TempoState.Fast => $"Szybkie {arrow}",
            TempoState.Normal => $"Normalne {arrow}",
            TempoState.Slowing => $"Zwalnia {arrow}",
            TempoState.Stopped when r.StopPrice is { } stop => $"Zatrzymanie przy {FormatPrice(stop)}",
            TempoState.Balance when r is { BalanceLow: { } low, BalanceHigh: { } high } => $"Równowaga {FormatPrice(low)}–{FormatPrice(high)}",
            TempoState.NoTrading => "Brak handlu",
            TempoState.SessionEnded => "Sesja zakończona – brak handlu",
            TempoState.Starting => $"Początek sesji ({r.Letter}) – za wcześnie na pomiar tempa",
            _ => string.Empty,
        };
    }

    private static string Signed(double value) =>
        (value > 0 ? "+" : value < 0 ? "−" : string.Empty) + Math.Abs(value).ToString("0.0", CultureInfo.CurrentCulture);

    /// <summary>Fragment paska na dole: stan tempa i ostatnie zdarzenie zatrzymania.</summary>
    private string TempoSummaryText()
    {
        var r = CurrentTempo;
        if (!_settings.TempoInStatusBar || r.State == TempoState.None)
        {
            return string.Empty;
        }

        var text = $"Tempo {(r.IsProjected ? "~" : string.Empty)}{Signed(r.Tempo)} ({Signed(r.TempoChange)})  {TempoStateText(r)}  " +
                   $"[litera {r.Letter}, nowe wiersze {Signed(r.NewRowsPerPeriod)}]";
        if (_tempoAnalysis.Events.Count > 0)
        {
            var last = _tempoAnalysis.Events[^1];
            var move = (last.Move >= 0 ? "+" : "−") + FormatPrice(Math.Abs(last.Move));
            text += $"   Ostatnie zatrzymanie: {last.Time:HH:mm} przy {FormatPrice(last.Price)} po ruchu {move} w {last.Duration.TotalMinutes:0} min";
        }

        return text;
    }
}
