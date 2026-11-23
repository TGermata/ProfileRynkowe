# Market Profile / TPO

Aplikacja desktopowa (C# / .NET, WPF) do rysowania wykresów Market Profile (TPO) i profilu wolumenowego
na danych z Interactive Brokers – na żywo, z opóźnieniem lub w symulacji z lokalnej bazy danych.

## Najważniejsze funkcje
- Profile TPO dla sesji dziennej i nocnej (godziny i strefa czasowa osobno dla każdego instrumentu).
- POC, Value Area, Initial Balance, HalfBack, strzałki otwarcia i zamknięcia, zegar bieżącego okresu TPO.
- Rozbijanie i łączenie profili, tryb Split (Expanded) z kreskami połowy słupków.
- Profil wolumenowy: dla ostatniego albo dla wszystkich profili, profil bieżącego słupka TPO, liczby wolumenu przy słupkach.
- Profil wolumenowy tradycyjny albo różnicowy (delta = kupno − sprzedaż) – przełącznik w menu „Wyświetlanie” i w Ustawieniach. Strona transakcji z notowań bid/ask (TWS: subskrypcja tick-by-tick „BidAsk”, Web API: pola bid/ask) zapisywana w bazie (kolumna `side`); starsze ticki i transakcje w środku spreadu – reguła ticka (szacunek); świece 1-min nie mają strony, więc pod profilem widać deltę sesji i jaka część wolumenu jest w niej uwzględniona.
- Linie cenowe (kolor, grubość, styl, opis, blokada).
- Poziomy alarmowe z dźwiękiem (prawy przycisk na wykresie → „🔔 Dodaj poziom alarmowy”): wyróżnione kreską-kropką, dzwonkiem i ramką na osi; alarm jednokrotny albo przy każdym przebiciu (z odsunięciem w tickach), kierunek oba / w górę / w dół, dźwięk Windows albo własny plik .wav/.mp3. Przełączniki „Alarmy dźwiękowe” i „Alarmy w symulacji” w menu „Wyświetlanie”.
- Powiększanie i przesuwanie jak na mapie, automatyczne doczytywanie starszej historii.
- Interactive Brokers: kontrakty futures, akcje i ETF-y, indeksy; dane na żywo albo opóźnione –
  przez TWS / IB Gateway (TWS API) albo bez TWS przez IBKR Web API (klucze OAuth).
- Zapis danych do lokalnej bazy SQLite i symulacja (odtwarzanie) z regulacją prędkości.

## Struktura
- `src/MarketProfile.Core` – model danych i obliczenia profilu (bez warstwy prezentacji).
- `src/MarketProfile.DataFeed` – generator danych, baza SQLite, odtwarzanie symulacji.
- `src/MarketProfile.DataFeed.IB` – połączenie z Interactive Brokers (TWS / IB Gateway).
- `src/MarketProfile.DataFeed.IBWeb` – połączenie z Interactive Brokers przez IBKR Web API (OAuth 1.0a, REST + WebSocket).
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

## IBKR Web API (bez TWS)
Źródło „Interactive Brokers – IBKR Web API” łączy się bezpośrednio z `api.ibkr.com` kluczami OAuth 1.0a
(consumer key, access token, access token secret, prywatny klucz podpisu, prywatny klucz szyfrowania, `dhparam.pem`),
wygenerowanymi w portalu IBKR dla Twojego konta.

**Klucze nigdy nie trafiają do repozytorium.** Wpisz je w Ustawienia → Dane i broker → Źródło i połączenie i kliknij
„Zapisz klucze”. Aplikacja szyfruje je przez Windows (DPAPI – odczytać może tylko Twoje konto Windows) do pliku
`%AppData%\MarketProfile\ibkr-web-api.secret`; w `settings.json` ich nie ma. Pliki `.pem` trzymaj poza folderem
repozytorium – `.gitignore` dodatkowo blokuje `*.pem`, `*.key`, `*.secret` i podobne.

Ograniczenia Web API względem TWS API:
- brak historii ticków – w tle pobierane są tylko świece 1-min i D/W/M (symulacja korzysta ze świec),
- transakcje na żywo składane ze strumienia notowań (bez tick-by-tick),
- świece D/W/M kontraktów futures tylko z bieżącego kontraktu (brak kontraktu ciągłego),
- IBKR pozwala na jedną sesję na użytkownika – Web API i TWS zalogowane na ten sam login się wykluczają
  (opcja „Przejmij sesję” rozłącza TWS).
