namespace MarketProfile.Core;

/// <summary>
/// Wspólny interfejs źródła danych rynkowych.
/// Każdy broker (Interactive Brokers, w przyszłości inni) oraz symulator implementują ten interfejs,
/// dzięki czemu reszta aplikacji nie wie, skąd pochodzą dane – źródło można podmienić bez zmian w modelu i widoku.
/// </summary>
public interface IMarketDataFeed : IAsyncDisposable
{
    /// <summary>Nazwa źródła danych wyświetlana użytkownikowi.</summary>
    string Name { get; }

    /// <summary>Zdarzenie wywoływane dla każdej nowej transakcji (ticka) na żywo.</summary>
    event EventHandler<Tick>? TickReceived;

    /// <summary>Zdarzenie z komunikatami o stanie połączenia (do wyświetlenia na pasku statusu).</summary>
    event EventHandler<string>? StatusChanged;

    /// <summary>Łączy się ze źródłem danych dla wybranego instrumentu.</summary>
    Task ConnectAsync(Instrument instrument, CancellationToken cancellationToken = default);

    /// <summary>Pobiera historyczne świece 1-minutowe z podanego okresu wstecz (do chwili obecnej).</summary>
    Task<IReadOnlyList<Bar>> GetHistoryAsync(TimeSpan lookback, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pobiera świece 1-minutowe z zakresu [<paramref name="from"/>, <paramref name="to"/>) – używane do doczytywania
    /// starszej historii, gdy użytkownik przesuwa wykres w lewo. Pusta lista = brak starszych danych.
    /// </summary>
    Task<IReadOnlyList<Bar>> GetHistoryRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    /// <summary>Rozpoczyna strumień ticków na żywo (zdarzenie <see cref="TickReceived"/>).</summary>
    Task StartLiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Zatrzymuje strumień i rozłącza się.</summary>
    Task DisconnectAsync();
}
