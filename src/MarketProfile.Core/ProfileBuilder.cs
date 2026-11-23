namespace MarketProfile.Core;

/// <summary>
/// Silnik budujący profile z napływających danych (ticków na żywo i świec historycznych).
/// Dla każdej transakcji ustala:
///  1) do której sesji należy (dzienna/nocna) – i tworzy nowy profil, gdy zaczyna się nowa sesja,
///  2) do którego okresu TPO należy (A, B, C...),
/// a następnie przekazuje dane do odpowiedniego obiektu <see cref="SessionProfile"/>.
/// Transakcje spoza zdefiniowanych sesji są pomijane.
/// </summary>
public sealed class ProfileBuilder
{
    private readonly object _sync = new();
    private readonly Dictionary<(string Session, DateTime Start), SessionProfile> _byKey = new();
    private readonly List<SessionProfile> _profiles = [];

    public ProfileBuilder(ProfileSettings settings)
    {
        Settings = settings;
    }

    /// <summary>Ustawienia, według których budowane są profile.</summary>
    public ProfileSettings Settings { get; }

    /// <summary>Zdarzenie wywoływane, gdy powstaje nowy profil (zaczęła się nowa sesja).</summary>
    public event EventHandler<SessionProfile>? ProfileCreated;

    /// <summary>Kopia listy wszystkich profili posortowanych chronologicznie.</summary>
    public IReadOnlyList<SessionProfile> Profiles
    {
        get
        {
            lock (_sync)
            {
                return [.. _profiles];
            }
        }
    }

    /// <summary>Przetwarza pojedynczy tick (transakcję na żywo).</summary>
    public void ProcessTick(Tick tick)
    {
        // Strona transakcji: zmierzona (bid/ask) albo oszacowana regułą ticka – do profilu wolumenowego różnicowego (delta).
        TradeSide side;
        lock (_tickRule)
        {
            side = _tickRule.Classify(tick);
            if (tick.Time > _tickRuleTime)
            {
                _tickRuleTime = tick.Time;
            }
        }

        if (TryResolve(tick.Time, out var profile, out var periodIndex, out var minute))
        {
            profile.AddPriceRange(periodIndex, tick.Price, tick.Price, tick.Price, tick.Volume, minute: minute, side: side);
        }
    }

    private readonly TickRule _tickRule = new();

    /// <summary>Czas najnowszych danych, które ustawiły punkt odniesienia reguły ticka (chronione blokadą _tickRule).</summary>
    private DateTimeOffset _tickRuleTime = DateTimeOffset.MinValue;

    /// <summary>
    /// Przetwarza świecę historyczną. Świeca jest przypisywana do okresu TPO według czasu jej rozpoczęcia,
    /// a jej zakres High–Low oznaczany jest literą okresu. (Świece 1-minutowe zawsze mieszczą się w jednym okresie.)
    /// </summary>
    public void ProcessBar(Bar bar)
    {
        // Świeca nie ma stron transakcji – tylko punkt odniesienia reguły ticka. Tylko świeca nie starsza niż dotychczasowe
        // dane: doczytywana starsza historia (np. sprzed tygodnia) nie może przestawić odniesienia dla ticków na żywo.
        lock (_tickRule)
        {
            if (bar.Time >= _tickRuleTime)
            {
                _tickRule.Observe(bar.Close);
                _tickRuleTime = bar.Time;
            }
        }

        if (TryResolve(bar.Time, out var profile, out var periodIndex, out var minute))
        {
            profile.AddPriceRange(periodIndex, bar.Low, bar.High, bar.Close, bar.Volume, bar.Open, minute);
        }
    }

    /// <summary>
    /// Znajduje (lub tworzy) profil dla danego czasu i wylicza numer okresu TPO oraz minutę od początku sesji.
    /// </summary>
    private bool TryResolve(DateTimeOffset time, out SessionProfile profile, out int periodIndex, out int minute)
    {
        // Przeliczamy czas transakcji na strefę czasową giełdy (godziny sesji są podane w tej strefie).
        var local = TimeZoneInfo.ConvertTime(time, Settings.TimeZone).DateTime;

        foreach (var session in Settings.Sessions)
        {
            if (!session.TryGetSessionStart(local, out var sessionStart))
            {
                continue;
            }

            profile = GetOrCreate(session, sessionStart);
            periodIndex = (int)((local - sessionStart).Ticks / Settings.TpoPeriod.Ticks);
            minute = (int)((local - sessionStart).Ticks / TimeSpan.TicksPerMinute);
            return true;
        }

        profile = null!;
        periodIndex = -1;
        minute = -1;
        return false;
    }

    private SessionProfile GetOrCreate(SessionDefinition session, DateTime sessionStart)
    {
        SessionProfile? created = null;
        SessionProfile profile;

        lock (_sync)
        {
            if (!_byKey.TryGetValue((session.Name, sessionStart), out profile!))
            {
                profile = new SessionProfile(session, sessionStart, Settings);
                _byKey[(session.Name, sessionStart)] = profile;

                // Wstawiamy w kolejności chronologicznej (dane historyczne mogą przyjść po żywych).
                var index = _profiles.FindIndex(p => p.SessionStart > sessionStart);
                _profiles.Insert(index < 0 ? _profiles.Count : index, profile);
                created = profile;
            }
        }

        // Zdarzenie wywołujemy poza blokadą, żeby uniknąć zakleszczeń.
        if (created is not null)
        {
            ProfileCreated?.Invoke(this, created);
        }

        return profile;
    }
}
