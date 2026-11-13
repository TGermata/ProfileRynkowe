# Market Profile / TPO

Aplikacja desktopowa (C# / .NET, WPF) do rysowania wykresów Market Profile (TPO) i profilu wolumenowego
na danych z Interactive Brokers – na żywo, z opóźnieniem lub w symulacji z lokalnej bazy danych.

## Najważniejsze funkcje
- Profile TPO dla sesji dziennej i nocnej (godziny i strefa czasowa osobno dla każdego instrumentu).
- POC, Value Area, Initial Balance, HalfBack, strzałki otwarcia i zamknięcia, zegar bieżącego okresu TPO.
- Rozbijanie i łączenie profili, tryb Split (Expanded) z kreskami połowy słupków.
- Profil wolumenowy: dla ostatniego albo dla wszystkich profili, profil bieżącego słupka TPO, liczby wolumenu przy słupkach.
- Linie cenowe (kolor, grubość, styl, opis, blokada).
- Powiększanie i przesuwanie jak na mapie, automatyczne doczytywanie starszej historii.
- Interactive Brokers: kontrakty futures, akcje i ETF-y, indeksy; dane na żywo albo opóźnione.
- Zapis danych do lokalnej bazy SQLite i symulacja (odtwarzanie) z regulacją prędkości.

## Struktura
- `src/MarketProfile.Core` – model danych i obliczenia profilu (bez warstwy prezentacji).
- `src/MarketProfile.DataFeed` – generator danych, baza SQLite, odtwarzanie symulacji.
- `src/MarketProfile.DataFeed.IB` – połączenie z Interactive Brokers (TWS / IB Gateway).
- `src/MarketProfile.App` – aplikacja WPF (wykres, ustawienia).
- `tests/MarketProfile.Core.Tests` – testy.

## Kompilacja
Wymagany .NET SDK 11 (Windows).

Biblioteka TWS API od Interactive Brokers **nie jest częścią repozytorium**. Pobierz ją z
https://interactivebrokers.github.io/ i rozpakuj tak, aby powstał folder `TWS API\CSharpClient` w katalogu głównym
repozytorium. Następnie skompiluj bibliotekę `TWS API\CSharpClient\client` (Release, net8.0). Inną lokalizację
biblioteki można wskazać w aplikacji: Ustawienia → Interactive Brokers.

```
dotnet build MarketProfile.slnx
dotnet test MarketProfile.slnx
```

Ustawienia użytkownika i rysunki są zapisywane w `%AppData%\MarketProfile`.
