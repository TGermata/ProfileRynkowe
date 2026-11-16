namespace MarketProfile.Core;

/// <summary>
/// Profil rynkowy (Market Profile / TPO) jednej sesji – obiekt "żywy", aktualizowany przyrostowo
/// przy każdej nowej transakcji. Przechowuje surowe dane: które wiersze cenowe były odwiedzone
/// w których okresach TPO oraz wolumen (osobno dla każdego wiersza i okresu).
///
/// Dzięki temu, że dane są trzymane per okres, z jednego profilu sesji można utworzyć migawkę
/// dowolnego zakresu okresów – tak działa rozbijanie profilu (patrz <see cref="ProfileSplits"/>).
///
/// Statystyki (POC, Value Area, IB) są liczone w <see cref="GetSnapshot"/>, a warstwa prezentacji
/// dostaje tylko niezmienną migawkę – dzięki temu dane i rysowanie są od siebie oddzielone.
/// Klasa jest bezpieczna wątkowo (dane przychodzą z wątku brokera, a odczyt odbywa się z wątku UI).
/// </summary>
public sealed class SessionProfile
{
    private readonly object _sync = new();

    // Wiersz cenowy -> numery okresów, w których cena była w tym wierszu.
    private readonly SortedDictionary<decimal, SortedSet<int>> _tpoByRow = new();

    // (wiersz, okres) -> wolumen.
    private readonly Dictionary<(decimal Row, int Period), long> _volume = new();

    // Numer okresu -> zakres cen i ostatnia cena w tym okresie.
    private readonly SortedDictionary<int, PeriodData> _periods = new();

    private long _version;

    /// <summary>Dane jednego okresu TPO.</summary>
    private sealed class PeriodData
    {
        public decimal Open;   // pierwsza cena w okresie (cena otwarcia okresu)
        public decimal High;
        public decimal Low;
        public decimal Close;
    }

    /// <summary>Tworzy pusty profil dla sesji rozpoczynającej się w <paramref name="sessionStart"/>.</summary>
    public SessionProfile(SessionDefinition session, DateTime sessionStart, ProfileSettings settings)
    {
        Session = session;
        Settings = settings;
        SessionStart = sessionStart;
        SessionEnd = sessionStart + session.Duration;

        // Data handlowa to data dnia, w którym sesja się kończy (dla sesji nocnej – następny dzień).
        TradingDate = DateOnly.FromDateTime(SessionEnd.AddTicks(-1));
    }

    /// <summary>Sesja, której dotyczy profil.</summary>
    public SessionDefinition Session { get; }

    /// <summary>Ustawienia profilu (wysokość wiersza, długość okresu TPO itd.).</summary>
    public ProfileSettings Settings { get; }

    /// <summary>Początek sesji (czas lokalny giełdy).</summary>
    public DateTime SessionStart { get; }

    /// <summary>Koniec sesji (czas lokalny giełdy).</summary>
    public DateTime SessionEnd { get; }

    /// <summary>Data handlowa profilu.</summary>
    public DateOnly TradingDate { get; }

    /// <summary>Numer wersji – zwiększany przy każdej zmianie danych.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Zdarzenie wywoływane po każdej aktualizacji profilu.</summary>
    public event EventHandler? Updated;

    /// <summary>Czas rozpoczęcia okresu TPO o podanym numerze.</summary>
    public DateTime GetPeriodStart(int periodIndex) => SessionStart + periodIndex * Settings.TpoPeriod;

    /// <summary>Numer okresu TPO, do którego należy podany czas lokalny.</summary>
    public int GetPeriodIndex(DateTime localTime) =>
        (int)((localTime - SessionStart).Ticks / Settings.TpoPeriod.Ticks);

    /// <summary>
    /// Dodaje do profilu ruch ceny w danym okresie TPO.
    /// </summary>
    /// <param name="periodIndex">Numer okresu TPO (0 = A).</param>
    /// <param name="low">Najniższa cena ruchu (dla ticka = cena ticka).</param>
    /// <param name="high">Najwyższa cena ruchu (dla ticka = cena ticka).</param>
    /// <param name="lastPrice">Ostatnia cena (zamknięcie świecy lub cena ticka).</param>
    /// <param name="volume">Wolumen ruchu – rozkładany równo na wiersze od low do high.</param>
    /// <param name="openPrice">
    /// Pierwsza cena ruchu (dla świecy – jej otwarcie; dla ticka – cena ticka).
    /// Zapamiętywana jako cena otwarcia okresu, gdy jest to pierwszy ruch w tym okresie.
    /// </param>
    public void AddPriceRange(int periodIndex, decimal low, decimal high, decimal lastPrice, long volume, decimal? openPrice = null)
    {
        lock (_sync)
        {
            // 1. Aktualizujemy zakres cen okresu TPO (pierwszy ruch w okresie wyznacza jego cenę otwarcia).
            if (!_periods.TryGetValue(periodIndex, out var period))
            {
                period = new PeriodData { Open = openPrice ?? lastPrice, High = high, Low = low };
                _periods[periodIndex] = period;
            }

            period.High = Math.Max(period.High, high);
            period.Low = Math.Min(period.Low, low);
            period.Close = lastPrice;

            // 2. Oznaczamy literą okresu wszystkie wiersze pomiędzy low i high okresu.
            //    (Cena przechodzi przez wszystkie poziomy pomiędzy – dlatego wypełniamy cały zakres.)
            var rowHeight = Settings.RowHeight;
            var lowRow = Settings.ToRowPrice(period.Low);
            var highRow = Settings.ToRowPrice(period.High);

            for (var row = lowRow; row <= highRow; row += rowHeight)
            {
                if (!_tpoByRow.TryGetValue(row, out var periodsInRow))
                {
                    periodsInRow = [];
                    _tpoByRow[row] = periodsInRow;
                }

                periodsInRow.Add(periodIndex); // SortedSet ignoruje duplikaty.
            }

            // 3. Wolumen rozkładamy równo na wiersze zakresu low–high tego ruchu (dla ticka to jeden wiersz,
            //    dla świecy – cały jej zakres). Resztę z dzielenia dostaje wiersz ostatniej ceny.
            var moveLowRow = Settings.ToRowPrice(low);
            var moveHighRow = Settings.ToRowPrice(high);
            var rowCount = (int)((moveHighRow - moveLowRow) / rowHeight) + 1;
            var perRow = volume / rowCount;

            for (var row = moveLowRow; row <= moveHighRow; row += rowHeight)
            {
                AddVolume(row, periodIndex, perRow);
            }

            AddVolume(Settings.ToRowPrice(lastPrice), periodIndex, volume - perRow * rowCount);

            _version++;
        }

        Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Dodaje wolumen do wiersza w danym okresie (wywoływane wewnątrz blokady).</summary>
    private void AddVolume(decimal row, int periodIndex, long volume)
    {
        if (volume > 0)
        {
            _volume[(row, periodIndex)] = _volume.GetValueOrDefault((row, periodIndex)) + volume;
        }
    }

    /// <summary>
    /// Dane do analizy tempa budowania TPO (<see cref="TpoTempo"/>): cena otwarcia sesji i zakres każdej litery
    /// do okresu <paramref name="toPeriod"/> włącznie. Lżejsze niż pełna migawka.
    /// </summary>
    public TempoSession GetTempoSession(int toPeriod = int.MaxValue)
    {
        lock (_sync)
        {
            var periods = _periods
                .Where(p => p.Key <= toPeriod)
                .Select(p => new TpoPeriod(p.Key, GetPeriodStart(p.Key), GetPeriodStart(p.Key + 1), p.Value.High, p.Value.Low)
                {
                    Open = p.Value.Open,
                    Close = p.Value.Close,
                })
                .ToList();
            var open = _periods.Count > 0 ? _periods.First().Value.Open : 0;
            return new TempoSession(Session.Name, SessionStart, Settings.TpoPeriod, Settings.RowHeight, open, periods)
            {
                SessionEnd = SessionEnd,
            };
        }
    }

    /// <summary>
    /// Tworzy niezmienną migawkę profilu z wyliczonymi statystykami (POC, Value Area, Initial Balance).
    /// Można ograniczyć migawkę do zakresu okresów – wtedy powstaje "część" rozbitego profilu
    /// z własnym POC, Value Area i Initial Balance.
    /// </summary>
    /// <param name="fromPeriod">Pierwszy uwzględniany okres (włącznie).</param>
    /// <param name="toPeriod">Ostatni uwzględniany okres (włącznie).</param>
    public ProfileSnapshot GetSnapshot(int fromPeriod = 0, int toPeriod = int.MaxValue)
    {
        lock (_sync)
        {
            var rowHeight = Settings.RowHeight;

            // 1. Wiersze z literami tylko z wybranego zakresu okresów.
            var filtered = new SortedDictionary<decimal, int[]>();
            foreach (var (row, periods) in _tpoByRow)
            {
                var inRange = periods.GetViewBetween(fromPeriod, toPeriod);
                if (inRange.Count > 0)
                {
                    filtered[row] = [.. inRange];
                }
            }

            // 2. Wolumen wierszy z wybranego zakresu okresów.
            var volumeByRow = new Dictionary<decimal, long>();
            long totalVolume = 0;
            foreach (var ((row, period), volume) in _volume)
            {
                if (period >= fromPeriod && period <= toPeriod)
                {
                    volumeByRow[row] = volumeByRow.GetValueOrDefault(row) + volume;
                    totalVolume += volume;
                }
            }

            // 3. Budujemy ciągłą listę wierszy od najniższego do najwyższego (puste wiersze też, z 0 TPO),
            //    bo algorytm Value Area rozszerza obszar wiersz po wierszu.
            var levels = new List<PriceLevel>();
            if (filtered.Count > 0)
            {
                var minRow = filtered.Keys.First();
                var maxRow = filtered.Keys.Last();

                for (var row = minRow; row <= maxRow; row += rowHeight)
                {
                    IReadOnlyList<int> periods = filtered.TryGetValue(row, out var list) ? list : [];
                    levels.Add(new PriceLevel(row, periods, volumeByRow.GetValueOrDefault(row)));
                }
            }

            // 4. Okresy TPO z zakresu.
            var periodList = _periods
                .Where(p => p.Key >= fromPeriod && p.Key <= toPeriod)
                .Select(p => new TpoPeriod(p.Key, GetPeriodStart(p.Key), GetPeriodStart(p.Key + 1), p.Value.High, p.Value.Low)
                {
                    Open = p.Value.Open,
                    Close = p.Value.Close,
                })
                .ToList();

            // 4a. Wolumen bieżącego (ostatniego) okresu tej części – profil wolumenowy aktualnie budowanego słupka TPO.
            var currentPeriod = periodList.Count > 0 ? periodList[^1].Index : -1;
            var currentVolume = new Dictionary<decimal, long>();
            foreach (var ((row, period), volume) in _volume)
            {
                if (period == currentPeriod && volume > 0)
                {
                    currentVolume[row] = currentVolume.GetValueOrDefault(row) + volume;
                }
            }

            // 4b. Największy wolumen wiersza w każdym ZAKOŃCZONYM okresie tej części (bez bieżącego) –
            //     odniesienie dla skali profilu wolumenowego bieżącego słupka.
            var completedMax = new SortedDictionary<int, long>();
            foreach (var ((_, period), volume) in _volume)
            {
                if (period >= fromPeriod && period <= toPeriod && period != currentPeriod &&
                    volume > completedMax.GetValueOrDefault(period))
                {
                    completedMax[period] = volume;
                }
            }

            var lastClose = _periods.LastOrDefault(p => p.Key >= fromPeriod && p.Key <= toPeriod).Value?.Close ?? 0;

            // Cena otwarcia profilu (lub części rozbitego profilu) = otwarcie jego pierwszego okresu.
            var firstOpen = _periods.FirstOrDefault(p => p.Key >= fromPeriod && p.Key <= toPeriod).Value?.Open ?? 0;

            // 5. POC i Value Area (z liter TPO) oraz VPOC (z wolumenu).
            var pocIndex = ProfileCalculator.FindPocIndex(levels, Settings.PocTieBreak);
            var vpocIndex = ProfileCalculator.FindVolumePocIndex(levels);
            var (vaLow, vaHigh) = ProfileCalculator.FindValueArea(levels, pocIndex, Settings.ValueAreaPercent);

            // 6. Initial Balance – zakres pierwszych N okresów (licząc od początku tej części profilu).
            var ibPeriods = periodList.Where(p => p.Index < fromPeriod + Settings.InitialBalancePeriods).ToList();

            return new ProfileSnapshot
            {
                Session = Session,
                TradingDate = TradingDate,
                SessionStart = SessionStart,
                SessionEnd = SessionEnd,
                RowHeight = rowHeight,
                TickSize = Settings.TickSize,
                Levels = levels,
                Periods = periodList,
                FirstPeriod = fromPeriod,
                PartStart = GetPeriodStart(fromPeriod),
                VolumePoc = vpocIndex >= 0 ? levels[vpocIndex].Price : null,
                MaxRowVolume = levels.Count > 0 ? levels.Max(l => l.Volume) : 0,
                High = periodList.Count > 0 ? periodList.Max(p => p.High) : 0,
                Low = periodList.Count > 0 ? periodList.Min(p => p.Low) : 0,
                Poc = pocIndex >= 0 ? levels[pocIndex].Price : 0,
                ValueAreaLow = vaLow >= 0 ? levels[vaLow].Price : 0,
                ValueAreaHigh = vaHigh >= 0 ? levels[vaHigh].Price : 0,
                InitialBalanceHigh = ibPeriods.Count > 0 ? ibPeriods.Max(p => p.High) : null,
                InitialBalanceLow = ibPeriods.Count > 0 ? ibPeriods.Min(p => p.Low) : null,
                LastPrice = lastClose,
                Open = firstOpen,
                TotalTpo = levels.Sum(l => l.TpoCount),
                TotalVolume = totalVolume,
                CurrentPeriodIndex = currentPeriod,
                CurrentPeriodVolume = currentVolume,
                CompletedPeriodMaxVolumes = [.. completedMax.Values],
                Version = _version,
            };
        }
    }

    public override string ToString() => $"{Session.Name} {TradingDate:dd.MM.yyyy}";
}
