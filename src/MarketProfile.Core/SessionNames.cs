namespace MarketProfile.Core;

/// <summary>Nazwy sesji używane w całej aplikacji (profil dzienny i nocny).</summary>
public static class SessionNames
{
    /// <summary>Sesja dzienna – najważniejsza w analizie relacji między profilami.</summary>
    public const string Day = "Dzienna";

    /// <summary>Sesja nocna – w analizie liczą się tylko jej POC, High i Low.</summary>
    public const string Night = "Nocna";

    public static bool IsDay(string sessionName) => sessionName == Day;
}
