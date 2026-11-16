using MarketProfile.Core;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Parametry profili (<see cref="ProfileStats"/>) do decyzji handlowych i automatycznego tradingu:
///  - liczone na bieżąco dla wszystkich wczytanych profili (pełne sesje – rozbicie profilu nie zmienia parametrów),
///  - dostępne w kodzie: <see cref="ProfileStatsList"/>, <see cref="CurrentProfileStats"/>, zdarzenie <see cref="ProfileStatsUpdated"/>,
///  - zapisywane w bazie (tabela profile_stats – czytelna także dla innych programów): zakończone sesje zawsze,
///    a bieżąca sesja na żywo po każdej literze (is_complete = 0).
/// "Teraz" to czas danych (<see cref="DataNow"/>) – w symulacji parametry nie widzą przyszłości.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<SessionProfile, (long Version, int ToPeriod, ProfileSnapshot Snapshot)> _fullSnapshots = [];
    private string _statsKey = string.Empty;
    private IReadOnlyList<ProfileStats> _profileStats = [];
    private readonly Dictionary<string, string> _savedStats = [];

    /// <summary>Parametry wczytanych profili, od najstarszego (ostatni = bieżący profil).</summary>
    public IReadOnlyList<ProfileStats> ProfileStatsList => _profileStats;

    /// <summary>Parametry bieżącego (najnowszego) profilu; null = brak danych.</summary>
    public ProfileStats? CurrentProfileStats => _profileStats.Count > 0 ? _profileStats[^1] : null;

    /// <summary>Wywoływane po przeliczeniu parametrów (zmiana danych, nowa litera, zakończenie sesji).</summary>
    public event EventHandler<IReadOnlyList<ProfileStats>>? ProfileStatsUpdated;

    /// <summary>Przelicza parametry profili, gdy zmieniły się dane, „teraz” przesunęło się o literę albo sesja się zakończyła.</summary>
    private void UpdateProfileStats()
    {
        if (_builder is null)
        {
            SetProfileStats([], string.Empty);
            return;
        }

        var now = DataNow();
        var profiles = _builder.Profiles;
        var tpoMinutes = (int)_builder.Settings.TpoPeriod.TotalMinutes;

        // Profile do „teraz” (w symulacji – bez przyszłości), z liczbą liter do bieżącej chwili.
        var visible = new List<(SessionProfile Profile, int ToPeriod, bool Complete)>();
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            var toPeriod = int.MaxValue;
            var complete = i < profiles.Count - 1; // po nim zaczął się już kolejny profil
            if (now is { } n)
            {
                var local = TimeZoneInfo.ConvertTime(n, profile.Settings.TimeZone).DateTime;
                if (local <= profile.SessionStart)
                {
                    break; // ten i dalsze profile zaczynają się po „teraz”
                }

                if (local < profile.SessionEnd)
                {
                    toPeriod = profile.GetPeriodIndex(local.AddTicks(-1));
                    complete = false;
                }
                else
                {
                    complete = true;
                }
            }

            visible.Add((profile, toPeriod, complete));
        }

        // Klucz stanu: wersje danych, odcięcie i zakończenie – bez zmian = bez przeliczania.
        var key = string.Join("|", visible.Select(v => $"{v.Profile.SessionStart.Ticks}:{v.Profile.Version}:{v.ToPeriod}:{v.Complete}"));
        if (key == _statsKey)
        {
            return;
        }

        var snapshots = new List<(ProfileSnapshot Snapshot, bool Complete)>();
        foreach (var (profile, toPeriod, complete) in visible)
        {
            if (!_fullSnapshots.TryGetValue(profile, out var cached) || cached.Version != profile.Version || cached.ToPeriod != toPeriod)
            {
                cached = (profile.Version, toPeriod, profile.GetSnapshot(0, toPeriod));
                _fullSnapshots[profile] = cached;
            }

            if (!cached.Snapshot.IsEmpty)
            {
                snapshots.Add((cached.Snapshot, complete));
            }
        }

        // Profile spoza listy (np. po zmianie instrumentu) – usuwamy z pamięci podręcznej.
        foreach (var stale in _fullSnapshots.Keys.Except(visible.Select(v => v.Profile)).ToList())
        {
            _fullSnapshots.Remove(stale);
        }

        var history = new List<ProfileSnapshot>(snapshots.Count);
        var stats = new List<ProfileStats>(snapshots.Count);
        foreach (var (snapshot, complete) in snapshots)
        {
            stats.Add(ProfileAnalyzer.Analyze(snapshot, history, complete, tpoMinutes));
            history.Add(snapshot);
        }

        SetProfileStats(stats, key);
        SaveProfileStatsAsync(stats);
    }

    private void SetProfileStats(IReadOnlyList<ProfileStats> stats, string key)
    {
        _statsKey = key;
        if (stats.Count == 0 && _profileStats.Count == 0)
        {
            return;
        }

        _profileStats = stats;
        OnPropertyChanged(nameof(ProfileStatsList));
        OnPropertyChanged(nameof(CurrentProfileStats));
        ProfileStatsUpdated?.Invoke(this, stats);
    }

    /// <summary>
    /// Zapis do bazy (w tle) parametrów, które się zmieniły: zakończone sesje – zawsze (dane z brokera albo symulacja,
    /// która odtwarza te same prawdziwe dane); bieżąca sesja – tylko na żywo, po każdej nowej literze.
    /// Dane testowe z generatora nie są zapisywane.
    /// </summary>
    private void SaveProfileStatsAsync(IReadOnlyList<ProfileStats> stats)
    {
        var real = IsSimulationMode ? _simSource == MarketDataStore.SourceBroker : IsBrokerLive;
        if (!real)
        {
            return;
        }

        var toSave = new List<ProfileStats>();
        foreach (var s in stats)
        {
            if (!s.IsComplete && (IsSimulationMode || !ReferenceEquals(s, stats[^1])))
            {
                continue; // niezakończone – tylko bieżąca sesja na żywo
            }

            // Zapisujemy, gdy zmieniło się coś istotnego (zakończenie, liczba liter, poziomy, relacje).
            var signature = $"{s.IsComplete}:{s.Periods}:{s.High}:{s.Low}:{s.Poc}:{s.ValueAreaHigh}:{s.ValueAreaLow}:{s.RowHeight}:" +
                            $"{s.TpoMinutes}:{s.PreviousTradingDate}:{string.Join(",", s.NakedPocs)}:{s.Close}";
            if (!s.IsComplete)
            {
                signature = $"{s.Periods}:{s.RowHeight}:{s.TpoMinutes}"; // bieżąca sesja – po każdej nowej literze
            }

            var id = $"{_settings.InstrumentSymbol}|{s.Session}|{s.SessionStart:O}";
            if (_savedStats.TryGetValue(id, out var saved) && saved == signature)
            {
                continue;
            }

            _savedStats[id] = signature;
            toSave.Add(s);
        }

        if (toSave.Count == 0)
        {
            return;
        }

        var store = GetStore();
        var symbol = _settings.InstrumentSymbol;
        _ = Task.Run(() =>
        {
            try
            {
                store.SaveProfileStats(symbol, toSave);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Zapis parametrów profili nie powiódł się: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Parametry profili do tabeli: zapisane w bazie + bieżące z pamięci (bieżące zastępują zapisane z tej samej sesji).
    /// </summary>
    public IReadOnlyList<ProfileStats> GetProfileStatsForView(DateOnly? from = null, DateOnly? to = null)
    {
        IReadOnlyList<ProfileStats> stored;
        try
        {
            stored = GetStore().ReadProfileStats(_settings.InstrumentSymbol, from, to);
        }
        catch (Exception)
        {
            stored = [];
        }

        var merged = stored.ToDictionary(s => (s.Session, s.SessionStart));
        foreach (var s in _profileStats.Where(s => (from is null || s.TradingDate >= from) && (to is null || s.TradingDate <= to)))
        {
            merged[(s.Session, s.SessionStart)] = s;
        }

        return [.. merged.Values.OrderBy(s => s.SessionStart)];
    }

    /// <summary>Symbol, którego dotyczą parametry.</summary>
    public string ProfileStatsSymbol => _settings.InstrumentSymbol;
}
