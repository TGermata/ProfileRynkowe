namespace MarketProfile.Core;

/// <summary>
/// Wskaźnik „Oddech rynku”: od początku sesji kasowej sumuje rzeczywisty wolumen kupna (transakcje po cenie ask
/// i wyżej) i sprzedaży (po bid i niżej) – stronę ticka wyznacza notowanie bid/ask (<see cref="TradeSideClassifier"/>),
/// a nie zmiana ceny. Ticki bez zmierzonej strony (transakcja między bid a ask, brak notowań) są pomijane.
///
/// Wartość ze znakiem (<see cref="SignedRatio"/>): przewaga kupna – kupno/sprzedaż (np. +1,25),
/// przewaga sprzedaży – −(sprzedaż/kupno) (np. −1,25). Ticki przychodzą z wątku źródła danych – klasa jest bezpieczna wątkowo.
/// </summary>
public sealed class MarketBreathCalculator(Instrument instrument)
{
    private readonly Lock _lock = new();
    private DateOnly? _sessionDate;
    private long _buy;
    private long _sell;
    private DateTimeOffset _countFrom = DateTimeOffset.MinValue;

    /// <summary>Instrument, którego wolumen jest liczony (np. SPY).</summary>
    public Instrument Instrument => instrument;

    /// <summary>Wolumen kupna w bieżącej sesji.</summary>
    public long BuyVolume { get { lock (_lock) { return _buy; } } }

    /// <summary>Wolumen sprzedaży w bieżącej sesji.</summary>
    public long SellVolume { get { lock (_lock) { return _sell; } } }

    /// <summary>Data sesji kasowej, z której są sumy (null – brak danych).</summary>
    public DateOnly? SessionDate { get { lock (_lock) { return _sessionDate; } } }

    /// <summary>Bieżąca wartość wskaźnika (zaokrąglona do 2 miejsc); null – brak kupna albo sprzedaży.</summary>
    public decimal? Value
    {
        get
        {
            lock (_lock)
            {
                return SignedRatio(_buy, _sell);
            }
        }
    }

    /// <summary>
    /// Stosunek ze znakiem, zaokrąglony do 2 miejsc: kupno ≥ sprzedaż → kupno/sprzedaż (≥ 1,00),
    /// sprzedaż &gt; kupno → −(sprzedaż/kupno) (≤ −1,00). Null, gdy któraś strona jest zerowa.
    /// </summary>
    public static decimal? SignedRatio(long buy, long sell)
    {
        if (buy <= 0 || sell <= 0)
        {
            return null;
        }

        return buy >= sell
            ? Math.Round((decimal)buy / sell, 2, MidpointRounding.AwayFromZero)
            : -Math.Round((decimal)sell / buy, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Zaczyna liczenie od sum zapisanych wcześniej (np. ticki z bazy od otwarcia sesji): ticki na żywo starsze
    /// lub równe <paramref name="countFrom"/> nie są już doliczane – nie liczymy ich dwa razy.
    /// </summary>
    public void Load(IEnumerable<Tick> ticks, DateTimeOffset countFrom)
    {
        lock (_lock)
        {
            _sessionDate = null;
            _buy = _sell = 0;
            _countFrom = DateTimeOffset.MinValue;
            foreach (var tick in ticks)
            {
                AddCore(tick);
            }

            _countFrom = countFrom;
        }
    }

    /// <summary>Dolicza tick (tylko ze zmierzoną stroną, tylko z sesji kasowej; nowa sesja – sumy od zera).</summary>
    public void Add(Tick tick)
    {
        lock (_lock)
        {
            AddCore(tick);
        }
    }

    private void AddCore(Tick tick)
    {
        if (tick.Side == TradeSide.Unknown || tick.Volume <= 0 || tick.Time <= _countFrom ||
            CashSession.DateOf(instrument, tick.Time) is not { } date)
        {
            return;
        }

        if (_sessionDate is not { } current || date > current)
        {
            _sessionDate = date;
            _buy = _sell = 0;
        }
        else if (date < current)
        {
            return; // tick ze starszej sesji
        }

        if (tick.Side == TradeSide.Buy)
        {
            _buy += tick.Volume;
        }
        else
        {
            _sell += tick.Volume;
        }
    }
}
