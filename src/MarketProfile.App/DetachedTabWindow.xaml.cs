using System.Windows;

namespace MarketProfile.App;

/// <summary>
/// Osobne okno z zawartością jednej zakładki okna głównego – do pracy na kilku monitorach.
/// Zawartość to ten sam obiekt, który był w zakładce (te same zdarzenia i dane), więc wszystko działa jak w oknie głównym.
/// Zamknięcie okna (albo „Wróć do okna głównego”) oddaje zawartość z powrotem – <see cref="ContentReturned"/>.
/// </summary>
public partial class DetachedTabWindow : Window
{
    private bool _closingApplication;

    /// <param name="key">Klucz zakładki ("tpo", "candles", "volume").</param>
    /// <param name="title">Nazwa zakładki.</param>
    /// <param name="content">Zawartość zabrana z zakładki.</param>
    /// <param name="dataContext">Model widoku okna głównego.</param>
    public DetachedTabWindow(string key, string title, UIElement content, object dataContext)
    {
        InitializeComponent();
        Key = key;
        Title = $"Market Profile – {title}";
        Header.Text = title;
        DataContext = dataContext;
        Host.Content = content;
    }

    /// <summary>Klucz zakładki ("tpo", "candles", "volume").</summary>
    public string Key { get; }

    /// <summary>Okno zamknięte przez użytkownika – zawartość (argument) wraca do zakładki w oknie głównym.</summary>
    public event Action<DetachedTabWindow, UIElement>? ContentReturned;

    /// <summary>Zamknięcie razem z aplikacją – bez oddawania zawartości do okna głównego.</summary>
    public void CloseWithApplication()
    {
        _closingApplication = true;
        Close();
    }

    private void OnReturnClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_closingApplication || Host.Content is not UIElement content)
        {
            return;
        }

        Host.Content = null; // element może mieć tylko jednego rodzica
        ContentReturned?.Invoke(this, content);
    }
}
