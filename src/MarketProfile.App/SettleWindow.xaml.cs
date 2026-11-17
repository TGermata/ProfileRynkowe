using System.Globalization;
using System.Windows;
using MarketProfile.App.ViewModels;
using MarketProfile.Core;

namespace MarketProfile.App;

/// <summary>Ręczny wpis ceny rozliczeniowej (SETTLE) sesji dziennej – gdy nie udało się jej pobrać ze strony CME.</summary>
public partial class SettleWindow : Window
{
    private SettleWindow(DateOnly tradingDate, Settlement? current, decimal suggested, decimal tickSize)
    {
        InitializeComponent();
        HeaderText.Text = $"Sesja dzienna {tradingDate.ToString("dddd, dd.MM.yyyy", CultureInfo.CurrentCulture)}";
        CurrentText.Text = current is null
            ? "Bez wpisu ręcznego za SETTLE przyjmowana jest cena zamknięcia (Close) sesji – podana poniżej."
            : $"Obecny wpis ręczny: {PriceFormat.Format(current.Price, tickSize)}.";
        PriceBox.Text = PriceFormat.Format(current?.Price ?? suggested, tickSize);
        Loaded += (_, _) =>
        {
            PriceBox.Focus();
            PriceBox.SelectAll();
        };
    }

    /// <summary>Wpisana cena (po „Zapisz”).</summary>
    public decimal? Result { get; private set; }

    /// <summary>Otwiera okno i zapisuje wpis ręczny; true = zapisano.</summary>
    public static bool Edit(Window owner, MainViewModel viewModel, DateOnly tradingDate, decimal suggested)
    {
        var tick = viewModel.Settings.Instrument.TickSize;
        var dialog = new SettleWindow(tradingDate, viewModel.GetSettlement(tradingDate), suggested, tick) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } price)
        {
            return false;
        }

        viewModel.SetManualSettlement(tradingDate, price);
        return true;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var text = PriceBox.Text.Trim().Replace(',', '.');
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var price) || price <= 0)
        {
            ErrorText.Text = "Podaj cenę jako liczbę, np. 6881.25.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Result = price;
        DialogResult = true;
    }
}
