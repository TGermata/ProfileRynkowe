using MarketProfile.App.Controls;
using MarketProfile.App.Infrastructure;
using MarketProfile.App.Settings;

namespace MarketProfile.App.ViewModels;

/// <summary>Przełącznik jednej średniej SMA na wykresie TPO (menu „Wyświetlanie”).</summary>
public sealed class SmaToggle(string label, int index, Func<int, bool> get, Action<int, bool> set) : ObservableObject
{
    public string Label { get; } = label;

    public bool IsVisible
    {
        get => get(index);
        set
        {
            if (value != get(index))
            {
                set(index, value);
                OnPropertyChanged();
            }
        }
    }
}

/// <summary>
/// Menu „Wyświetlanie” nad wykresem TPO: szybkie włączanie / wyłączanie elementów wykresu. Każda zmiana jest
/// zapisywana w ustawieniach (te same wartości są w oknie Ustawień), bez restartu danych.
/// </summary>
public sealed partial class MainViewModel
{
    private IReadOnlyList<SmaToggle>? _smaToggles;

    // --- Profil ---

    public bool ShowInitialBalance
    {
        get => _settings.ShowInitialBalance;
        set => SaveAppearance(_settings with { ShowInitialBalance = value });
    }

    public bool ShowOpenArrow
    {
        get => _settings.Arrows.ShowOpen;
        set => SaveAppearance(_settings with { Arrows = _settings.Arrows with { ShowOpen = value } });
    }

    public bool ShowCloseArrow
    {
        get => _settings.Arrows.ShowClose;
        set => SaveAppearance(_settings with { Arrows = _settings.Arrows with { ShowClose = value } });
    }

    /// <summary>Profil wolumenowy bieżącego (budowanego) słupka TPO.</summary>
    public bool ShowPeriodVolume
    {
        get => _settings.PeriodVolume.Enabled;
        set => SaveAppearance(_settings with { PeriodVolume = _settings.PeriodVolume with { Enabled = value } });
    }

    // --- Opisy dnia ---

    /// <summary>Opisy analizy na profilach dziennych – dla wykresu.</summary>
    public ChartNotesStyle NotesStyle => _settings.Notes;

    public bool ShowHalfback
    {
        get => _settings.Notes.ShowHalfback;
        set => SaveNotes(_settings.Notes with { ShowHalfback = value });
    }

    public bool ShowSettle
    {
        get => _settings.Notes.ShowSettle;
        set => SaveNotes(_settings.Notes with { ShowSettle = value });
    }

    public bool ShowPullback
    {
        get => _settings.Notes.ShowPullback;
        set => SaveNotes(_settings.Notes with { ShowPullback = value });
    }

    public bool ShowNoteText
    {
        get => _settings.Notes.ShowNoteText;
        set => SaveNotes(_settings.Notes with { ShowNoteText = value });
    }

    // Poziomy nocy – trzy przyciski wyboru (RadioButton).
    public bool OvernightLevelsLastDay
    {
        get => _settings.Notes.OvernightLevels == OvernightLevelsMode.LastDay;
        set => SetOvernightLevels(value, OvernightLevelsMode.LastDay);
    }

    public bool OvernightLevelsAllDays
    {
        get => _settings.Notes.OvernightLevels == OvernightLevelsMode.AllDays;
        set => SetOvernightLevels(value, OvernightLevelsMode.AllDays);
    }

    public bool OvernightLevelsNone
    {
        get => _settings.Notes.OvernightLevels == OvernightLevelsMode.None;
        set => SetOvernightLevels(value, OvernightLevelsMode.None);
    }

    // --- Średnie SMA ---

    /// <summary>Średnie z wykresu dziennego na wykresie TPO (linie / pasy) – wszystkie naraz.</summary>
    public bool ShowTpoAverages
    {
        get => _settings.TpoAverages.Enabled;
        set
        {
            if (value != _settings.TpoAverages.Enabled)
            {
                SaveDisplaySettings(_settings with { TpoAverages = _settings.TpoAverages with { Enabled = value } });
                UpdateTpoAverages();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Przełączniki poszczególnych średnich (np. SMA20 D, SMA50 D).</summary>
    public IReadOnlyList<SmaToggle> TpoAverageToggles => _smaToggles ??=
    [
        .. _settings.CandleChart.MovingAverages.Select((average, index) => new SmaToggle(average.EffectiveTpoLabel, index,
            i => i < _settings.CandleChart.MovingAverages.Count && _settings.CandleChart.MovingAverages[i].ShowOnTpo,
            SetAverageOnTpo)),
    ];

    // --- Panel ---

    /// <summary>Czy menu „Wyświetlanie” jest zwinięte – zapamiętywane.</summary>
    public bool DisplayPanelCollapsed
    {
        get => _settings.DisplayPanelCollapsed;
        set
        {
            if (value != _settings.DisplayPanelCollapsed)
            {
                _settings = _settings with { DisplayPanelCollapsed = value };
                SettingsStore.Save(_settings);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Tabela „Parametry profili”: tylko profile dzienne – zapamiętywane.</summary>
    public bool ProfileStatsDayOnly
    {
        get => _settings.ProfileStatsDayOnly;
        set
        {
            if (value != _settings.ProfileStatsDayOnly)
            {
                _settings = _settings with { ProfileStatsDayOnly = value };
                SettingsStore.Save(_settings);
            }
        }
    }

    /// <summary>Zapisuje położenie menu „Wyświetlanie” (po przesunięciu myszą).</summary>
    public void SaveDisplayPanelPosition(double left, double top)
    {
        _settings = _settings with { DisplayPanelLeft = left, DisplayPanelTop = top };
        SettingsStore.Save(_settings);
    }

    /// <summary>Odświeża wszystkie pola menu (np. po zmianie w oknie Ustawień).</summary>
    private void NotifyDisplayOptions()
    {
        _smaToggles = null;
        foreach (var name in new[]
                 {
                     nameof(ShowInitialBalance), nameof(ShowOpenArrow), nameof(ShowCloseArrow), nameof(ShowPeriodVolume),
                     nameof(NotesStyle), nameof(ShowHalfback), nameof(ShowSettle), nameof(ShowPullback), nameof(ShowNoteText),
                     nameof(OvernightLevelsLastDay), nameof(OvernightLevelsAllDays), nameof(OvernightLevelsNone),
                     nameof(ShowTpoAverages), nameof(TpoAverageToggles),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Zapis ustawień wyglądu profili (IB, strzałki, wolumen słupka) – nowy styl wykresu.</summary>
    private void SaveAppearance(AppSettings newSettings)
    {
        if (newSettings == _settings)
        {
            return;
        }

        SaveDisplaySettings(newSettings);
        Appearance = ChartStyle.Create(newSettings);
        NotifyDisplayOptions();
    }

    private void SaveNotes(ChartNotesStyle notes)
    {
        if (notes == _settings.Notes)
        {
            return;
        }

        _settings = _settings with { Notes = notes };
        SettingsStore.Save(_settings);
        NotifyDisplayOptions();
    }

    private void SetOvernightLevels(bool selected, OvernightLevelsMode mode)
    {
        if (selected)
        {
            SaveNotes(_settings.Notes with { OvernightLevels = mode });
        }
    }

    private void SetAverageOnTpo(int index, bool visible)
    {
        var averages = _settings.CandleChart.MovingAverages.ToList();
        if (index >= averages.Count)
        {
            return;
        }

        averages[index] = averages[index] with { ShowOnTpo = visible };
        SaveDisplaySettings(_settings with { CandleChart = _settings.CandleChart with { MovingAverages = averages } });
        UpdateTpoAverages();
    }
}
