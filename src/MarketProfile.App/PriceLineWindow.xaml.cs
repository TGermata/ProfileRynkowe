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

        if (line.Alert is { } alert)
        {
            ShowAlert(alert);
        }

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

        PriceAlert? alert = null;
        if (_original.Alert is { } original)
        {
            if (!int.TryParse(RearmTicksBox.Text.Trim(), out var rearmTicks) || rearmTicks < 0 || rearmTicks > 1000)
            {
                ErrorText.Text = "Ponowne uzbrojenie: podaj liczbę ticków 0–1000";
                return;
            }

            alert = original with
            {
                Repeat = SoundEveryBox.IsChecked == true ? AlertRepeat.EveryCross : AlertRepeat.Once,
                Direction = DirectionUpBox.IsChecked == true ? AlertDirection.Up
                    : DirectionDownBox.IsChecked == true ? AlertDirection.Down
                    : AlertDirection.Both,
                RearmTicks = rearmTicks,
                Sound = SelectedSound(),
                IsEnabled = AlertEnabledBox.IsChecked == true,
                IsTriggered = false, // po zapisie poziom jest uzbrojony od nowa
            };
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
            Alert = alert,
        };

        DialogResult = true;
    }

    // =====================================================================
    // Poziom alarmowy
    // =====================================================================

    /// <summary>Własny plik dźwięku (pełna ścieżka) – pozycja na końcu listy dźwięków.</summary>
    private string? _soundFile;

    private void ShowAlert(PriceAlert alert)
    {
        Title = "Poziom alarmowy";
        AlertPanel.Visibility = Visibility.Visible;
        LockedBox.Content = "🔒 Zablokuj poziom (nie można go przesunąć myszą)";
        StyleBox.IsEnabled = false; // poziom alarmowy ma własny wzór (kreska-kropka)
        StyleBox.ToolTip = "Poziom alarmowy zawsze ma wzór kreska-kropka – odróżnia się od zwykłych linii";

        SoundOnceBox.IsChecked = alert.Repeat == AlertRepeat.Once;
        SoundEveryBox.IsChecked = alert.Repeat == AlertRepeat.EveryCross;
        RearmTicksBox.Text = alert.RearmTicks.ToString(CultureInfo.InvariantCulture);
        DirectionBothBox.IsChecked = alert.Direction == AlertDirection.Both;
        DirectionUpBox.IsChecked = alert.Direction == AlertDirection.Up;
        DirectionDownBox.IsChecked = alert.Direction == AlertDirection.Down;
        AlertEnabledBox.IsChecked = alert.IsEnabled;

        if (!Infrastructure.AlertSoundPlayer.IsSystemSound(alert.Sound) && !string.IsNullOrWhiteSpace(alert.Sound))
        {
            _soundFile = alert.Sound;
        }

        FillSounds(alert.Sound);

        AlertStateText.Text = alert.IsTriggered
            ? $"Alarm zadziałał{(alert.LastTriggered is { } t ? $" ({t.ToLocalTime():dd.MM HH:mm:ss})" : string.Empty)} – zapis uzbroi go ponownie."
            : "Zapis (także po zmianie ceny) uzbraja alarm od nowa. Dodawanie i menu poziomu: prawy przycisk myszy na wykresie.";
    }

    private void FillSounds(string selected)
    {
        SoundBox.Items.Clear();
        foreach (var name in PriceAlert.SystemSounds)
        {
            SoundBox.Items.Add($"Windows: {name}");
        }

        if (_soundFile is not null)
        {
            SoundBox.Items.Add($"Plik: {System.IO.Path.GetFileName(_soundFile)}");
        }

        var index = PriceAlert.SystemSounds.ToList().FindIndex(n => string.Equals(n, selected, StringComparison.OrdinalIgnoreCase));
        SoundBox.SelectedIndex = index >= 0 ? index : _soundFile is not null ? SoundBox.Items.Count - 1 : 0;
    }

    /// <summary>Wybrany dźwięk: nazwa systemowa albo ścieżka pliku.</summary>
    private string SelectedSound() =>
        SoundBox.SelectedIndex >= 0 && SoundBox.SelectedIndex < PriceAlert.SystemSounds.Count
            ? PriceAlert.SystemSounds[SoundBox.SelectedIndex]
            : _soundFile ?? PriceAlert.SystemSounds[0];

    private void OnSoundFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Dźwięk alarmu",
            Filter = "Dźwięki (*.wav;*.mp3)|*.wav;*.mp3|Wszystkie pliki (*.*)|*.*",
            CheckFileExists = true,
        };

        if (_soundFile is not null)
        {
            dialog.InitialDirectory = System.IO.Path.GetDirectoryName(_soundFile);
        }

        if (dialog.ShowDialog(this) == true)
        {
            _soundFile = dialog.FileName;
            FillSounds(_soundFile);
        }
    }

    private void OnPlaySoundClick(object sender, RoutedEventArgs e)
    {
        if (!Infrastructure.AlertSoundPlayer.Play(SelectedSound()))
        {
            ErrorText.Text = "Nie można odtworzyć pliku – zagrał dźwięk systemowy.";
        }
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
