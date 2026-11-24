using MarketProfile.Core;

namespace MarketProfile.App.ViewModels;

/// <summary>
/// Automatyczne ponowne połączenie z brokerem po zerwaniu połączenia (np. nocny restart TWS / IB Gateway):
/// źródło jest zatrzymywane (profile zostają na ekranie), a start jest ponawiany coraz rzadziej (5 s … 60 s),
/// dopóki się nie uda albo użytkownik sam czegoś nie zmieni (Start/Stop, tryb, ustawienia).
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan[] ReconnectDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60)];

    private CancellationTokenSource? _reconnectCts;

    private void OnFeedConnectionLost(object? sender, string reason) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            if (!ReferenceEquals(sender, _feed))
            {
                return;
            }

            try
            {
                AppLog.Warning("Połączenie", $"utrata połączenia: {reason} – zatrzymuję źródło i łączę ponownie");
                await StopAsync();
                ShowError($"{reason} – ponowne łączenie...");
                await ReconnectLoopAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error("Połączenie", $"ponowne łączenie po „{reason}” nie powiodło się", ex);
                ShowError($"{reason}; ponowne łączenie nie powiodło się: {ex.Message}");
            }
        });

    private async Task ReconnectLoopAsync()
    {
        CancelReconnect();
        var cts = _reconnectCts = new CancellationTokenSource();
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var delay = ReconnectDelays[Math.Min(attempt, ReconnectDelays.Length - 1)];
                await Task.Delay(delay, cts.Token);

                // Użytkownik w międzyczasie coś uruchomił albo przełączył tryb – nie przeszkadzamy.
                if (IsRunning || IsSimulationMode || !_settings.UsesInteractiveBrokers)
                {
                    return;
                }

                AppLog.Info("Połączenie", $"próba ponownego połączenia nr {attempt + 1}");
                await StartAsync();
                if (IsRunning)
                {
                    Hint("Połączenie z brokerem przywrócone");
                    return;
                }

                var next = ReconnectDelays[Math.Min(attempt + 1, ReconnectDelays.Length - 1)];
                AppLog.Warning("Połączenie", $"próba nr {attempt + 1} nieudana ({ErrorTextOrDefault()}), kolejna za {next.TotalSeconds:0} s");
                ShowError($"{ErrorTextOrDefault()} – kolejna próba połączenia za {next.TotalSeconds:0} s");
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("Połączenie", "ponowne łączenie przerwane (działanie użytkownika)");
        }
        finally
        {
            if (ReferenceEquals(_reconnectCts, cts))
            {
                _reconnectCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>Przerywa automatyczne ponowne łączenie (użytkownik sam zatrzymał / uruchomił / zmienił ustawienia).</summary>
    private void CancelReconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectCts = null;
    }

    private string ErrorTextOrDefault()
    {
        var text = string.IsNullOrEmpty(_errorText) ? "Brak połączenia z brokerem" : _errorText;
        var suffix = text.IndexOf(" – kolejna próba połączenia", StringComparison.Ordinal);
        return suffix >= 0 ? text[..suffix] : text;
    }

    private void SubscribeConnectionLoss(IMarketDataFeed feed)
    {
        if (feed is IConnectionLossAware aware)
        {
            aware.ConnectionLost += OnFeedConnectionLost;
        }
    }

    private void UnsubscribeConnectionLoss(IMarketDataFeed feed)
    {
        if (feed is IConnectionLossAware aware)
        {
            aware.ConnectionLost -= OnFeedConnectionLost;
        }
    }
}
