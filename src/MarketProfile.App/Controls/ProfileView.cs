using MarketProfile.Core;

namespace MarketProfile.App.Controls;

/// <summary>
/// Profil przygotowany do wyświetlenia: dane (migawka z modelu) + sposób prezentacji.
/// Sposób prezentacji to sprawa widoku, dlatego nie jest częścią migawki w warstwie Core.
/// </summary>
/// <param name="Snapshot">Dane profilu.</param>
/// <param name="IsVisible">Czy profil jest widoczny (profile ukrytych sesji nie są rysowane).</param>
/// <param name="IsExpanded">
/// Tryb Split/Expanded – każdy okres TPO w osobnej kolumnie (zamiast liter zsuniętych do lewej).
/// </param>
/// <param name="ShowVolume">Czy rysować profil wolumenowy (histogram) obok liter.</param>
/// <param name="ShowPeriodVolume">Czy rysować profil wolumenowy aktualnie budowanego słupka TPO (tylko bieżący profil).</param>
public sealed record ProfileView(ProfileSnapshot Snapshot, bool IsVisible, bool IsExpanded, bool ShowVolume, bool ShowPeriodVolume = false);
