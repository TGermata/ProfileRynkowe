using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MarketProfile.App.Settings;

namespace MarketProfile.App;

/// <summary>
/// Zakładki w osobnych oknach – praca na kilku monitorach.
///  - odpięcie: TYLKO przeciągnięcie nagłówka zakładki z paska zakładek – w dół na wykres albo na inny monitor
///    (okno powstaje w miejscu upuszczenia); kliknięcia i przeciąganie na samym wykresie nic nie odpinają,
///  - powrót: zamknięcie okna zakładki albo „⇱ Wróć do okna głównego”,
///  - układ okien (także okna głównego) jest zapamiętywany i odtwarzany przy kolejnym uruchomieniu.
/// Do okna trafia ta sama zawartość, która była w zakładce, więc wykresy i ich obsługa działają bez zmian.
/// </summary>
public partial class MainWindow
{
    private readonly List<DetachedTabWindow> _detachedWindows = [];
    private TabItem? _dragTab;      // nagłówek zakładki, który użytkownik zaczął przeciągać
    private Point _dragStart;       // miejsce wciśnięcia przycisku (względem okna głównego)
    private bool _dragging;         // przeciąganie ruszyło (przesunięcie większe niż próg systemowy)

    private IEnumerable<TabItem> TabItems => Tabs.Items.OfType<TabItem>();

    private void InitializeDetachableTabs()
    {
        foreach (var tab in TabItems)
        {
            // Uwaga: zdarzenia myszy z ZAWARTOŚCI zakładki (wykres, tabela) też przechodzą przez TabItem –
            // dlatego przeciąganie zaczyna się tylko, gdy wciśnięto przycisk na samym nagłówku (IsInHeader).
            tab.PreviewMouseLeftButtonDown += OnTabHeaderMouseDown;
            tab.PreviewMouseMove += OnTabHeaderMouseMove;
            tab.PreviewMouseLeftButtonUp += OnTabHeaderMouseUp;
            tab.LostMouseCapture += (_, _) => _dragging = false;
        }
    }

    private void OnTabHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _dragTab = sender is TabItem tab && IsInHeader(tab, e.OriginalSource) ? tab : null;
        _dragStart = e.GetPosition(this);
    }

    /// <summary>
    /// Czy element (źródło zdarzenia myszy) leży w nagłówku zakładki. Zawartość zakładki jest w drzewie wizualnym
    /// TabControl (nie TabItem), więc dla kliknięć na wykresie czy w tabeli zwraca false.
    /// </summary>
    private static bool IsInHeader(TabItem tab, object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (ReferenceEquals(current, tab))
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private void OnTabHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab is null || !ReferenceEquals(sender, _dragTab) || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var offset = e.GetPosition(this) - _dragStart;
        if (!_dragging && (Math.Abs(offset.X) > SystemParameters.MinimumHorizontalDragDistance ||
                           Math.Abs(offset.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            // Przechwycenie myszy – zwolnienie przycisku dotrze do nas także poza oknem (np. na drugim monitorze).
            _dragging = _dragTab.CaptureMouse();
        }

        if (_dragging)
        {
            // Kursor „przenoszenie” – po zwolnieniu przycisku w tym miejscu zakładka trafi do osobnego okna.
            Mouse.OverrideCursor = IsTearOff(_dragTab, e.GetPosition(this)) ? Cursors.SizeAll : null;
        }
    }

    private void OnTabHeaderMouseUp(object sender, MouseButtonEventArgs e)
    {
        var tab = _dragTab;
        var wasDragging = _dragging;
        _dragTab = null;
        _dragging = false;
        Mouse.OverrideCursor = null;
        if (tab is null || !wasDragging)
        {
            return; // zwykłe kliknięcie – wybór zakładki
        }

        tab.ReleaseMouseCapture();
        var position = e.GetPosition(this);
        if (!IsTearOff(tab, position))
        {
            return;
        }

        // Okno zakładki w miejscu upuszczenia: lewy górny róg trochę nad i na lewo od kursora.
        e.Handled = true;
        var screen = ToScreenDip(position);
        DetachTab(tab, new Rect(screen.X - 60, screen.Y - 15, DefaultDetachedWidth, DefaultDetachedHeight), maximized: false);
    }

    /// <summary>
    /// Czy nagłówek „oderwano” od paska zakładek: upuszczony poza oknem głównym (np. na drugim monitorze) albo wyraźnie
    /// nad / pod paskiem zakładek – jak w przeglądarce; działa także przy zmaksymalizowanym oknie na jednym monitorze.
    /// </summary>
    private bool IsTearOff(TabItem tab, Point position)
    {
        if (position.X < 0 || position.Y < 0 || position.X > ActualWidth || position.Y > ActualHeight)
        {
            return true;
        }

        var header = tab.TransformToAncestor(this).TransformBounds(new Rect(tab.RenderSize));
        return position.Y < header.Top - TearOffDistance || position.Y > header.Bottom + TearOffDistance;
    }

    /// <summary>O ile trzeba odsunąć nagłówek w pionie od paska zakładek, żeby się oderwał.</summary>
    private const double TearOffDistance = 50;

    /// <summary>Punkt względem okna → współrzędne pulpitu w jednostkach WPF (jak <see cref="Window.Left"/>).</summary>
    private Point ToScreenDip(Point position)
    {
        var pixels = PointToScreen(position);
        return PresentationSource.FromVisual(this)?.CompositionTarget is { } target
            ? target.TransformFromDevice.Transform(pixels)
            : pixels;
    }

    /// <summary>Lewa krawędź okna głównego (dla zmaksymalizowanego <see cref="Window.Left"/> to położenie sprzed maksymalizacji).</summary>
    private double ActualLeft => WindowState == WindowState.Maximized ? ToScreenDip(new Point(0, 0)).X : Left;

    private double ActualTop => WindowState == WindowState.Maximized ? ToScreenDip(new Point(0, 0)).Y : Top;

    private double DefaultDetachedWidth => Math.Max(600, Math.Min(1200, ActualWidth * 0.7));

    private double DefaultDetachedHeight => Math.Max(400, Math.Min(800, ActualHeight * 0.7));

    /// <summary>
    /// Przenosi zakładkę do osobnego okna. <paramref name="bounds"/> – położenie okna (null = obok okna głównego).
    /// </summary>
    private void DetachTab(TabItem tab, Rect? bounds, bool maximized)
    {
        if (tab.Content is not UIElement content || tab.Tag is not string key)
        {
            return; // już w osobnym oknie
        }

        var index = Tabs.Items.IndexOf(tab);
        tab.Content = null; // element może mieć tylko jednego rodzica
        tab.Visibility = Visibility.Collapsed;
        if (ReferenceEquals(Tabs.SelectedItem, tab))
        {
            Tabs.SelectedItem = TabItems.FirstOrDefault(t => t.Visibility == Visibility.Visible);
        }

        var window = new DetachedTabWindow(key, tab.Header?.ToString() ?? key, content, _viewModel)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
        };

        // Domyślnie poniżej paska narzędzi i nagłówków zakładek okna głównego – żeby ich nie zasłaniać.
        var rect = bounds ?? new Rect(ActualLeft + 80 + 30 * _detachedWindows.Count, ActualTop + 140 + 30 * _detachedWindows.Count,
            DefaultDetachedWidth, DefaultDetachedHeight);
        window.Left = rect.Left;
        window.Top = rect.Top;
        window.Width = rect.Width;
        window.Height = rect.Height;
        if (maximized)
        {
            // Maksymalizacja dopiero po utworzeniu okna – na monitorze, na którym okno leży.
            window.SourceInitialized += (_, _) => window.WindowState = WindowState.Maximized;
        }

        window.ContentReturned += (w, returned) => AttachTab(w, returned);
        _detachedWindows.Add(window);
        _viewModel.SetTabDetached(index, detached: true);
        UpdateAllTabsDetachedText();
        window.Show();
    }

    /// <summary>Okno zakładki zamknięte – zawartość wraca do zakładki w oknie głównym.</summary>
    private void AttachTab(DetachedTabWindow window, UIElement content)
    {
        _detachedWindows.Remove(window);
        if (TabItems.FirstOrDefault(t => Equals(t.Tag, window.Key)) is not { } tab)
        {
            return;
        }

        tab.Content = content;
        tab.Visibility = Visibility.Visible;
        Tabs.SelectedItem = tab;
        _viewModel.SetTabDetached(Tabs.Items.IndexOf(tab), detached: false);
        UpdateAllTabsDetachedText();
    }

    private void UpdateAllTabsDetachedText()
    {
        var any = TabItems.Any(t => t.Visibility == Visibility.Visible);
        Tabs.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        AllTabsDetachedText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    // =====================================================================
    // Układ okien – zapis przy zamykaniu, odtworzenie przy starcie
    // =====================================================================

    private void SaveWindowLayout()
    {
        var main = LayoutOf(this, "main");
        var tabs = _detachedWindows.Select(w => LayoutOf(w, w.Key)).ToList();
        _viewModel.SaveWindowLayout(main, tabs);
    }

    /// <summary>Położenie okna; dla zmaksymalizowanego / zminimalizowanego – położenie „normalne” (na tym samym monitorze).</summary>
    private static WindowLayout LayoutOf(Window window, string key)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        return new WindowLayout(key, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            window.WindowState == WindowState.Maximized);
    }

    /// <summary>Okno główne na miejscu z ostatniego uruchomienia (domyślnie – zmaksymalizowane na ekranie głównym).</summary>
    private void RestoreMainWindowLayout()
    {
        if (_viewModel.Settings.MainWindowLayout is not { } layout || ToVisibleRect(layout) is not { } rect)
        {
            return;
        }

        WindowState = WindowState.Normal;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;
        if (layout.Maximized)
        {
            SourceInitialized += (_, _) => WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Zakładki, które przy ostatnim zamknięciu były w osobnych oknach – na tych samych miejscach.</summary>
    private void RestoreDetachedTabs()
    {
        foreach (var layout in _viewModel.Settings.DetachedTabs)
        {
            if (TabItems.FirstOrDefault(t => Equals(t.Tag, layout.Key)) is { } tab)
            {
                DetachTab(tab, ToVisibleRect(layout), layout.Maximized); // monitor odłączony – okno obok okna głównego
            }
        }

        Activate(); // okno główne na wierzchu (sterowanie)
    }

    /// <summary>
    /// Zapisane położenie, jeśli okno będzie widoczne na którymś z obecnych monitorów (co najmniej 100×50 jednostek
    /// w obrębie pulpitu); null – np. monitor został odłączony.
    /// </summary>
    private static Rect? ToVisibleRect(WindowLayout layout)
    {
        if (layout.Width < 200 || layout.Height < 150 || double.IsNaN(layout.Left) || double.IsNaN(layout.Top))
        {
            return null;
        }

        var rect = new Rect(layout.Left, layout.Top, layout.Width, layout.Height);
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var visible = Rect.Intersect(rect, desktop);
        return !visible.IsEmpty && visible.Width >= 100 && visible.Height >= 50 ? rect : null;
    }
}
