namespace MarketProfile.Core;

/// <summary>
/// Zamiana numeru okresu TPO na literę.
/// Okresy 0–25 to A–Z, 26–51 to a–z, kolejne to cyfry 0–9 (sesja nocna przy 30 min ma ok. 31 okresów).
/// </summary>
public static class TpoLetters
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>Zwraca literę dla okresu o podanym numerze (liczonym od 0).</summary>
    public static char ForPeriod(int periodIndex) =>
        periodIndex >= 0 && periodIndex < Alphabet.Length ? Alphabet[periodIndex] : '#';
}
