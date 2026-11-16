using System.Globalization;
using MarketProfile.Core;

namespace MarketProfile.App.ViewModels;

/// <summary>Wiersz tabeli „Parametry profili” – parametry jednego profilu w postaci tekstów do wyświetlenia.</summary>
public sealed class ProfileStatsRow
{
    private readonly decimal _tick;

    public ProfileStatsRow(ProfileStats stats, decimal tickSize)
    {
        Stats = stats;
        _tick = tickSize;
    }

    /// <summary>Parametry (liczby) – do nawigacji i eksportu.</summary>
    public ProfileStats Stats { get; }

    private string P(decimal value) => PriceFormat.Format(value, _tick);

    private string P(decimal? value) => value is { } v ? P(v) : "–";

    public string Date => Stats.TradingDate.ToString("yyyy-MM-dd ddd", CultureInfo.CurrentCulture);
    public string Session => Stats.Session;
    public string State => Stats.IsComplete ? "✓" : $"w toku ({Stats.LastLetter})";
    public string Shape => Stats.ShapeCode;
    public string Open => P(Stats.Open);
    public string High => P(Stats.High);
    public string Low => P(Stats.Low);
    public string Close => P(Stats.Close);
    public string Range => P(Stats.Range);
    public string Poc => P(Stats.Poc);
    public string VolumePoc => P(Stats.VolumePoc);
    public string ValueAreaHigh => P(Stats.ValueAreaHigh);
    public string ValueAreaLow => P(Stats.ValueAreaLow);
    public string PocPosition => Stats.PocPosition.ToString("0.00", CultureInfo.CurrentCulture);
    public string IbHigh => P(Stats.IbHigh);
    public string IbLow => P(Stats.IbLow);

    public string IbExtension => Stats.RangeExtension switch
    {
        RangeExtension.Up => $"↑ +{P(Stats.IbExtensionUp)} ({Stats.IbExtensionUpLetter})",
        RangeExtension.Down => $"↓ −{P(Stats.IbExtensionDown)} ({Stats.IbExtensionDownLetter})",
        RangeExtension.Both => $"↑ +{P(Stats.IbExtensionUp)} ({Stats.IbExtensionUpLetter})  ↓ −{P(Stats.IbExtensionDown)} ({Stats.IbExtensionDownLetter})",
        _ => "–",
    };

    public string TpoBalance => $"{Stats.TpoAbovePoc} / {Stats.TpoBelowPoc}";

    public string Tails
    {
        get
        {
            var parts = new List<string>();
            if (Stats.ExcessHigh > 0)
            {
                parts.Add($"excess ↑ {P(Stats.ExcessHigh * Stats.RowHeight)}");
            }

            if (Stats.PoorHigh)
            {
                parts.Add("poor high");
            }

            if (Stats.ExcessLow > 0)
            {
                parts.Add($"excess ↓ {P(Stats.ExcessLow * Stats.RowHeight)}");
            }

            if (Stats.PoorLow)
            {
                parts.Add("poor low");
            }

            return parts.Count > 0 ? string.Join(", ", parts) : "–";
        }
    }

    public string SinglePrints => Stats.SinglePrints.Count > 0
        ? string.Join("; ", Stats.SinglePrints.Select(r => $"{P(r.Low)}–{P(r.High)}"))
        : "–";

    public string OpenLocation => Stats.OpenLocation switch
    {
        Core.OpenLocation.AboveRange => "nad zakresem",
        Core.OpenLocation.AboveValue => "nad wartością",
        Core.OpenLocation.InValue => "w wartości",
        Core.OpenLocation.BelowValue => "pod wartością",
        Core.OpenLocation.BelowRange => "pod zakresem",
        _ => "–",
    };

    public string OpenType
    {
        get
        {
            var arrow = Stats.OpenDirection > 0 ? " ↑" : Stats.OpenDirection < 0 ? " ↓" : string.Empty;
            return Stats.OpenType switch
            {
                Core.OpenType.OpenDrive => "Open-Drive" + arrow,
                Core.OpenType.OpenTestDrive => "Open-Test-Drive" + arrow,
                Core.OpenType.OpenRejectionReverse => "Open-Rejection-Reverse" + arrow,
                Core.OpenType.OpenAuction => "Open-Auction",
                _ => "–",
            };
        }
    }

    public string ValueMigration => Stats.ValueMigration switch
    {
        Core.ValueMigration.Higher => "wyżej",
        Core.ValueMigration.OverlappingHigher => "nakładająca, wyżej",
        Core.ValueMigration.OverlappingLower => "nakładająca, niżej",
        Core.ValueMigration.Lower => "niżej",
        Core.ValueMigration.Inside => "wewnątrz",
        Core.ValueMigration.Outside => "obejmuje poprzednią",
        _ => "–",
    };

    public string ValueAreaOverlap => Stats.ValueAreaOverlap is { } o ? o.ToString("P0", CultureInfo.CurrentCulture) : "–";

    public string NakedPocs => Stats.NakedPocs.Count > 0 ? string.Join("; ", Stats.NakedPocs.Select(P)) : "–";

    public string NakedVolumePocs => Stats.NakedVolumePocs.Count > 0 ? string.Join("; ", Stats.NakedVolumePocs.Select(P)) : "–";

    public string Volume => Stats.Volume.ToString("N0", CultureInfo.CurrentCulture);
}
