using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MarketProfile.App.Infrastructure;

namespace MarketProfile.App.Controls;

/// <summary>
/// Pole wyboru koloru. Kolor jest przechowywany jako tekst "#RRGGBB" (tak jak w pliku ustawień),
/// więc można go wpisać ręcznie albo kliknąć w palecie gotowych kolorów.
/// </summary>
public partial class ColorPicker : UserControl
{
    /// <summary>Wybrany kolor w formacie "#RRGGBB" (wiązanie dwukierunkowe).</summary>
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color),
        typeof(string),
        typeof(ColorPicker),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((ColorPicker)d).OnColorChanged((string)e.NewValue)));

    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public ColorPicker()
    {
        InitializeComponent();

        // Przyciski palety – kliknięcie ustawia kolor i zamyka paletę.
        foreach (var color in ColorUtil.Palette)
        {
            ColorUtil.TryParse(color, out var c);
            var button = new Button
            {
                Width = 22,
                Height = 18,
                Margin = new Thickness(2),
                Background = new SolidColorBrush(c),
                ToolTip = color,
            };
            button.Click += (_, _) =>
            {
                Color = color;
                PaletteButton.IsChecked = false;
            };
            PalettePanel.Children.Add(button);
        }
    }

    /// <summary>Zmiana koloru z zewnątrz (np. przy wczytaniu ustawień) – aktualizujemy pole tekstowe i podgląd.</summary>
    private void OnColorChanged(string color)
    {
        if (ColorText.Text != color)
        {
            ColorText.Text = color;
        }

        UpdateSwatch();
    }

    /// <summary>Użytkownik wpisuje kolor – przekazujemy go dalej i odświeżamy podgląd.</summary>
    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        Color = ColorText.Text;
        UpdateSwatch();
    }

    /// <summary>Podgląd koloru; przy błędnym wpisie podgląd jest pusty.</summary>
    private void UpdateSwatch() =>
        Swatch.Background = ColorUtil.TryParse(ColorText.Text, out var c) ? new SolidColorBrush(c) : null;
}
