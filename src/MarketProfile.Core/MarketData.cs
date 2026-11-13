namespace MarketProfile.Core;

/// <summary>
/// Pojedyncza transakcja (tick) otrzymana od brokera.
/// </summary>
/// <param name="Time">Czas transakcji (z informacją o strefie czasowej).</param>
/// <param name="Price">Cena transakcji.</param>
/// <param name="Volume">Wolumen (liczba kontraktów).</param>
public readonly record struct Tick(DateTimeOffset Time, decimal Price, long Volume);

/// <summary>
/// Świeca (bar) z danymi historycznymi: cena otwarcia, maksimum, minimum, zamknięcia i wolumen.
/// Dane historyczne pobieramy jako świece 1-minutowe, żeby szybko odbudować profile z przeszłości.
/// </summary>
/// <param name="Time">Czas rozpoczęcia świecy.</param>
/// <param name="Open">Cena otwarcia.</param>
/// <param name="High">Najwyższa cena w świecy.</param>
/// <param name="Low">Najniższa cena w świecy.</param>
/// <param name="Close">Cena zamknięcia.</param>
/// <param name="Volume">Wolumen w świecy.</param>
public readonly record struct Bar(DateTimeOffset Time, decimal Open, decimal High, decimal Low, decimal Close, long Volume);
