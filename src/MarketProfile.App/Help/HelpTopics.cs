namespace MarketProfile.App.Help;

/// <summary>
/// Jedno hasło pomocy: parametr profilu opisany po angielsku (terminologia Jima Daltona – „Mind Over Markets”,
/// „Markets in Profile”) i po polsku, z opisem, jak liczy go aplikacja.
/// </summary>
/// <param name="Group">Grupa na liście (np. „Podstawy profilu”).</param>
/// <param name="Abbreviation">Skrót używany w aplikacji (tabela, wykres, baza), pusty gdy brak.</param>
/// <param name="TermEn">Termin angielski.</param>
/// <param name="TermPl">Nazwa polska.</param>
/// <param name="DescriptionEn">Opis po angielsku – znaczenie wg Daltona.</param>
/// <param name="DescriptionPl">Opis po polsku – znaczenie i interpretacja.</param>
/// <param name="InApp">Jak parametr liczy i pokazuje aplikacja (po polsku).</param>
/// <param name="Columns">Kolumny w bazie (tabela profile_stats) i w eksporcie CSV; pusty gdy brak.</param>
public sealed record HelpTopic(
    string Group,
    string Abbreviation,
    string TermEn,
    string TermPl,
    string DescriptionEn,
    string DescriptionPl,
    string InApp,
    string Columns = "")
{
    /// <summary>Tytuł na liście, np. „POC – Point of Control”.</summary>
    public string Title => Abbreviation.Length > 0 && Abbreviation != TermEn ? $"{Abbreviation} – {TermEn}" : TermEn;

    /// <summary>Czy hasło pasuje do wyszukiwanego tekstu (dowolne pole, bez wielkości liter).</summary>
    public bool Matches(string text) =>
        string.IsNullOrWhiteSpace(text) ||
        new[] { Abbreviation, TermEn, TermPl, DescriptionEn, DescriptionPl, InApp, Columns, Group }
            .Any(f => f.Contains(text.Trim(), StringComparison.CurrentCultureIgnoreCase));
}

/// <summary>Treść pomocy – parametry profili.</summary>
public static class HelpTopics
{
    public const string GroupBasics = "1. Podstawy profilu";
    public const string GroupStructure = "2. Struktura profilu";
    public const string GroupOpen = "3. Otwarcie dnia";
    public const string GroupRelations = "4. Relacje z poprzednim dniem";
    public const string GroupTrend = "5. Trend i typy dnia";

    public static IReadOnlyList<HelpTopic> All { get; } =
    [
        // ---------------------------------------------------------------- 1. Podstawy
        new(GroupBasics, "TPO", "Time Price Opportunity", "TPO – litera profilu",
            "A TPO is one letter in the profile: it marks that a price was traded during a given time period (usually 30 minutes). " +
            "Each period gets its own letter (A, B, C…). Stacking TPOs at each price builds a distribution that shows how much time " +
            "the market spent at each price – time is the measure of acceptance.",
            "TPO to jedna litera w profilu: oznacza, że w danym okresie (zwykle 30 minut) handlowano po tej cenie. Każdy okres ma " +
            "swoją literę (A, B, C…). Litery ułożone przy cenach tworzą rozkład, który pokazuje, ile czasu rynek spędził przy każdej " +
            "cenie – czas jest miarą akceptacji ceny.",
            "Długość okresu TPO i wysokość wiersza ustawiasz w Ustawieniach → Profile. Kolumna „TPO” w tabeli to łączna liczba liter profilu.",
            "periods, last_letter, TPO_minutes, total_TPO"),

        new(GroupBasics, "POC", "Point of Control", "POC – punkt kontroli",
            "The price at which the most TPOs occurred – the price where the market spent the most time and therefore the " +
            "\"fairest\" price of the session. If two prices have the same count, the one closest to the centre of the range is used. " +
            "The POC acts as a magnet in balanced markets and as a reference for value.",
            "Cena, przy której jest najwięcej TPO – rynek spędził tam najwięcej czasu, więc to „najbardziej sprawiedliwa” cena sesji. " +
            "Przy remisie wybierana jest cena bliżej środka zakresu. W rynku w równowadze POC przyciąga cenę; jest punktem odniesienia dla wartości.",
            "Wiersz z największą liczbą liter; sposób rozstrzygania remisu – Ustawienia → Profile → „Remis POC”. Na wykresie ramka wiersza i wartość w nagłówku profilu.",
            "POC, POC_position"),

        new(GroupBasics, "VPOC", "Volume Point of Control", "VPOC – POC wolumenowy",
            "The price with the highest traded volume in the session. Dalton (Markets in Profile) uses volume to confirm the TPO " +
            "distribution: when the VPOC and the TPO POC are close, value is well defined; when they differ, volume shows where " +
            "the real business was done.",
            "Cena z największym wolumenem w sesji. Dalton (Markets in Profile) używa wolumenu do potwierdzenia rozkładu TPO: gdy VPOC " +
            "i POC są blisko, wartość jest wyraźna; gdy się różnią, wolumen pokazuje, gdzie naprawdę zawierano transakcje.",
            "Wiersz z największym wolumenem (profil wolumenowy włączysz w menu Wyświetlanie).",
            "VPOC"),

        new(GroupBasics, "VA / VAH / VAL", "Value Area (High / Low)", "Obszar wartości – VA (górna / dolna granica)",
            "The range of prices where roughly 70% of the session's TPOs occurred (about one standard deviation), built outward from " +
            "the POC. Value is where the market accepted price and did most of its business. VAH and VAL are the upper and lower " +
            "edges; trading outside value is either rejected (price returns) or accepted (value migrates).",
            "Zakres cen, w którym wystąpiło ok. 70% TPO sesji (mniej więcej jedno odchylenie standardowe), budowany od POC w górę i w dół. " +
            "Wartość to obszar, który rynek zaakceptował i gdzie zawarto większość transakcji. VAH i VAL to jego górna i dolna granica; " +
            "handel poza wartością jest albo odrzucany (cena wraca), albo akceptowany (wartość się przesuwa).",
            "Procent TPO w VA – Ustawienia → Profile → „Value Area”. Szer. VA = VAH − VAL + wysokość wiersza.",
            "VAH, VAL, VA_width"),

        new(GroupBasics, "IB / IBH / IBL", "Initial Balance", "Initial Balance – zakres początkowy",
            "The price range of the first hour of trading (A and B periods in 30-minute profiles). It is set mostly by day-timeframe " +
            "traders. A wide IB suggests a balanced, rotational day; a narrow IB makes range extension and a trending day more likely.",
            "Zakres cen pierwszej godziny handlu (litery A i B przy okresach 30-minutowych). Tworzą go głównie traderzy krótkoterminowi. " +
            "Szeroki IB zapowiada dzień w równowadze, wąski IB zwiększa szansę wybicia i dnia trendowego.",
            "Liczba okresów IB – Ustawienia → Profile → „Initial Balance”. Szer. IB = IBH − IBL. Na wykresie pionowy pasek przy profilu (menu Wyświetlanie → IB).",
            "IBH, IBL, IB_width"),

        new(GroupBasics, "", "Range Extension", "Wybicie z IB (rozszerzenie zakresu)",
            "Any move beyond the Initial Balance. Range extension is the footprint of the other-timeframe trader (longer-term " +
            "buyer or seller) entering the market. Extension on one side only shows directional control; extension on both sides " +
            "is typical of a Neutral day.",
            "Każde wyjście ceny poza Initial Balance. Wybicie to ślad uczestnika z dłuższego horyzontu (other-timeframe), który wchodzi " +
            "na rynek. Wybicie tylko w jedną stronę pokazuje przewagę kierunkową; w obie strony – typowe dla dnia neutralnego.",
            "O ile cena wyszła ponad IBH (↑) / poniżej IBL (↓) i pierwsza litera, która wyszła poza IB.",
            "IB_ext, IB_ext_up, IB_ext_up_letter, IB_ext_down, IB_ext_down_letter"),

        new(GroupBasics, "HB", "Halfback", "HB – halfback (połowa zakresu)",
            "The midpoint of the session's range: (High + Low) / 2. Not a core Dalton term, but a common profile reference: the market " +
            "opening above the prior day's halfback leans bullish, below it bearish.",
            "Środek zakresu sesji: (High + Low) / 2. Nie jest to podstawowy termin Daltona, ale popularny punkt odniesienia: otwarcie nad " +
            "halfbackiem poprzedniego dnia sprzyja kupującym, pod nim – sprzedającym.",
            "Zaokrąglony do ticka. Na wykresie linia „HB” (menu Wyświetlanie → Opisy dnia). Kolumna „Otwarcie vs HB poprz.”.",
            "HB, prev_HB, open_vs_prev_HB"),

        new(GroupBasics, "SETTLE", "Settlement price", "SETTLE – cena rozliczeniowa",
            "The official daily settlement price set by the exchange. Dalton measures overnight inventory and the day's net change " +
            "against the prior settlement.",
            "Oficjalna cena rozliczeniowa dnia ustalana przez giełdę. Dalton mierzy względem niej inwentarz z nocy i zmianę dnia.",
            "Wpis ręczny (prawy klik w tabeli albo na profilu), a gdy go brak – Close sesji dziennej. Na wykresie linia „S”.",
            "SETTLE, SETTLE_source, prev_SETTLE"),

        new(GroupBasics, "ONH / ONL / ONPOC", "Overnight High / Low / POC", "Noc – High, Low i POC sesji nocnej",
            "The high, low and POC of the overnight (Globex) session before the day session. They are important references: the day " +
            "session often tests them early, and an open outside the overnight range signals strong initiative.",
            "High, Low i POC sesji nocnej (Globex) przed sesją dzienną. To ważne punkty odniesienia: sesja dzienna często testuje je na " +
            "początku, a otwarcie poza zakresem nocy pokazuje silną inicjatywę.",
            "Z ostatniej sesji nocnej bezpośrednio przed dniem. Na wykresie linie ONH / ONL / ONPOC (menu Wyświetlanie → Poziomy nocy).",
            "ONH, ONL, ONPOC"),

        new(GroupBasics, "", "Open / High / Low / Close / Range", "Open / High / Low / Close / Zakres",
            "The session's first, highest, lowest and last price, and its range (High − Low). In Dalton's approach the open and close " +
            "matter mostly in relation to value and to the range extremes (e.g. a trend day opens near one extreme and closes near the other).",
            "Pierwsza, najwyższa, najniższa i ostatnia cena sesji oraz jej zakres (High − Low). U Daltona otwarcie i zamknięcie liczą się " +
            "głównie względem wartości i skrajów zakresu (np. dzień trendowy otwiera się przy jednym krańcu, a zamyka przy drugim).",
            "Strzałki otwarcia i zamknięcia na wykresie (menu Wyświetlanie). Kolumna „Close poprz.” – Close poprzedniego dnia.",
            "open, high, low, close, range, prev_close"),

        // ---------------------------------------------------------------- 2. Struktura
        new(GroupStructure, "", "Profile shape", "Kształt profilu (D, P, b, B, Trend)",
            "The shape of the distribution tells who controlled the session. D – normal, balanced distribution. P – value in the upper " +
            "part with a thin lower tail, typically short covering (old business). b – value in the lower part with a thin upper tail, " +
            "typically long liquidation. B – double distribution: two areas of value separated by a thin area of single prints. " +
            "Trend – elongated, thin profile of a trend day.",
            "Kształt rozkładu mówi, kto kontrolował sesję. D – rozkład normalny, równowaga. P – wartość w górnej części, cienki ogon na dole, " +
            "zwykle zamykanie krótkich pozycji (tzw. stary biznes). b – wartość w dolnej części, cienki ogon u góry, zwykle likwidacja " +
            "długich pozycji. B – podwójny rozkład: dwa obszary wartości rozdzielone cienkim odcinkiem single printów. Trend – wydłużony, " +
            "wąski profil dnia trendowego.",
            "P / b: środek ciężkości TPO i POC w górnych / dolnych ok. 40% zakresu. B: dwa skupiska TPO z przewężeniem ≤ 50% mniejszego " +
            "szczytu. Trend: dzień trendowy wg interwałowania (patrz „Trend day”).",
            "shape"),

        new(GroupStructure, "", "TPO count", "TPO nad / pod POC",
            "Dalton's TPO count compares the TPOs above and below the POC (excluding single-print extremes). More TPOs above " +
            "the POC shows selling imbalance (sellers were active higher); more below shows buying imbalance.",
            "TPO count Daltona porównuje liczbę TPO nad i pod POC (bez single printów na krańcach). Więcej TPO nad POC oznacza przewagę " +
            "sprzedających (byli aktywni wyżej), więcej pod POC – przewagę kupujących.",
            "Aplikacja liczy wszystkie litery nad i pod wierszem POC (łącznie z ogonami).",
            "TPO_above_POC, TPO_below_POC"),

        new(GroupStructure, "", "POC position", "POC w zakresie",
            "Where the POC sits inside the session's range. A POC near the top or bottom shows that the session's value was skewed " +
            "toward that extreme.",
            "Położenie POC w zakresie sesji. POC blisko góry albo dołu pokazuje, że wartość sesji przesunęła się w stronę tego krańca.",
            "0 = POC na dole zakresu, 1 = na górze.",
            "POC_position"),

        new(GroupStructure, "", "Excess (tail)", "Ogon (excess)",
            "Two or more single TPOs at an extreme of the profile. Excess marks the end of an auction: the other-timeframe trader " +
            "aggressively rejected those prices, so a tail is strong support (buying tail) or resistance (selling tail).",
            "Co najmniej dwie pojedyncze litery na krańcu profilu. Ogon oznacza koniec aukcji: uczestnik z dłuższego horyzontu agresywnie " +
            "odrzucił te ceny, więc ogon to silne wsparcie (ogon kupujących na dole) albo opór (ogon sprzedających u góry).",
            "Długość ogona w cenie, kolumna „Ogony / poor”.",
            "excess_high, excess_low"),

        new(GroupStructure, "", "Poor high / Poor low", "Poor high / poor low – słaby szczyt / dołek",
            "An extreme with no excess – several TPOs at the high or low, a flat top or bottom. It shows the auction ended without " +
            "conviction (often short-term traders only), so the market is likely to revisit and repair that area.",
            "Kraniec bez ogona – kilka liter na najwyższej albo najniższej cenie, płaski szczyt lub dołek. Aukcja skończyła się bez " +
            "przekonania (zwykle tylko gracze krótkoterminowi), więc rynek prawdopodobnie wróci i „naprawi” ten obszar.",
            "Co najmniej 2 litery w skrajnym wierszu i brak ogona.",
            "poor_high, poor_low"),

        new(GroupStructure, "", "Single prints", "Single printy",
            "A run of single TPOs inside the profile, left by a fast, one-directional move. They show strong imbalance and usually " +
            "act as support or resistance; a double distribution is separated by single prints.",
            "Odcinek pojedynczych liter wewnątrz profilu, zostawiony przez szybki ruch w jedną stronę. Pokazuje silną nierównowagę i zwykle " +
            "działa jak wsparcie lub opór; podwójny rozkład (B) jest rozdzielony single printami.",
            "Odcinki co najmniej 2 wierszy z jedną literą, bez ogonów na krańcach.",
            "single_prints"),

        // ---------------------------------------------------------------- 3. Otwarcie
        new(GroupOpen, "", "Open type", "Typ otwarcia",
            "Dalton's four open types show the conviction of the market at the open. Open-Drive: price opens and drives in one direction " +
            "without trading back through the open – the highest conviction. Open-Test-Drive: price first tests a reference in one direction, " +
            "then reverses and drives the other way. Open-Rejection-Reverse: price moves in one direction, meets opposing activity and " +
            "reverses back through the open. Open-Auction: price rotates around the open – no conviction, the market is searching.",
            "Cztery typy otwarcia Daltona pokazują przekonanie rynku na otwarciu. Open-Drive: cena od otwarcia idzie zdecydowanie w jedną " +
            "stronę i nie wraca do ceny otwarcia – najsilniejsze przekonanie. Open-Test-Drive: najpierw test poziomu w jedną stronę, potem " +
            "zawrócenie i zdecydowany ruch w drugą. Open-Rejection-Reverse: ruch w jedną stronę napotyka przeciwną aktywność i cena wraca " +
            "przez otwarcie na drugą stronę. Open-Auction: rotacja wokół otwarcia – brak przekonania, rynek szuka kierunku.",
            "Z liter A i B (O = cena otwarcia): Open-Drive – O przy skraju A (≤ 15% zakresu A), A zamyka się po drugiej stronie, B nie wraca " +
            "do O; Open-Test-Drive – A testuje stronę przeciwną (15–50% zakresu A), B rozszerza zakres w kierunku ruchu; Open-Rejection-Reverse – " +
            "A jednostronna (ruch ≥ 40% zakresu liter A+B i ≥ 2× ruchu w drugą stronę), B zamyka się po drugiej stronie O; w pozostałych " +
            "przypadkach Open-Auction. Strzałka – kierunek.",
            "open_type, open_direction"),

        new(GroupOpen, "", "Open location (open relative to prior value and range)", "Otwarcie vs VA poprzedniego dnia",
            "Where today opens relative to yesterday's value area and range. An open within value suggests balance and acceptance of " +
            "yesterday's prices; outside value but within range – moderate imbalance; outside the range – strong imbalance, and if " +
            "price is accepted there, a potential trend day.",
            "Gdzie dziś otworzył się rynek względem obszaru wartości i zakresu poprzedniego dnia. Otwarcie w VA zapowiada równowagę " +
            "i akceptację wczorajszych cen; poza VA, ale w zakresie – umiarkowaną nierównowagę; poza zakresem – silną nierównowagę, " +
            "a jeśli cena zostanie tam zaakceptowana, możliwy dzień trendowy.",
            "Wartości: nad High / nad VA / w VA / pod VA / pod Low (poprzedniego dnia). Liczone dla sesji dziennych; karta „Otwarcie” pokazuje to także dla sesji nocnej (względem dnia przed nią). " +
            "Karta „Otwarcie” (menu Wyświetlanie) pojawia się przy każdym nowym profilu – w symulacji po skoku ⏭ / ⏮ (skok ląduje na pierwszej transakcji) i na żywo – z otwarciem, GAP, inwentarzem, nocą, HB i typem otwarcia.",
            "open_vs_prev_VA"),

        new(GroupOpen, "", "Open relative to overnight range", "Otwarcie vs noc",
            "Where the day session opens relative to the overnight high and low. An open beyond the overnight range shows strong " +
            "initiative; an open inside it usually leads to early rotation within the overnight range.",
            "Gdzie otworzyła się sesja dzienna względem ONH i ONL. Otwarcie poza zakresem nocy pokazuje silną inicjatywę, " +
            "otwarcie w środku zwykle prowadzi do rotacji w zakresie nocy na początku dnia.",
            "Wartości: nad ONH / w nocy (ONL–ONH) / pod ONL. Opis pod profilem: „Otwarcie > ONH”, „Otwarcie w nocy”, „Otwarcie < ONL”.",
            "open_vs_overnight"),

        new(GroupOpen, "GAP", "Gap (open outside prior range)", "GAP – luka otwarcia",
            "An open outside the previous day's range. Dalton treats it as a sign of strong imbalance: if the gap is not filled early " +
            "(price does not return into yesterday's range), the move has conviction.",
            "Otwarcie poza zakresem poprzedniego dnia. Dalton traktuje to jako znak silnej nierównowagi: jeśli luka nie zostanie szybko " +
            "zamknięta (cena nie wróci do wczorajszego zakresu), ruch ma przekonanie.",
            "Open − High poprzedniego dnia (otwarcie nad High, dodatnia) albo Open − Low (pod Low, ujemna); brak luki – „–”.",
            "GAP"),

        new(GroupOpen, "", "Overnight inventory", "Inwentarz na otwarciu",
            "Dalton (Markets in Profile) looks at how the overnight session leaves short-term traders positioned relative to the prior " +
            "settlement. When inventory is net long (price above settlement) or net short, the market often corrects it early in the day " +
            "session – price moves back toward the settlement before a new direction develops.",
            "Dalton (Markets in Profile) patrzy, jak sesja nocna ustawiła pozycje graczy krótkoterminowych względem poprzedniego SETTLE. " +
            "Gdy inwentarz jest długi (cena nad SETTLE) albo krótki, rynek często wyrównuje go na początku sesji dziennej – cena wraca " +
            "w stronę SETTLE, zanim wybierze nowy kierunek.",
            "Otwarcie dnia względem SETTLE poprzedniego dnia: długi / krótki / neutralny, wielkość (Open − SETTLE poprz.) i litera, " +
            "w której cena wróciła do SETTLE (wyrównanie). Przyjęta szansa wyrównania ok. 65% – statystyka w stopce tabeli. " +
            "Karta „Otwarcie”: inwentarz jest istotny, gdy |Open − SETTLE| ≥ 15% zakresu poprzedniego dnia (Ustawienia → Profile) – wtedy strzałka kierunku spodziewanej korekty do SETTLE: ↓ czerwona przy długim, ↑ zielona przy krótkim, ✓ po wyrównaniu; mniejszy odstęp jest przygaszony jako bez znaczenia.",
            "inventory, inventory_size, inventory_corrected, inventory_corrected_letter"),

        new(GroupOpen, "", "Open relative to prior halfback", "Otwarcie vs HB poprzedniego dnia",
            "Whether today opens above or below the midpoint of yesterday's range – a simple bias: above favours buyers, below favours sellers.",
            "Czy dziś otwarcie jest nad, czy pod środkiem zakresu poprzedniego dnia – prosty kierunek: nad sprzyja kupującym, pod sprzedającym.",
            "Wartości: nad / pod / na poziomie (cena HB w nawiasie).",
            "open_vs_prev_HB"),

        // ---------------------------------------------------------------- 4. Relacje
        new(GroupRelations, "", "Value area migration (two-day value relationship)", "Migracja VA",
            "Dalton compares today's value area with yesterday's to read the market's direction. Higher value – buyers in control; " +
            "overlapping-to-higher – moderately bullish; lower and overlapping-to-lower – the bearish equivalents; inside value – " +
            "balance and contraction, a breakout is likely; outside value (wider, containing yesterday's) – a big two-sided " +
            "trade, often a turning point.",
            "Dalton porównuje dzisiejszą VA z wczorajszą, żeby odczytać kierunek rynku. Wyżej – przewaga kupujących; zachodzi wyżej – " +
            "umiarkowanie wzrostowo; niżej i zachodzi niżej – odpowiedniki spadkowe; węższa (wewnątrz) – równowaga i zawężanie, możliwe " +
            "wybicie; szersza (obejmuje wczorajszą) – duży handel w obie strony, często punkt zwrotny.",
            "Wartości: wyżej / zachodzi wyżej / zachodzi niżej / niżej / węższa (wewnątrz) / szersza (obejmuje). W dniu trendowym VA jest " +
            "pomijana. Opis pod profilem: „VA ↑ wyżej”, „VA ↗ pokr. wyżej” itd.",
            "value_migration"),

        new(GroupRelations, "", "Value area overlap", "Nakładanie VA",
            "How much of today's value overlaps yesterday's. Large overlap means the market is still accepting the same prices " +
            "(balance); little or no overlap means value has moved (imbalance).",
            "Jaka część dzisiejszej VA pokrywa się z wczorajszą. Duże nakładanie – rynek dalej akceptuje te same ceny (równowaga); " +
            "małe albo żadne – wartość się przesunęła (nierównowaga).",
            "Ułamek VA dnia pokrywający się z VA poprzedniego dnia (0–100%).",
            "VA_overlap"),

        new(GroupRelations, "", "Naked (virgin) POC", "Nietknięte POC / VPOC",
            "A prior session's POC that price has not traded at since. Not a core Dalton term, but widely used: naked POCs act as " +
            "magnets and targets, because the market tends to revisit prior areas of fair value.",
            "POC wcześniejszej sesji, do którego cena od tamtej pory nie wróciła. Nie jest to podstawowy termin Daltona, ale często " +
            "używany: nietknięte POC przyciągają cenę i bywają celami, bo rynek lubi wracać do dawnych obszarów wartości.",
            "Z ostatnich 20 dni; poziom jest „dotknięty”, gdy cena w dowolnej późniejszej sesji (dziennej albo nocnej) weszła w jego wiersz.",
            "naked_POCs, naked_VPOCs"),

        // ---------------------------------------------------------------- 5. Trend
        new(GroupTrend, "", "One-timeframing and trend day", "Trend (interwałowanie) i dzień trendowy",
            "One-timeframing: each period trades higher (lows not below the prior period's low) or lower – only one timeframe is " +
            "active and in control. A Trend day is dominated by the other-timeframe trader: a narrow IB, early range extension, " +
            "one-timeframing for most of the day, an open near one extreme and a close near the other. On trend days value matters " +
            "less – price, not value, is what moves.",
            "One-timeframing: każdy okres handluje wyżej (dołki nie niżej niż w poprzednim okresie) albo niżej – aktywny i decydujący jest " +
            "tylko jeden horyzont czasowy. Dzień trendowy jest zdominowany przez uczestnika z dłuższego horyzontu: wąski IB, wczesne " +
            "wybicie, one-timeframing przez większość dnia, otwarcie przy jednym krańcu, zamknięcie przy drugim. W dniu trendowym " +
            "wartość ma mniejsze znaczenie – porusza się cena, nie wartość.",
            "Aplikacja szuka najdłuższej serii liter, w której każda otwiera się wyżej (↑) / niżej (↓) niż poprzednia (porównuje ceny " +
            "otwarcia liter, nie dołki). Dzień trendowy: seria obejmuje ≥ 60% okresów sesji i co najmniej 4 okresy. Kolumna " +
            "„Trend (interwałowanie)”, np. „↑ trend B–K (10)”; opis pod profilem „Trend ↑ B–K”.",
            "trend_direction, is_trend_day, trend_from, trend_to, trend_periods"),

        new(GroupTrend, "PBL / PBH", "Pullback low / high", "PBL / PBH – pullback dnia trendowego",
            "Not a Dalton term: the extreme of the counter-move after a trend ends within the day. It marks how deep the market " +
            "retraced once the one-timeframing stopped.",
            "Nie jest to termin Daltona: skrajna cena ruchu powrotnego po zakończeniu trendu w ciągu dnia. Pokazuje, jak głęboko rynek " +
            "się cofnął, gdy seria się skończyła.",
            "PBL – w dniu trendowym w górę najniższy Low liter po końcu serii; PBH – w dniu trendowym w dół najwyższy High. Litera w nawiasie. " +
            "Na wykresie linie „PBL X” / „PBH X” (menu Wyświetlanie).",
            "PBL, PBH, PB_letter"),

        new(GroupTrend, "", "Day types", "Typy dnia wg Daltona",
            "Dalton classifies days by how the range develops from the IB. Normal – a wide IB that contains most of the range. Normal " +
            "Variation – range extension of up to about twice the IB. Trend – the other timeframe in control all day. Double-Distribution " +
            "Trend – a narrow IB, then a move to a new area of value, separated by single prints. Neutral – range extension on both " +
            "sides of the IB (neutral-centre or neutral-extreme close). Nontrend – a narrow range with little activity.",
            "Dalton dzieli dni według tego, jak zakres rozwija się z IB. Normalny – szeroki IB obejmuje większość zakresu. Normalny " +
            "z wariantem – wybicie do ok. dwóch szerokości IB. Trendowy – przez cały dzień kontroluje uczestnik z dłuższego horyzontu. " +
            "Trendowy z podwójnym rozkładem – wąski IB, potem przejście do nowego obszaru wartości, rozdzielone single printami. " +
            "Neutralny – wybicie z IB w obie strony (zamknięcie w środku albo przy krańcu). Bez trendu – wąski zakres, mała aktywność.",
            "Aplikacja nie przypisuje typu dnia wprost – pomagają w tym: Kształt (D, B, Trend), Wybicie z IB (w jedną / obie strony), " +
            "Szer. IB, Trend (interwałowanie) i Ogony / poor.",
            "shape, IB_ext, IB_width, is_trend_day"),
    ];
}
