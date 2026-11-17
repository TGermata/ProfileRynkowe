using System.Windows.Threading;
using MarketProfile.DataFeed.Storage;

namespace MarketProfile.App.ViewModels;

/// <summary>Waga komunikatu w polu informacji (kolor paska).</summary>
public enum InfoLevel
{
    /// <summary>Nic do pokazania – pole ukryte.</summary>
    None,

    /// <summary>Postęp pobierania w tle (tylko gdy włączono w ustawieniach).</summary>
    Progress,

    /// <summary>Podpowiedź po akcji użytkownika – znika sama po kilku sekundach.</summary>
    Hint,

    /// <summary>Ostrzeżenie o danych (np. dane testowe, brak ticków, błąd pobierania w tle).</summary>
    Warning,

    /// <summary>Błąd (np. brak połączenia z TWS) – zostaje, dopóki problem trwa.</summary>
    Error,
}

/// <summary>
/// Pole informacji – JEDYNE miejsce komunikatów aplikacji. Pokazuje tylko to, co ważne:
///  - błędy (np. brak połączenia z TWS) – do czasu udanego startu źródła danych,
///  - ostrzeżenia o danych (dane testowe, okres bez ticków, brak danych do symulacji, niezgodny tick, błąd pobierania w tle),
///  - podpowiedzi po akcji użytkownika (np. „To początek danych w bazie”) – znikają same po kilku sekundach,
///  - opcjonalnie postęp pobierania w tle (Ustawienia → Dane i broker → Pobieranie i baza).
/// Gdy wszystko działa – pole jest ukryte. Komunikaty rutynowe („wczytywanie…”, „połączono…”) nie są pokazywane.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Jak długo widać podpowiedź.</summary>
    private static readonly TimeSpan HintDuration = TimeSpan.FromSeconds(8);

    private string? _errorText;      // błąd aplikacji (np. start źródła danych)
    private string? _feedWarning;    // problem zgłoszony przez źródło danych (np. brak subskrypcji, zerwane połączenie)
    private string? _hintText;       // podpowiedź po akcji użytkownika
    private DispatcherTimer? _hintTimer;

    /// <summary>Tekst pola informacji – wszystkie aktualne pozycje, najważniejsze pierwsze.</summary>
    public string InfoText => string.Join("   •   ", InfoItems().Select(i => i.Text));

    /// <summary>Najwyższa waga aktualnych pozycji (kolor pola).</summary>
    public InfoLevel InfoLevel => InfoItems().Select(i => i.Level).DefaultIfEmpty(InfoLevel.None).Max();

    /// <summary>Czy pokazać pole informacji.</summary>
    public bool HasInfo => InfoItems().Any();

    /// <summary>Podpowiedź po akcji użytkownika – widoczna kilka sekund.</summary>
    private void Hint(string text)
    {
        _hintText = text;
        _hintTimer ??= CreateHintTimer();
        _hintTimer.Stop();
        _hintTimer.Start();
        NotifyInfo();
    }

    /// <summary>Błąd – widoczny, dopóki nie zostanie usunięty (np. udanym startem źródła danych).</summary>
    private void ShowError(string text)
    {
        _errorText = text;
        NotifyInfo();
    }

    /// <summary>Usuwa błąd i problem źródła danych (np. po udanym połączeniu albo zmianie trybu).</summary>
    private void ClearError()
    {
        _errorText = null;
        _feedWarning = null;
        NotifyInfo();
    }

    /// <summary>
    /// Komunikat od źródła danych: problemy (błąd, brak danych/subskrypcji, zerwane połączenie, ostrzeżenie IB) trafiają
    /// do pola informacji; komunikaty rutynowe (łączenie, pobieranie historii, „połączono”) są pomijane, a komunikat
    /// o działających danych usuwa wcześniejszy problem. Symulacja zgłasza swoje stany sama – jej komunikaty są pomijane.
    /// </summary>
    private void OnFeedMessage(object? sender, string message)
    {
        if (sender is DatabaseReplayFeed)
        {
            return;
        }

        var text = message.ToLowerInvariant();
        if (text.Contains("błąd") || text.Contains("brak ") || text.Contains("uwaga") || text.Contains("zamknięte"))
        {
            _feedWarning = message;
            NotifyInfo();
        }
        else if (text.Contains("połączono") || text.StartsWith("ib: dane", StringComparison.Ordinal))
        {
            _feedWarning = null;
            NotifyInfo();
        }
    }

    /// <summary>
    /// Krótki powód błędu pobierania w tle: brak połączenia z TWS (najczęstszy – ten sam dla każdego instrumentu) jednym
    /// zdaniem, inny błąd – pierwszy z listy, skrócony.
    /// </summary>
    private static string ShortDownloadError(string error)
    {
        var text = error.ToLowerInvariant();
        if (text.Contains("nawiązać połączenia") || text.Contains("odmawia") || text.Contains("brak połączenia") ||
            text.Contains("connection refused") || text.Contains("502"))
        {
            return "brak połączenia z TWS / IB Gateway";
        }

        var first = error.Split(';', 2)[0].Trim();
        return first.Length > 140 ? first[..140] + "…" : first;
    }

    private DispatcherTimer CreateHintTimer()
    {
        var timer = new DispatcherTimer { Interval = HintDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _hintText = null;
            NotifyInfo();
        };
        return timer;
    }

    /// <summary>Aktualne pozycje pola informacji, od najważniejszej.</summary>
    private IEnumerable<(InfoLevel Level, string Text)> InfoItems()
    {
        if (_errorText is { } error)
        {
            yield return (InfoLevel.Error, error);
        }

        if (_feedWarning is { } feed)
        {
            yield return (InfoLevel.Error, feed);
        }

        foreach (var warning in DataWarnings())
        {
            yield return (InfoLevel.Warning, warning);
        }

        // Błąd pobierania w tle – tylko gdy nie mówi tego samego co błąd połączenia (na żywo bez TWS).
        if (_backgroundError is { } background && _errorText is null)
        {
            var retry = _nextTickCheck is { } next ? $" (ponowienie o {next:HH:mm})" : string.Empty;
            yield return (InfoLevel.Warning, $"Pobieranie danych w tle nie działa – {ShortDownloadError(background)}{retry}");
        }

        if (_hintText is { } hint)
        {
            yield return (InfoLevel.Hint, hint);
        }

        if (_settings.ShowDownloadProgress && _backgroundError is null && TickDownloadText.Length > 0)
        {
            yield return (InfoLevel.Progress, TickDownloadText);
        }
    }

    /// <summary>Odświeża pole informacji w widoku.</summary>
    private void NotifyInfo()
    {
        OnPropertyChanged(nameof(InfoText));
        OnPropertyChanged(nameof(InfoLevel));
        OnPropertyChanged(nameof(HasInfo));
    }
}
