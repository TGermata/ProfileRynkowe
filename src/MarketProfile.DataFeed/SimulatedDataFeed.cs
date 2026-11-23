using MarketProfile.Core;

namespace MarketProfile.DataFeed;

/// <summary>
/// Symulator danych rynkowych – tryb "Generator": generuje losowe, realistycznie wyglądające ceny
/// (patrz <see cref="PriceGenerator"/>). Pozwala sprawdzać aplikację bez brokera i bez danych w bazie.
///
/// Czas w symulatorze płynie szybciej niż rzeczywisty (<see cref="Speed"/>), żeby było widać,
/// jak profil rośnie i jak pojawiają się kolejne litery TPO.
/// </summary>
public sealed class SimulatedDataFeed : IMarketDataFeed
{
    private PriceGenerator? _generator;
    private CancellationTokenSource? _liveCts;
    private Task? _liveTask;
    private DateTimeOffset _simTime;
    private Instrument? _instrument;
    private decimal? _oldestOpen;   // otwarcie najstarszej wygenerowanej świecy – do łączenia starszej historii

    /// <summary>Przyspieszenie czasu: 60 oznacza, że 1 sekunda rzeczywista = 1 minuta symulacji.</summary>
    public double Speed { get; set; } = 60;

    public string Name => "Symulator (generator)";

    public event EventHandler<Tick>? TickReceived;
    public event EventHandler<string>? StatusChanged;

    public Task ConnectAsync(Instrument instrument, CancellationToken cancellationToken = default)
    {
        _generator = new PriceGenerator(instrument);
        _instrument = instrument;
        StatusChanged?.Invoke(this, $"Generator: połączono ({instrument.Symbol})");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Bar>> GetHistoryAsync(TimeSpan lookback, CancellationToken cancellationToken = default)
    {
        var generator = EnsureConnected();

        // Generujemy świece 1-minutowe od (teraz - lookback) do teraz.
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero) - lookback;
        var bars = GenerateBars(generator, start, now, cancellationToken);

        _simTime = bars.Count > 0 ? bars[^1].Time.AddMinutes(1) : now;
        _oldestOpen = bars.Count > 0 ? bars[0].Open : null;
        StatusChanged?.Invoke(this, $"Generator: wygenerowano {bars.Count} świec historycznych");
        return Task.FromResult<IReadOnlyList<Bar>>(bars);
    }

    /// <summary>
    /// Starsza historia (doczytywana przy przesuwaniu wykresu w lewo). Świece są generowane osobnym generatorem
    /// i przesuwane cenowo tak, żeby ostatnie zamknięcie pasowało do otwarcia najstarszej już wygenerowanej
    /// świecy – dzięki temu na łączeniu nie ma skoku ceny.
    /// </summary>
    public Task<IReadOnlyList<Bar>> GetHistoryRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var bars = GenerateBars(new PriceGenerator(_instrument!), from, to, cancellationToken);

        if (bars.Count > 0 && _oldestOpen is { } joinPrice)
        {
            var shift = joinPrice - bars[^1].Close;
            bars = bars.Select(b => b with { Open = b.Open + shift, High = b.High + shift, Low = b.Low + shift, Close = b.Close + shift }).ToList();
        }

        if (bars.Count > 0)
        {
            _oldestOpen = bars[0].Open;
        }

        return Task.FromResult<IReadOnlyList<Bar>>(bars);
    }

    /// <summary>Generuje świece 1-minutowe z zakresu [from, to) – każda świeca to 60 jednosekundowych kroków ceny.</summary>
    private static List<Bar> GenerateBars(PriceGenerator generator, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var bars = new List<Bar>();
        for (var time = from; time < to; time = time.AddMinutes(1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var open = generator.Price;
            decimal high = open, low = open;
            long volume = 0;

            for (var s = 0; s < 60; s++)
            {
                generator.Step(1);
                high = Math.Max(high, generator.Price);
                low = Math.Min(low, generator.Price);
                volume += generator.NextVolume(5, 60);
            }

            bars.Add(new Bar(time, open, high, low, generator.Price, volume));
        }

        return bars;
    }

    public Task StartLiveAsync(CancellationToken cancellationToken = default)
    {
        var generator = EnsureConnected();

        if (_simTime == default)
        {
            _simTime = DateTimeOffset.UtcNow;
        }

        _liveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _liveCts.Token;

        // Pętla generująca ticki w tle: co 50 ms rzeczywistych przesuwamy czas symulacji
        // o 50 ms × Speed i generujemy kilka transakcji.
        _liveTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    var simSeconds = 0.05 * Speed;
                    var ticks = generator.Next(1, 4);

                    for (var i = 0; i < ticks; i++)
                    {
                        generator.Step(simSeconds / ticks);
                        _simTime = _simTime.AddSeconds(simSeconds / ticks);
                        TickReceived?.Invoke(this, new Tick(_simTime, generator.Price, generator.NextVolume(1, 20)));
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Zatrzymanie generatora.
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"Generator: błąd – {ex.Message}; dane zatrzymane");
            }
        }, token);

        StatusChanged?.Invoke(this, $"Generator: dane na żywo (przyspieszenie ×{Speed})");
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        if (_liveCts is not null)
        {
            await _liveCts.CancelAsync().ConfigureAwait(false);

            try
            {
                if (_liveTask is not null)
                {
                    await _liveTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Oczekiwane przy zatrzymaniu.
            }
            catch (Exception)
            {
                // Błąd pętli został już zgłoszony (StatusChanged) – rozłączenie i tak musi się udać.
            }
            finally
            {
                _liveCts.Dispose();
                _liveCts = null;
                _liveTask = null;
            }
        }

        StatusChanged?.Invoke(this, "Generator: zatrzymano");
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private PriceGenerator EnsureConnected() =>
        _generator ?? throw new InvalidOperationException("Najpierw wywołaj ConnectAsync.");
}
