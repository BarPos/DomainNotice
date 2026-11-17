namespace DomainNotice.Console.Services;

internal static partial class ConsoleStrings
{
    /// <summary>Layout, navigation, sign-in pages, announcement editor, settings, agent-window preview, target picker.</summary>
    static partial void Shell(Dictionary<string, string> d)
    {
        // App bar and navigation
        d["Collapse or expand the navigation"] = "Zwiń lub rozwiń nawigację";
        d["Search computers, users, announcements"] = "Szukaj komputerów, użytkowników, ogłoszeń";
        d["Global search"] = "Wyszukiwanie globalne";
        d["Admin"] = "Administrator";
        d["Read-only"] = "Tylko odczyt";
        d["Sign out"] = "Wyloguj";
        d["Dashboard"] = "Pulpit";
        d["Computers"] = "Komputery";
        d["Announcements"] = "Ogłoszenia";
        d["Confirmations"] = "Potwierdzenia";
        d["Answers"] = "Odpowiedzi";
        d["History"] = "Historia";
        d["Agent updates"] = "Aktualizacje agentów";
        d["Directory"] = "Katalog";
        d["Settings"] = "Ustawienia";
        d["Audit log"] = "Dziennik audytu";
        d["A newer release is available"] = "Dostępna jest nowsza wersja";
        // Reconnection overlay (App.razor, console.js)
        d["The connection to the server was lost. Reconnecting…"] = "Utracono połączenie z serwerem. Ponowne łączenie…";
        d["The server is not reachable, for example because it is restarting after an update. This page continues as soon as it is back."] =
            "Serwer jest niedostępny, na przykład dlatego, że uruchamia się ponownie po aktualizacji. Strona będzie kontynuowana, gdy tylko serwer wróci.";
        d["The server is back. Opening the sign-in page…"] = "Serwer znowu działa. Otwieranie strony logowania…";
        d["Update"] = "Aktualizacja";
        d["You do not have access to this page."] = "Nie masz dostępu do tej strony.";

        // Sign-in, setup, denied, error, not found
        d["Sign in"] = "Zaloguj się";
        d["Sign in to the management console"] = "Zaloguj się do konsoli zarządzania";
        d["Sign-in failed. Check the account type, user name and password."] =
            "Logowanie nie powiodło się. Sprawdź typ konta, nazwę użytkownika i hasło.";
        d["The account was created. You can sign in now."] = "Konto zostało utworzone. Możesz się teraz zalogować.";
        d["No sign-in method is enabled. Configure the directory or enable local accounts."] =
            "Żadna metoda logowania nie jest włączona. Skonfiguruj katalog lub włącz konta lokalne.";
        d["Account type"] = "Typ konta";
        d["Domain account"] = "Konto domenowe";
        d["Local account"] = "Konto lokalne";
        d["User name"] = "Nazwa użytkownika";
        d["DOMAIN\\user or user@domain"] = "DOMENA\\użytkownik lub użytkownik@domena";
        d["Password"] = "Hasło";
        d["No account yet?"] = "Nie masz jeszcze konta?";
        d["Create the first local account"] = "Utwórz pierwsze konto lokalne";
        d["First-run setup"] = "Konfiguracja początkowa";
        d["No directory and no local account are configured. Create the first local administrator account."] =
            "Nie skonfigurowano katalogu ani konta lokalnego. Utwórz pierwsze lokalne konto administratora.";
        d["Password (at least 8 characters)"] = "Hasło (co najmniej 8 znaków)";
        d["Repeat the password"] = "Powtórz hasło";
        d["Create account"] = "Utwórz konto";
        d["The passwords do not match."] = "Hasła nie są zgodne.";
        d["The account could not be created."] = "Nie udało się utworzyć konta.";
        d["Access denied"] = "Brak dostępu";
        d["Your account is signed in but has no role for this console, or this action needs the Admin role."] =
            "Twoje konto jest zalogowane, ale nie ma roli w tej konsoli lub ta czynność wymaga roli Administrator.";
        d["Back to the console"] = "Powrót do konsoli";
        d["Error"] = "Błąd";
        d["Something went wrong"] = "Coś poszło nie tak";
        d["The request could not be completed. The details are in the server log."] =
            "Nie udało się wykonać żądania. Szczegóły znajdują się w dzienniku serwera.";
        d["The request could not be completed. The details are in the server log (request {0})."] =
            "Nie udało się wykonać żądania. Szczegóły znajdują się w dzienniku serwera (żądanie {0}).";
        d["Not found"] = "Nie znaleziono";
        d["Page not found"] = "Nie znaleziono strony";
        d["There is nothing at this address."] = "Pod tym adresem nic nie ma.";
        d["Go to the dashboard"] = "Przejdź do pulpitu";

        // Agent window preview
        d["Preview of the agent window"] = "Podgląd okna agenta";
        d["Your answers in this announcement are anonymous."] = "Twoje odpowiedzi w tym ogłoszeniu są anonimowe.";
        d["Buttons unlock after {0} s"] = "Przyciski odblokują się po {0} s";
        d["Buttons unlock when required questions are answered"] = "Przyciski odblokują się po odpowiedzi na wymagane pytania";
        d["Buttons unlock after {0} s and required answers"] = "Przyciski odblokują się po {0} s i odpowiedzi na wymagane pytania";

        // Target picker
        d["Selected targets"] = "Wybrani odbiorcy";
        d["No targets: the announcement goes to <b>everyone</b>."] = "Brak odbiorców: ogłoszenie trafi do <b>wszystkich</b>.";
        d["Any matching target is enough. Group membership comes from the synced directory (nested groups count)."] =
            "Wystarczy zgodność z dowolnym odbiorcą. Członkostwo w grupach pochodzi z synchronizowanego katalogu (grupy zagnieżdżone też się liczą).";
        d["Search users, groups, computers, OUs"] = "Szukaj użytkowników, grup, komputerów, jednostek OU";
        d["Choose an OU or search."] = "Wybierz jednostkę OU lub wyszukaj.";
        d["Nothing found."] = "Nic nie znaleziono.";
        d["Add user"] = "Dodaj użytkownika";
        d["Add computer"] = "Dodaj komputer";
        d["Add as user group"] = "Dodaj jako grupę użytkowników";
        d["as computer group"] = "jako grupę komputerów";
        d["Add OU"] = "Dodaj OU";
        d["Enrolled computers"] = "Zarejestrowane komputery";
        d["Computer name"] = "Nazwa komputera";
        d["Typed name"] = "Wpisana nazwa";
        d["No directory is configured. User and computer names are matched as typed; <b>group and OU targets never match</b> until a directory is set up."] =
            "Nie skonfigurowano katalogu. Nazwy użytkowników i komputerów są porównywane w postaci wpisanej; <b>grupy i jednostki OU nie zostaną dopasowane</b>, dopóki katalog nie zostanie skonfigurowany.";
        d["Kind"] = "Rodzaj";
        d["DOMAIN\\user"] = "DOMENA\\użytkownik";
        d["User"] = "Użytkownik";
        d["Computer"] = "Komputer";
        d["User group"] = "Grupa użytkowników";
        d["Computer group"] = "Grupa komputerów";
        d["OU"] = "OU";

        // Announcement editor
        d["New announcement"] = "Nowe ogłoszenie";
        d["Edit announcement"] = "Edycja ogłoszenia";
        d["This announcement does not exist."] = "To ogłoszenie nie istnieje.";
        d["Read-only access: you can look at the announcement but not save it."] =
            "Dostęp tylko do odczytu: możesz przeglądać ogłoszenie, ale nie możesz go zapisać.";
        d["Basic"] = "Podstawowe";
        d["Content"] = "Treść";
        d["Buttons and questions"] = "Przyciski i pytania";
        d["Targets"] = "Odbiorcy";
        d["Schedule"] = "Harmonogram";
        d["Summary"] = "Podsumowanie";
        d["Shown only in the Console (lists, results). The title people see is set in the Content step."] =
            "Widoczna tylko w konsoli (listy, wyniki). Tytuł widoczny dla użytkowników ustawisz w kroku Treść.";
        d["When can the buttons be used?"] = "Kiedy można użyć przycisków?";
        d["After the minimum time"] = "Po upływie minimalnego czasu";
        d["After the required questions are answered"] = "Po odpowiedzi na wymagane pytania";
        d["After the minimum time and the required answers"] = "Po upływie minimalnego czasu i odpowiedzi na wymagane pytania";
        d["Minimum seconds before the buttons unlock"] = "Minimalny czas do odblokowania przycisków (s)";
        d["Anonymous answers"] = "Anonimowe odpowiedzi";
        d["With anonymous answers, who confirmed is still recorded, but the answers are stored without any link to the person, computer or time, and can only be filtered by announcement, question and option."] =
            "Przy anonimowych odpowiedziach nadal jest zapisywane, kto potwierdził, ale odpowiedzi są przechowywane bez powiązania z osobą, komputerem ani czasem i można je filtrować tylko według ogłoszenia, pytania i opcji.";
        d["Draft (never delivered)"] = "Szkic (nigdy nie jest dostarczany)";
        d["Title"] = "Tytuł";
        d["Shown above the message on the PCs (optional)"] = "Wyświetlany nad treścią na komputerach (opcjonalnie)";
        d["Message (Markdown)"] = "Treść (Markdown)";
        d["Markdown: **bold**, *italic*, lists, links, headings. Raw HTML is not allowed."] =
            "Markdown: **pogrubienie**, *kursywa*, listy, linki, nagłówki. Surowy kod HTML jest niedozwolony.";
        d["Buttons"] = "Przyciski";
        d["The first button is the default. Existing buttons keep their identity when renamed."] =
            "Pierwszy przycisk jest domyślny. Istniejące przyciski po zmianie nazwy pozostają tymi samymi przyciskami.";
        d["Button {0}"] = "Przycisk {0}";
        d["Move up"] = "Przenieś w górę";
        d["Remove button"] = "Usuń przycisk";
        d["Add button"] = "Dodaj przycisk";
        d["Questions"] = "Pytania";
        d["A question that already has answers cannot be deleted, only hidden."] =
            "Pytania, na które już udzielono odpowiedzi, nie można usunąć, tylko ukryć.";
        d["Question {0}"] = "Pytanie {0}";
        d["Single choice"] = "Jednokrotny wybór";
        d["Multiple choice"] = "Wielokrotny wybór";
        d["Required"] = "Wymagane";
        d["Hidden"] = "Ukryte";
        d["has answers"] = "ma odpowiedzi";
        d["Answered questions can only be hidden"] = "Pytania z odpowiedziami można tylko ukryć";
        d["Delete question"] = "Usuń pytanie";
        d["Options"] = "Opcje";
        d["Option {0}"] = "Opcja {0}";
        d["Options of an answered question cannot be removed"] = "Nie można usuwać opcji pytania, na które udzielono odpowiedzi";
        d["Remove option"] = "Usuń opcję";
        d["Add option"] = "Dodaj opcję";
        d["Add question"] = "Dodaj pytanie";
        d["Times are in your browser's time zone (<b>{0}</b>) and stored as UTC."] =
            "Czasy są podane w strefie czasowej przeglądarki (<b>{0}</b>) i zapisywane w UTC.";
        d["Start"] = "Początek";
        d["End"] = "Koniec";
        d["Times shown per user"] = "Liczba wyświetleń na użytkownika";
        d["Counted per user across all computers."] = "Liczona dla użytkownika łącznie na wszystkich komputerach.";
        d["Repeat interval (minutes)"] = "Odstęp między wyświetleniami (minuty)";
        d["Time between showings when shown more than once; 0 = at the next opportunity."] =
            "Czas między kolejnymi wyświetleniami, gdy ogłoszenie jest pokazywane więcej niż raz; 0 = przy najbliższej okazji.";
        d["Ready to save."] = "Gotowe do zapisania.";
        d["Title on the PCs"] = "Tytuł na komputerach";
        d["(none)"] = "(brak)";
        d["Status"] = "Status";
        d["Draft (not delivered)"] = "Szkic (nie jest dostarczany)";
        d["Will be delivered in the time window"] = "Zostanie dostarczone w wyznaczonym przedziale czasu";
        d["Window"] = "Okres";
        d["{0} visible, {1} hidden"] = "widoczne: {0}, ukryte: {1}";
        d["Anonymous"] = "Anonimowe";
        d["Named"] = "Imienne";
        d["Everyone"] = "Wszyscy";
        d["Times shown"] = "Wyświetlanie";
        d["{0} time(s) per user"] = "{0} raz(y) na użytkownika";
        d["{0} time(s) per user, every {1} min"] = "{0} raz(y) na użytkownika, co {1} min";
        d["Preview of the window on the PCs"] = "Podgląd okna na komputerach";
        d["Save draft"] = "Zapisz szkic";
        d["{0} computer target(s) added from your selection."] = "Dodano odbiorców z zaznaczenia (komputery: {0}).";
        d["Fix the problems listed in the summary first."] = "Najpierw popraw problemy wymienione w podsumowaniu.";
        d["Announcement saved."] = "Ogłoszenie zostało zapisane.";
        d["The announcement could not be saved."] = "Nie udało się zapisać ogłoszenia.";

        // Settings: tabs
        d["General"] = "Ogólne";
        d["Enrolment"] = "Rejestracja";
        d["Agent install"] = "Instalacja agenta";
        d["Local accounts"] = "Konta lokalne";
        d["Announcement window"] = "Okno ogłoszenia";
        d["About and updates"] = "Informacje i aktualizacje";

        // Settings: General
        d["Language"] = "Język";
        d["The language of the Console for every admin, and of the tray and announcement windows on every PC. Agents switch as soon as they are told; the Console reloads."] =
            "Język konsoli dla wszystkich administratorów oraz ikony w zasobniku i okien ogłoszeń na wszystkich komputerach. Agenci przełączają się, gdy tylko otrzymają zmianę; konsola zostanie przeładowana.";

        // Settings: Enrolment
        d["Install password"] = "Hasło instalacyjne";
        d["New computers send this password once when they enrol. Already enrolled computers are not affected when you rotate it."] =
            "Nowe komputery wysyłają to hasło jeden raz podczas rejestracji. Zmiana hasła nie wpływa na już zarejestrowane komputery.";
        d["Required to enrol a computer"] = "Wymagane do zarejestrowania komputera";
        d["Not required: anyone who can reach the agent port can enrol"] =
            "Niewymagane: każdy, kto ma dostęp do portu agenta, może zarejestrować komputer";
        d["Password set (in the Console)"] = "Hasło ustawione (w konsoli)";
        d["Password set (in the config file)"] = "Hasło ustawione (w pliku konfiguracyjnym)";
        d["No password set"] = "Hasło nie jest ustawione";
        d["Rotate password"] = "Zmień hasło";
        d["Set password"] = "Ustaw hasło";
        d["A password is required but none is set, so no computer can enrol."] =
            "Hasło jest wymagane, ale nie zostało ustawione, więc żaden komputer nie może się zarejestrować.";
        d["Computers awaiting approval receive"] = "Komputery oczekujące na zatwierdzenie otrzymują";
        d["Everyone and User announcements (default)"] = "Ogłoszenia dla wszystkich i dla użytkowników (domyślnie)";
        d["Nothing until approved"] = "Nic do czasu zatwierdzenia";
        d["They never receive Computer, Computer group or OU announcements before approval."] =
            "Przed zatwierdzeniem nigdy nie otrzymują ogłoszeń skierowanych do komputerów, grup komputerów ani jednostek OU.";
        d["Turn off the install password"] = "Wyłączenie hasła instalacyjnego";
        d["Anyone who can reach the agent port will be able to enrol a computer. Continue?"] =
            "Każdy, kto ma dostęp do portu agenta, będzie mógł zarejestrować komputer. Kontynuować?";
        d["Turn off"] = "Wyłącz";
        d["Rotate install password"] = "Zmiana hasła instalacyjnego";
        d["Set install password"] = "Ustawienie hasła instalacyjnego";
        d["Only a hash is stored. Enrolled computers are not affected."] =
            "Przechowywany jest tylko skrót hasła. Nie wpływa to na zarejestrowane komputery.";
        d["Install password saved."] = "Hasło instalacyjne zostało zapisane.";

        // Settings: Agent install
        d["Update agents automatically"] = "Aktualizuj agentów automatycznie";
        d["When on, every agent older than this server is updated as soon as it is online, using the cached agent package (see below). A failed automatic update is retried after 24 hours. When off, update agents from Computers or the Dashboard."] =
            "Gdy ta opcja jest włączona, każdy agent starszy niż ten serwer jest aktualizowany, gdy tylko będzie online, z użyciem pakietu agenta z pamięci podręcznej (poniżej). Nieudana aktualizacja automatyczna jest ponawiana po 24 godzinach. Gdy jest wyłączona, aktualizuj agentów w widoku Komputery lub na Pulpicie.";
        d["Agents are updated automatically."] = "Agenci są aktualizowani automatycznie.";
        d["Automatic agent updates are off."] = "Automatyczne aktualizacje agentów są wyłączone.";
        d["Server key fingerprint"] = "Odcisk klucza serwera";
        d["Install command"] = "Polecenie instalacji";
        d["Run on the computer as an administrator (or deploy with these MSI properties)."] =
            "Uruchom na komputerze jako administrator (lub wdróż z tymi właściwościami MSI).";
        d["Copy"] = "Kopiuj";
        d["Copy the install command"] = "Kopiuj polecenie instalacji";
        d["Copied."] = "Skopiowano.";
        d["Copy failed; select the text instead."] = "Nie udało się skopiować; zaznacz tekst ręcznie.";
        d["<install password>"] = "<hasło instalacyjne>";
        d["Replace <install password> with the install password. The MSI keeps it hidden from its log."] =
            "Zastąp <hasło instalacyjne> hasłem instalacyjnym. Instalator MSI nie zapisuje go w swoim dzienniku.";
        d["Agent package (MSI) for version {0}"] = "Pakiet agenta (MSI) dla wersji {0}";
        d["No agent MSI is cached for this server version."] = "Brak pakietu MSI agenta w pamięci podręcznej dla tej wersji serwera.";
        d["It is downloaded from GitHub on \"Check now\" (About and updates)."] =
            "Zostanie pobrany z GitHub po kliknięciu „Sprawdź teraz” (Informacje i aktualizacje).";
        d["It is downloaded from the GitHub release, which needs Updates:GitHubRepository in the config file."] =
            "Jest pobierany z wydania w GitHub, co wymaga ustawienia Updates:GitHubRepository w pliku konfiguracyjnym.";
        d["Download {0}"] = "Pobierz {0}";
        d["Version"] = "Wersja";
        d["Size"] = "Rozmiar";
        d["Source"] = "Źródło";
        d["Added"] = "Dodano";

        // Settings: History
        d["Retention"] = "Przechowywanie";
        d["Name, user and IP history is kept for <b>{0} days</b> after a row is closed."] =
            "Historia nazw, użytkowników i adresów IP jest przechowywana przez <b>{0} dni</b> od zamknięcia wpisu.";
        d["Set with History:RetentionDays in the config file. Current rows, announcements, deliveries, confirmations and answers are never pruned."] =
            "Ustawiane przez History:RetentionDays w pliku konfiguracyjnym. Bieżące wpisy, ogłoszenia, dostarczenia, potwierdzenia i odpowiedzi nigdy nie są usuwane.";

        // Settings: Local accounts
        d["Local sign-in is turned off (LocalAccounts:Enabled). Accounts can be managed but not used."] =
            "Logowanie lokalne jest wyłączone (LocalAccounts:Enabled). Kontami można zarządzać, ale nie można ich używać.";
        d["Backup accounts with full admin rights; they work when the directory is unreachable."] =
            "Konta zapasowe z pełnymi uprawnieniami administratora; działają, gdy katalog jest niedostępny.";
        d["Add account"] = "Dodaj konto";
        d["State"] = "Stan";
        d["Created"] = "Utworzono";
        d["Last sign-in"] = "Ostatnie logowanie";
        d["Password changed"] = "Zmiana hasła";
        d["No local accounts."] = "Brak kont lokalnych.";
        d["Disabled"] = "Wyłączone";
        d["{0} by {1}"] = "{0}, przez {1}";
        d["Reset password"] = "Resetuj hasło";
        d["Enable"] = "Włącz";
        d["Disable"] = "Wyłącz";
        d["Add local account"] = "Dodaj konto lokalne";
        d["Password for {0}"] = "Hasło dla {0}";
        d["Account {0} created."] = "Utworzono konto {0}.";
        d["Reset password of {0}"] = "Resetowanie hasła: {0}";
        d["New password"] = "Nowe hasło";
        d["Reset"] = "Resetuj";
        d["Password reset."] = "Hasło zostało zresetowane.";
        d["Disable account"] = "Wyłączenie konta";
        d["Disable {0}? Open sessions of this account end within a few minutes."] =
            "Wyłączyć konto {0}? Otwarte sesje tego konta wygasną w ciągu kilku minut.";

        // Settings: Announcement window
        d["How announcements look on the PCs. Changes apply to announcements shown from now on."] =
            "Wygląd ogłoszeń na komputerach. Zmiany dotyczą ogłoszeń wyświetlanych od teraz.";
        d["Text to the right of the title"] = "Tekst na prawo od tytułu";
        d["For example your organisation's name. Leave empty for none. Default: {0}."] =
            "Na przykład nazwa organizacji. Pozostaw puste, aby nic nie wyświetlać. Domyślnie: {0}.";
        d["Example title"] = "Przykładowy tytuł";
        d["The message of the announcement."] = "Treść ogłoszenia.";

        // Settings: About and updates
        d["Updates are not configured (Updates:GitHubRepository is blank). No update checks are made."] =
            "Aktualizacje nie są skonfigurowane (Updates:GitHubRepository jest puste). Dostępność aktualizacji nie jest sprawdzana.";
        d["Current version"] = "Bieżąca wersja";
        d["Repository"] = "Repozytorium";
        d["Latest release"] = "Najnowsze wydanie";
        d["(prerelease)"] = "(wersja przedpremierowa)";
        d["Release page"] = "Strona wydania";
        d["Last check"] = "Ostatnie sprawdzenie";
        d["Self-update"] = "Samoaktualizacja";
        d["Supported on this server"] = "Obsługiwana na tym serwerze";
        d["Not supported here (needs the Ubuntu install made by install.sh)"] =
            "Nieobsługiwana (wymaga instalacji na Ubuntu wykonanej przez install.sh)";
        d["Last update"] = "Ostatnia aktualizacja";
        d["Downloading"] = "Pobieranie";
        d["Staged"] = "Przygotowana";
        d["Applying"] = "Instalowanie";
        d["Succeeded"] = "Zakończona powodzeniem";
        d["Rolled back"] = "Wycofana";
        d["Failed"] = "Niepowodzenie";
        d["Version {0} is available."] = "Dostępna jest wersja {0}.";
        d["Check now"] = "Sprawdź teraz";
        d["Update now"] = "Aktualizuj teraz";
        d["Release notes"] = "Informacje o wydaniu";
        d["Checked."] = "Sprawdzono.";
        d["Check failed: {0}"] = "Sprawdzanie nie powiodło się: {0}";
        d["Update the server"] = "Aktualizacja serwera";
        d["Download and install version {0}? The server restarts; a database backup is made first and a failed update is rolled back."] =
            "Pobrać i zainstalować wersję {0}? Serwer zostanie uruchomiony ponownie; najpierw tworzona jest kopia zapasowa bazy danych, a nieudana aktualizacja jest wycofywana.";
        d["The update was staged. The server restarts shortly."] = "Aktualizacja została przygotowana. Serwer wkrótce uruchomi się ponownie.";
        d["The update could not be started."] = "Nie udało się rozpocząć aktualizacji.";
    }
}
