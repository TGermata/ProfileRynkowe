using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MarketProfile.Core;

namespace MarketProfile.App;

/// <summary>
/// Okno dodawania/edycji poziomej linii cenowej: opis (i jego położenie), cena, kolor, grubość, styl, blokada.
/// </summary>
public partial class PriceLineWindow : Window
{
    /// <summary>Gotowe kolory do szybkiego wyboru.</summary>
    private static readonly string[] Palette =
    [
        "#FFD54F", "#FFA726", "#FF5252", "#E040FB", "#7C4DFF", "#448AFF",
        "#40C4FF", "#64FFDA", "#69F0AE", "#B2FF59", "#FFFFFF", "#9E9E9E",
    ];

    /// <summary>Nazwy stylów linii wyświetlane w liście.</summary>
    private static readonly (LineStyle Style, string Name)[] Styles =
    [
        (LineStyle.Solid, "Ciągła"),
        (LineStyle.Dashed, "Kreskowana"),
        (LineStyle.Dotted, "Kropkowana"),
    ];

    private readonly PriceLine _original;

    public PriceLineWindow(PriceLine line, decimal tickSize)
    {
        InitializeComponent();
        _original = line;
        TickSize = tickSize;

        // Przyciski z gotowymi kolorami – kliknięcie wpisuje kolor do pola tekstowego.
        foreach (var color in Palette)
        {
            var button = new Button
            {
                Width = 32,
                Height = 22,
                Margin = new Thickness(0, 0, 4, 4),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                ToolTip = color,
            };
            button.Click += (_, _) => ColorBox.Text = color;
            PalettePanel.Children.Add(button);
        }

        foreach (var (_, name) in Styles)
        {
            StyleBox.Items.Add(name);
        }

        // Wypełniamy pola wartościami edytowanej linii.
        PriceBox.Text = PriceFormat.Format(line.Price, tickSize); // miejsca po przecinku wg ticka instrumentu
        ColorBox.Text = line.Color;
        ThicknessSlider.Value = line.Thickness;
        StyleBox.SelectedIndex = Array.FindIndex(Styles, s => s.Style == line.Style);
        LabelBox.Text = line.Label;
        LabelLeftBox.IsChecked = line.LabelPosition == LabelPosition.Left;
        LabelRightBox.IsChecked = line.LabelPosition == LabelPosition.Right;
        LockedBox.IsChecked = line.IsLocked;

        // Kursor od razu w polu opisu (pierwsze pole okna).
        Loaded += (_, _) => LabelBox.Focus();
    }

    /// <summary>Wielkość ticka – cena linii jest do niej zaokrąglana.</summary>
    public decimal TickSize { get; }

    /// <summary>Linia po edycji (wypełniona po kliknięciu "Zapisz").</summary>
    public PriceLine? Result { get; private set; }

    /// <summary>Podgląd koloru na bieżąco podczas wpisywania.</summary>
    private void OnColorTextChanged(object sender, TextChangedEventArgs e) =>
        ColorPreview.Background = TryParseColor(ColorBox.Text, out var color) ? new SolidColorBrush(color) : null;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(PriceBox.Text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
            || price <= 0)
        {
            ErrorText.Text = "Podaj poprawną cenę, np. 6012.25";
            return;
        }

        if (!TryParseColor(ColorBox.Text, out _))
        {
            ErrorText.Text = "Podaj kolor w formacie #RRGGBB, np. #FF5252";
            return;
        }

        Result = _original with
        {
            Price = Math.Round(price / TickSize) * TickSize, // zaokrąglenie do pełnego ticka
            Color = ColorBox.Text.Trim().ToUpperInvariant(),
            Thickness = ThicknessSlider.Value,
            Style = Styles[Math.Max(0, StyleBox.SelectedIndex)].Style,
            Label = LabelBox.Text.Trim(),
            LabelPosition = LabelLeftBox.IsChecked == true ? LabelPosition.Left : LabelPosition.Right,
            IsLocked = LockedBox.IsChecked == true,
        };

        DialogResult = true;
    }

    /// <summary>Sprawdza, czy tekst jest kolorem w formacie #RRGGBB.</summary>
    private static bool TryParseColor(string text, out Color color)
    {
        color = default;
        text = text.Trim();

        if (text.Length != 7 || text[0] != '#' ||
            !int.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }
}
