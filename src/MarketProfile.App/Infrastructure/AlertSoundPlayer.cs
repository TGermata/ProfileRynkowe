using System.IO;
using System.Media;
using System.Windows.Media;
using MarketProfile.Core;

namespace MarketProfile.App.Infrastructure;

/// <summary>
/// Odtwarza dźwięk alarmu: dźwięk systemowy Windows (nazwa z <see cref="PriceAlert.SystemSounds"/>) albo plik .wav/.mp3.
/// Wywoływać na wątku UI (WPF <see cref="MediaPlayer"/>).
/// </summary>
public static class AlertSoundPlayer
{
    // Odtwarzacze plików trzymane do końca odtwarzania (inaczej GC mógłby je przerwać).
    private static readonly List<MediaPlayer> Playing = [];

    /// <summary>Czy dźwięk jest dźwiękiem systemowym (a nie ścieżką pliku).</summary>
    public static bool IsSystemSound(string sound) => PriceAlert.SystemSounds.Contains(sound, StringComparer.OrdinalIgnoreCase);

    /// <summary>Opis dźwięku do wyświetlenia (nazwa systemowa albo nazwa pliku).</summary>
    public static string Describe(string sound) => IsSystemSound(sound) ? sound : Path.GetFileName(sound);

    /// <summary>
    /// Odtwarza dźwięk. Zwraca false, gdy pliku nie ma albo nie da się go odtworzyć – wtedy gra dźwięk systemowy
    /// „Exclamation”, żeby alarm nie przepadł.
    /// </summary>
    public static bool Play(string sound)
    {
        if (string.IsNullOrWhiteSpace(sound) || IsSystemSound(sound))
        {
            PlaySystem(sound);
            return true;
        }

        if (!File.Exists(sound))
        {
            PlaySystem("Exclamation");
            return false;
        }

        try
        {
            var player = new MediaPlayer();
            player.MediaEnded += (_, _) => Release(player);
            player.MediaFailed += (_, _) =>
            {
                Release(player);
                PlaySystem("Exclamation");
            };
            player.Open(new Uri(Path.GetFullPath(sound)));
            player.Play();
            Playing.Add(player);
            return true;
        }
        catch (Exception)
        {
            PlaySystem("Exclamation");
            return false;
        }
    }

    private static void Release(MediaPlayer player)
    {
        player.Close();
        Playing.Remove(player);
    }

    private static void PlaySystem(string name)
    {
        var sound = name?.ToLowerInvariant() switch
        {
            "asterisk" => SystemSounds.Asterisk,
            "hand" => SystemSounds.Hand,
            "question" => SystemSounds.Question,
            "beep" => SystemSounds.Beep,
            _ => SystemSounds.Exclamation,
        };
        sound.Play();
    }
}
