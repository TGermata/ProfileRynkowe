using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MarketProfile.App.Controls;

/// <summary>
/// Pływający panel narzędzi szybkiego dostępu nad wykresem (umieszczany na <see cref="Canvas"/>):
///  - przeciąganie za nagłówek przesuwa panel (nie wychodzi poza obszar rodzica),
///  - strzałka w nagłówku zwija panel do samego nagłówka (<see cref="IsCollapsed"/>),
///  - narzędzia (przyciski) podaje się w <see cref="Tools"/> – nowe narzędzia dodaje się w XAML okna.
/// Po zakończeniu przesuwania zgłasza nowe położenie (<see cref="PositionCommitted"/>) – do zapamiętania w ustawieniach.
/// </summary>
public partial class FloatingToolPanel : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(FloatingToolPanel), new PropertyMetadata("⠿ Narzędzia"));

    public static readonly DependencyProperty ToolsProperty = DependencyProperty.Register(
        nameof(Tools), typeof(object), typeof(FloatingToolPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty IsCollapsedProperty = DependencyProperty.Register(
        nameof(IsCollapsed), typeof(bool), typeof(FloatingToolPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((FloatingToolPanel)d).ToolsHost.Visibility = (bool)e.NewValue ? Visibility.Collapsed : Visibility.Visible));

    private Point? _dragOffset;   // miejsce chwycenia względem lewego górnego rogu panelu

    public FloatingToolPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (Parent is FrameworkElement parent)
            {
                parent.SizeChanged += (_, _) => ClampToParent(); // np. zmniejszenie okna – panel zostaje widoczny
            }

            ClampToParent();
        };

        // Rozwinięcie panelu (większy rozmiar) – nadal w całości na wykresie.
        SizeChanged += (_, _) => ClampToParent();
    }

    /// <summary>Tekst nagłówka.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Narzędzia (zawartość panelu).</summary>
    public object? Tools
    {
        get => GetValue(ToolsProperty);
        set => SetValue(ToolsProperty, value);
    }

    /// <summary>Czy panel jest zwinięty do nagłówka.</summary>
    public bool IsCollapsed
    {
        get => (bool)GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    /// <summary>Panel przesunięto – nowe położenie (lewo, góra) względem rodzica.</summary>
    public event Action<double, double>? PositionCommitted;

    /// <summary>Ustawia położenie (np. zapamiętane w ustawieniach) – w granicach rodzica.</summary>
    public void MoveTo(double left, double top)
    {
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        ClampToParent();
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInToggle(source))
        {
            return; // klik na strzałce zwijania
        }

        _dragOffset = e.GetPosition(this);
        Header.CaptureMouse();
        e.Handled = true; // nie przekazujemy do wykresu pod spodem
    }

    private void OnHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragOffset is not { } offset || Parent is not IInputElement parent)
        {
            return;
        }

        var position = e.GetPosition(parent);
        Canvas.SetLeft(this, position.X - offset.X);
        Canvas.SetTop(this, position.Y - offset.Y);
        ClampToParent();
    }

    private void OnHeaderMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragOffset is null)
        {
            return;
        }

        Header.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnHeaderLostCapture(object sender, MouseEventArgs e)
    {
        if (_dragOffset is null)
        {
            return;
        }

        _dragOffset = null;
        PositionCommitted?.Invoke(Left, Top);
    }

    private double Left => double.IsNaN(Canvas.GetLeft(this)) ? 0 : Canvas.GetLeft(this);

    private double Top => double.IsNaN(Canvas.GetTop(this)) ? 0 : Canvas.GetTop(this);

    /// <summary>Panel w całości w obszarze rodzica (wykresu).</summary>
    private void ClampToParent()
    {
        if (Parent is not FrameworkElement parent || parent.ActualWidth <= 0 || parent.ActualHeight <= 0)
        {
            return;
        }

        Canvas.SetLeft(this, Math.Clamp(Left, 0, Math.Max(0, parent.ActualWidth - ActualWidth)));
        Canvas.SetTop(this, Math.Clamp(Top, 0, Math.Max(0, parent.ActualHeight - ActualHeight)));
    }

    private bool IsInToggle(DependencyObject source)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, Header);
             current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.ToggleButton)
            {
                return true;
            }
        }

        return false;
    }
}
