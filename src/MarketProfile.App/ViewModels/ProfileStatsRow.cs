using System.Globalization;
using MarketProfile.Core;

namespace MarketProfile.App.ViewModels;

/// <summary>Wiersz tabeli „Parametry profili” – parametry jednego profilu w postaci tekstów do wyświetlenia.</summary>
public sealed class ProfileStatsRow
{
    private readonly decimal _tick;

    /// <param name="settlement">
    /// Aktualny wpis ręczny SETTLE dnia (null = brak – SETTLE to Close sesji). Ważniejszy niż wartość zapisana
    /// w parametrach, bo wpis mógł się zmienić po ich zapisaniu.
    /// </param>
    public ProfileStatsRow(ProfileStats stats, decimal tickSize, Settlement? settlement = null)
    {
        Stats = stats is { IsDaySession: true, IsComplete: true }
            ? settlement is not null
                ? stats with { Settle = settlement.Price, SettleSource = settlement.Source }
                : stats with { Settle = stats.Close, SettleSource = Settlement.SourceClose }
            : stats;
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
        Core.ValueMigration.OverlappingHigher => "pokrywająca wyżej",
        Core.ValueMigration.OverlappingLower => "pokrywająca niżej",
        Core.ValueMigration.Lower => "niżej",
        Core.ValueMigration.Inside => "wewnątrz",
        Core.ValueMigration.Outside => "pokrywająca, obejmuje",
        Core.ValueMigration.TrendDay => "dzień trendowy",
        _ => "–",
    };

    /// <summary>Krótki opis relacji wartości (do opisu pod profilem): np. „VA ↑ wyżej”.</summary>
    public static string ValueMigrationShort(ValueMigration migration) => migration switch
    {
        Core.ValueMigration.Higher => "VA ↑ wyżej",
        Core.ValueMigration.OverlappingHigher => "VA ↗ pokr. wyżej",
        Core.ValueMigration.OverlappingLower => "VA ↘ pokr. niżej",
        Core.ValueMigration.Lower => "VA ↓ niżej",
        Core.ValueMigration.Inside => "VA ↔ wewnątrz",
        Core.ValueMigration.Outside => "VA ⇕ obejmuje",
        Core.ValueMigration.TrendDay => "Trend – VA pominięta",
        _ => string.Empty,
    };

    public string Halfback => P(Stats.Halfback);

    public string Settle => Stats.Settle is { } settle
        ? Stats.SettleSource == Settlement.SourceManual ? $"{P(settle)} ✎" : P(settle)
        : "–";

    public string SettleToolTip => Stats.Settle is null
        ? "Sesja w toku albo sesja nocna – bez SETTLE"
        : Stats.SettleSource == Settlement.SourceManual
            ? "Wpis ręczny (prawy klik – zmień albo usuń, wtedy SETTLE = Close)"
            : "Close sesji (prawy klik – wpisz SETTLE ręcznie)";

    /// <summary>Interwałowanie, np. „↑ trend B–K (10)” (dzień trendowy) albo „↓ B–E (4)”.</summary>
    public string Trend => Stats.TrendDirection == 0 || Stats.IntervalFrom is null
        ? "–"
        : $"{(Stats.TrendDirection > 0 ? "↑" : "↓")} {(Stats.IsTrendDay ? "trend " : string.Empty)}" +
          $"{Stats.IntervalFrom}–{Stats.IntervalTo} ({Stats.IntervalPeriods})";

    /// <summary>PULLBACK LOW / HIGH dnia trendowego, np. „PBL 6850.25 (M)”.</summary>
    public string Pullback => Stats.PullbackLow is { } low
        ? $"PBL {P(low)} ({Stats.PullbackLetter})"
        : Stats.PullbackHigh is { } high
            ? $"PBH {P(high)} ({Stats.PullbackLetter})"
            : "–";

    public string OvernightHigh => P(Stats.OvernightHigh);
    public string OvernightLow => P(Stats.OvernightLow);
    public string OvernightPoc => P(Stats.OvernightPoc);

    public string OpenVsOvernight => Stats.OpenVsOvernight switch
    {
        OvernightLocation.AboveHigh => "nad ONH",
        OvernightLocation.InRange => "w zakresie nocy",
        OvernightLocation.BelowLow => "pod ONL",
        _ => "–",
    };

    /// <summary>Inwentarz na otwarciu, np. „długi +3.25 → wyrównany (C)”, „krótki −2.00 → nie”, „… → jeszcze nie”.</summary>
    public string Inventory
    {
        get
        {
            if (Stats.InventorySize is not { } size)
            {
                return "–";
            }

            var kind = Stats.OpeningInventory switch
            {
                OpeningInventory.Long => $"długi +{P(size)}",
                OpeningInventory.Short => $"krótki −{P(-size)}",
                _ => "neutralny (Open = SETTLE)",
            };
            return Stats.InventoryCorrected switch
            {
                true => $"{kind} → wyrównany ({Stats.InventoryCorrectedLetter})",
                false => $"{kind} → {(Stats.IsComplete ? "nie" : "jeszcze nie")}",
                _ => kind,
            };
        }
    }

    /// <summary>Luka względem zakresu poprzedniego dnia, np. „↑ +6.25 (nad High)”; brak luki – „–”.</summary>
    public string Gap => Stats.Gap is { } gap
        ? gap > 0 ? $"↑ +{P(gap)} (nad High)" : $"↓ −{P(-gap)} (pod Low)"
        : "–";

    public string OpenVsPreviousHalfback => Stats.PreviousHalfback is null
        ? "–"
        : Stats.OpenVsPreviousHalfback switch
        {
            > 0 => $"nad ({P(Stats.PreviousHalfback)})",
            < 0 => $"pod ({P(Stats.PreviousHalfback)})",
            _ => $"na poziomie ({P(Stats.PreviousHalfback)})",
        };

    public string ValueAreaOverlap => Stats.ValueAreaOverlap is { } o ? o.ToString("P0", CultureInfo.CurrentCulture) : "–";

    public string NakedPocs => Stats.NakedPocs.Count > 0 ? string.Join("; ", Stats.NakedPocs.Select(P)) : "–";

    public string NakedVolumePocs => Stats.NakedVolumePocs.Count > 0 ? string.Join("; ", Stats.NakedVolumePocs.Select(P)) : "–";

    public string Volume => Stats.Volume.ToString("N0", CultureInfo.CurrentCulture);
}
