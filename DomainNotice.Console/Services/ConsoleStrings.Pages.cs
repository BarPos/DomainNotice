namespace DomainNotice.Console.Services;

internal static partial class ConsoleStrings
{
    /// <summary>Dashboard, computers, results, directory, history, updates, search, audit pages; endpoint and auth messages.</summary>
    static partial void Pages(Dictionary<string, string> d)
    {
        // Shared words used on these pages (also in Common; same wording).
        d["Delete"] = "Usuń";
        d["Edit"] = "Edytuj";
        d["Cancel"] = "Anuluj";
        d["Save"] = "Zapisz";
        d["Search"] = "Szukaj";
        d["Reload"] = "Odśwież";
        d["Details"] = "Szczegóły";
        d["Name"] = "Nazwa";
        d["Status"] = "Status";
        d["State"] = "Stan";
        d["None"] = "Brak";
        d["Never"] = "Nigdy";
        d["Failed."] = "Nie powiodło się.";
        d["Computer"] = "Komputer";
        d["Computers"] = "Komputery";
        d["Announcement"] = "Ogłoszenie";
        d["Announcements"] = "Ogłoszenia";
        d["Confirmations"] = "Potwierdzenia";
        d["Answers"] = "Odpowiedzi";
        d["Directory"] = "Katalog";
        d["History"] = "Historia";
        d["Users"] = "Użytkownicy";
        d["Everyone"] = "Wszyscy";
        d["{0} and {1} more"] = "{0} i {1} więcej";
        d["Last error: {0}"] = "Ostatni błąd: {0}";
        d["Sync running…"] = "Synchronizacja w toku…";

        // Dashboard
        d["Dashboard"] = "Pulpit";
        d["online"] = "online";
        d["offline"] = "offline";
        d["Outdated agents"] = "Nieaktualne agenty";
        d["older than the server ({0})"] = "starsze niż serwer ({0})";
        d["Update all"] = "Aktualizuj wszystkie";
        d["Awaiting approval"] = "Oczekujące na zatwierdzenie";
        d["re-imaged or duplicate computers to decide"] = "przeinstalowane lub zduplikowane komputery do rozstrzygnięcia";
        d["Directory sync"] = "Synchronizacja katalogu";
        d["No directory is configured."] = "Nie skonfigurowano katalogu.";
        d["{0} objects"] = "Obiekty: {0}";
        d["Active announcements"] = "Aktywne ogłoszenia";
        d["All active"] = "Wszystkie aktywne";
        d["No announcement is active right now."] = "Żadne ogłoszenie nie jest teraz aktywne.";
        d["{0} of {1} users confirmed · ends {2}"] = "Potwierdziło {0} z {1} użytkowników · koniec {2}";
        d["Update all outdated agents"] = "Aktualizacja wszystkich nieaktualnych agentów";
        d["Send the update command to {0} outdated agents? They update to version {1}."] =
            "Wysłać polecenie aktualizacji do nieaktualnych agentów (liczba: {0})? Zostaną zaktualizowane do wersji {1}.";
        d["{0} update task(s) created."] = "Utworzono zadania aktualizacji: {0}.";
        d["The update could not be started."] = "Nie udało się rozpocząć aktualizacji.";

        // Computers
        d["{0} awaiting approval"] = "Oczekujące na zatwierdzenie: {0}";
        d["All"] = "Wszystkie";
        d["Awaiting approval ({0})"] = "Oczekujące na zatwierdzenie ({0})";
        d["Not in directory"] = "Poza katalogiem";
        d["Offline more than 7 days"] = "Offline ponad 7 dni";
        d["Revoked"] = "Odwołany";
        d["Agent older than the server"] = "Agent starszy niż serwer";
        d["replaced"] = "zastąpiony";
        d["Proven"] = "Zweryfikowany";
        d["Matched by name"] = "Dopasowany po nazwie";
        d["Kept separate"] = "Zachowany osobno";
        d["Update agent"] = "Aktualizuj agenta";
        d["Send announcement to selected"] = "Wyślij ogłoszenie do zaznaczonych";
        d["Send announcement"] = "Wyślij ogłoszenie";
        d["Revoke"] = "Odwołaj";
        d["{0} update task(s) created. Up-to-date computers and computers with an open task were skipped."] =
            "Utworzono zadania aktualizacji: {0}. Pominięto komputery aktualne i komputery z otwartym zadaniem.";
        d["Revoke computers"] = "Odwołanie komputerów";
        d["Revoke {0}? Their keys are refused from now on; to come back they must enrol again."] =
            "Odwołać {0}? Ich klucze będą od teraz odrzucane; aby wrócić, muszą zarejestrować się ponownie.";
        d["{0} computer(s) revoked."] = "Odwołane komputery: {0}.";
        d["Delete computers"] = "Usuwanie komputerów";
        d["Delete {0} and their history? A computer that is not revoked can enrol again."] =
            "Usunąć {0} wraz z historią? Komputer, który nie został odwołany, może zarejestrować się ponownie.";
        d["{0} computer(s) deleted."] = "Usunięte komputery: {0}.";

        // Computer details
        d["This computer does not exist (it may have been deleted)."] = "Ten komputer nie istnieje (mógł zostać usunięty).";
        d["Back to computers"] = "Powrót do komputerów";
        d["Replaced by"] = "Zastąpiony przez";
        d["newer record"] = "nowszy rekord";
        d["History includes {0} replaced record(s) of this machine."] = "Historia obejmuje zastąpione rekordy tej maszyny: {0}.";
        d["Overview"] = "Przegląd";
        d["DNS name"] = "Nazwa DNS";
        d["Remote IP"] = "Zdalny IP";
        d["Local IPs"] = "Lokalne IP";
        d["Current users"] = "Bieżący użytkownicy";
        d["({0}, since {1})"] = "({0}, od {1})";
        d["active"] = "aktywna";
        d["disconnected"] = "rozłączona";
        d["locked"] = "zablokowana";
        d["Agent version"] = "Wersja agenta";
        d["outdated"] = "nieaktualny";
        d["OS"] = "System";
        d["Last seen"] = "Ostatnio widziany";
        d["Enrolled"] = "Zarejestrowano";
        d["AD link"] = "Powiązanie z AD";
        d["linked {0}"] = "powiązano {0}";
        d["Decide in Awaiting approval"] = "Zdecyduj w sekcji Oczekujące na zatwierdzenie";
        d["Key fingerprint"] = "Odcisk klucza";
        d["{0} by {1}"] = "{0}, przez {1}";
        d["Name history"] = "Historia nazw";
        d["User history"] = "Historia użytkowników";
        d["IP history"] = "Historia IP";
        d["Deliveries"] = "Doręczenia";
        d["Update task created."] = "Utworzono zadanie aktualizacji.";
        d["No task created (up to date or a task is already open)."] = "Nie utworzono zadania (agent jest aktualny lub zadanie jest już otwarte).";
        d["Revoke computer"] = "Odwołanie komputera";
        d["Revoke {0}? Its key is refused from now on."] = "Odwołać {0}? Jego klucz będzie od teraz odrzucany.";
        d["Computer revoked."] = "Komputer odwołany.";
        d["Delete computer"] = "Usuwanie komputera";
        d["Delete {0} and its history?"] = "Usunąć {0} wraz z historią?";
        d["Computer deleted."] = "Komputer usunięty.";

        // Awaiting approval
        d["These computers claim the name of a computer that is already linked to another enrolled key, and have no Kerberos proof. Until you decide, they receive only Everyone and User announcements (or nothing, see Settings)."] =
            "Te komputery podają nazwę komputera, który jest już powiązany z innym zarejestrowanym kluczem, i nie mają dowodu Kerberos. Dopóki nie zdecydujesz, otrzymują tylko ogłoszenia dla wszystkich i dla użytkowników (albo nic, zobacz Ustawienia).";
        d["Nothing is waiting for approval."] = "Nic nie czeka na zatwierdzenie.";
        d["Current record (holds the AD link)"] = "Obecny rekord (ma powiązanie z AD)";
        d["The record holding the link no longer exists."] = "Rekord z powiązaniem już nie istnieje.";
        d["New record (awaiting approval)"] = "Nowy rekord (oczekuje na zatwierdzenie)";
        d["Replace the old computer"] = "Zastąp stary komputer";
        d["Keep both"] = "Zachowaj oba";
        d["Revoke the new key"] = "Odwołaj nowy klucz";
        d["Move the AD link of {0} to the new key? The old record is marked replaced and its history is shown on the new one."] =
            "Przenieść powiązanie z AD komputera {0} na nowy klucz? Stary rekord zostanie oznaczony jako zastąpiony, a jego historia będzie widoczna w nowym.";
        d["Replace"] = "Zastąp";
        d["The new record replaced the old one."] = "Nowy rekord zastąpił stary.";
        d["Keep the new {0} as a separate computer, not linked to AD?"] = "Zachować nowy {0} jako osobny komputer, bez powiązania z AD?";
        d["Both records are kept."] = "Zachowano oba rekordy.";
        d["Revoke the new {0}? Its key is refused from now on."] = "Odwołać nowy {0}? Jego klucz będzie od teraz odrzucany.";
        d["The new key was revoked."] = "Nowy klucz został odwołany.";
        d["The action failed."] = "Akcja nie powiodła się.";

        // Announcements and results
        d["New announcement"] = "Nowe ogłoszenie";
        d["Results"] = "Wyniki";
        d["Delete announcements"] = "Usuwanie ogłoszeń";
        d["Delete {0} with all deliveries, confirmations and answers? Open windows on the computers are withdrawn."] =
            "Usunąć {0} wraz ze wszystkimi doręczeniami, potwierdzeniami i odpowiedziami? Otwarte okna na komputerach zostaną wycofane.";
        d["{0} announcement(s) deleted."] = "Usunięte ogłoszenia: {0}.";
        d["This announcement does not exist."] = "To ogłoszenie nie istnieje.";
        d["Back to announcements"] = "Powrót do ogłoszeń";
        d["Anonymous answers"] = "Anonimowe odpowiedzi";
        d["Confirmations CSV"] = "Potwierdzenia CSV";
        d["Answers CSV"] = "Odpowiedzi CSV";
        d["Window"] = "Okres";
        d["Targets"] = "Odbiorcy";
        d["Delivered to"] = "Doręczono do";
        d["{0} users"] = "Użytkownicy: {0}";
        d["Confirmed by"] = "Potwierdzili";
        d["Confirmation rate"] = "Odsetek potwierdzeń";
        d["This announcement has no questions."] = "To ogłoszenie nie ma pytań.";
        d["(hidden)"] = "(ukryte)";
        d["{0} answer(s)"] = "Odpowiedzi: {0}";
        d["multiple choice"] = "wielokrotny wybór";
        d["single choice"] = "jednokrotny wybór";
        d["Anonymous answers are stored without user, computer or time and listed by content only."] =
            "Anonimowe odpowiedzi są zapisywane bez użytkownika, komputera i czasu, a listowane tylko według treści.";
        d["{0} (removed)"] = "{0} (usunięta)";
        d["Delete announcement"] = "Usuwanie ogłoszenia";
        d["Delete \"{0}\" with all deliveries, confirmations and answers? Open windows are withdrawn."] =
            "Usunąć „{0}” wraz ze wszystkimi doręczeniami, potwierdzeniami i odpowiedziami? Otwarte okna zostaną wycofane.";
        d["Announcement deleted."] = "Ogłoszenie usunięte.";
        d["The announcement could not be deleted."] = "Nie udało się usunąć ogłoszenia.";

        // Confirmations, answers, history, audit
        d["Read confirmations"] = "Potwierdzenia przeczytania";
        d["Named answers"] = "Odpowiedzi imienne";
        d["Anonymous answers have no user, computer or time. They can only be filtered by announcement, question and option, and are listed by content, never in the order they were stored."] =
            "Anonimowe odpowiedzi nie mają użytkownika, komputera ani czasu. Można je filtrować tylko według ogłoszenia, pytania i opcji, a są listowane według treści, nigdy w kolejności zapisu.";
        d["Kept for {0} days after a row is closed; open rows are never pruned."] =
            "Przechowywana przez {0} dni od zamknięcia wiersza; otwarte wiersze nigdy nie są usuwane.";
        d["Audit log"] = "Dziennik audytu";
        d["Who changed what in the Console."] = "Kto i co zmienił w konsoli.";

        // Agent updates
        d["Agent updates"] = "Aktualizacje agentów";
        d["Agents always update to the server's version ({0})."] = "Agenty zawsze aktualizują się do wersji serwera ({0}).";
        d["Update all outdated"] = "Aktualizuj wszystkie nieaktualne";
        d["Cancel task"] = "Anuluj zadanie";
        d["Cancel update tasks"] = "Anulowanie zadań aktualizacji";
        d["Cancel {0} open update task(s)?"] = "Anulować otwarte zadania aktualizacji (liczba: {0})?";
        d["Cancel tasks"] = "Anuluj zadania";
        d["{0} task(s) cancelled."] = "Anulowane zadania: {0}.";
        d["Send the update command to every outdated agent (to {0})?"] = "Wysłać polecenie aktualizacji do każdego nieaktualnego agenta (do wersji {0})?";

        // Directory
        d["Sync now"] = "Synchronizuj teraz";
        d["Full sync"] = "Pełna synchronizacja";
        d["Test connection"] = "Testuj połączenie";
        d["Set bind password"] = "Ustaw hasło konta wiązania";
        d["No directory is configured (Directory:Hosts and Directory:BaseDn in the config file). Sign-in uses local accounts, and announcements can target typed user and computer names only."] =
            "Nie skonfigurowano katalogu (Directory:Hosts i Directory:BaseDn w pliku konfiguracyjnym). Logowanie używa kont lokalnych, a ogłoszenia mogą być kierowane tylko do wpisanych nazw użytkowników i komputerów.";
        d["Domain controllers"] = "Kontrolery domeny";
        d["{0} (port {1}, {2})"] = "{0} (port {1}, {2})";
        d["Base DN"] = "Bazowy DN";
        d["Bind password from"] = "Źródło hasła konta wiązania";
        d["Last successful sync"] = "Ostatnia udana synchronizacja";
        d["Last attempt"] = "Ostatnia próba";
        d["Objects"] = "Obiekty";
        d["Idle"] = "Bezczynna";
        d["Nothing synced yet."] = "Jeszcze nic nie zsynchronizowano.";
        d["No agent"] = "Brak agenta";
        d["Full sync requested."] = "Zlecono pełną synchronizację.";
        d["Incremental sync requested."] = "Zlecono synchronizację przyrostową.";
        d["The service account could bind to the directory."] = "Konto usługi połączyło się z katalogiem.";
        d["Connection failed: {0}"] = "Połączenie nie powiodło się: {0}";
        d["It is stored encrypted in the database."] = "Jest przechowywane w bazie danych w postaci zaszyfrowanej.";
        d["Note: Directory:BindPasswordSource is \"{0}\"; set it to \"Database\" for this password to be used."] =
            "Uwaga: Directory:BindPasswordSource ma wartość \"{0}\"; ustaw \"Database\", aby to hasło było używane.";
        d["Bind password"] = "Hasło konta wiązania";
        d["Password of the bind account {0}. {1}"] = "Hasło konta wiązania {0}. {1}";
        d["Bind password saved."] = "Zapisano hasło konta wiązania.";

        // Search
        d["Search results for \"{0}\""] = "Wyniki wyszukiwania dla „{0}”";
        d["Type in the search box at the top."] = "Wpisz tekst w polu wyszukiwania u góry.";
        d["Open in list"] = "Otwórz na liście";
        d["No match."] = "Brak wyników.";
        d["logged on at {0}"] = "zalogowany na {0}";

        // Endpoints (setup, MSI upload) and access checks
        d["The passwords do not match."] = "Hasła nie są zgodne.";
        d["The account could not be created."] = "Nie udało się utworzyć konta.";
        d["No agent package is cached for this server version."] = "Brak pakietu agenta w pamięci podręcznej dla tej wersji serwera.";
        d["The file is larger than {0} MB."] = "Plik jest większy niż {0} MB.";
        d["Choose an MSI file."] = "Wybierz plik MSI.";
        d["Only .msi files can be imported."] = "Można importować tylko pliki .msi.";
        d["The file is too large."] = "Plik jest za duży.";
        d["Imported {0} for version {1}."] = "Zaimportowano {0} dla wersji {1}.";
        d["The package could not be imported."] = "Nie udało się zaimportować pakietu.";
        d["Read-only access: this action needs the Admin role."] = "Dostęp tylko do odczytu: ta akcja wymaga roli Admin.";
    }
}
