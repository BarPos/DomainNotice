using DomainNotice.Shared;

namespace DomainNotice.Console.Services;

internal static partial class ConsoleStrings
{
    /// <summary>
    /// Fixed messages from server services (DomainNotice.Server.Core) shown in the Console. Keys must match the
    /// English text exactly, including the numbers of the length limits. Messages with variable parts are not listed.
    /// </summary>
    static partial void Services(Dictionary<string, string> d)
    {
        // Announcements/AnnouncementService (Validate, save, delete)
        d["Name is required."] = "Nazwa jest wymagana.";
        d["Name must be 200 characters or fewer."] = "Nazwa może mieć najwyżej 200 znaków.";
        d["Title must be 200 characters or fewer."] = "Tytuł może mieć najwyżej 200 znaków.";
        d["Minimum time to dismiss cannot be negative."] = "Minimalny czas do zamknięcia nie może być ujemny.";
        d["Number of times shown must be at least 1."] = "Liczba wyświetleń musi wynosić co najmniej 1.";
        d["Repeat interval cannot be negative."] = "Odstęp powtórzeń nie może być ujemny.";
        d["End time must be after the start time."] = "Czas zakończenia musi być późniejszy niż czas rozpoczęcia.";
        d["Add at least one button."] = "Dodaj co najmniej jeden przycisk.";
        d["A button is listed twice."] = "Przycisk występuje dwukrotnie.";
        d["A question is listed twice."] = "Pytanie występuje dwukrotnie.";
        d["That announcement no longer exists."] = "To ogłoszenie już nie istnieje.";
        d["Anonymous answers cannot be switched on or off after the announcement was sent."] =
            "Anonimowych odpowiedzi nie można włączyć ani wyłączyć po wysłaniu ogłoszenia.";

        // Announcements/AnnouncementWindowSettings
        d["The text must be 60 characters or fewer."] = "Tekst może mieć najwyżej 60 znaków.";

        // Common/AppLanguage
        d["Unsupported language."] = "Nieobsługiwany język.";

        // Accounts/LocalAccountService
        d["Enter a user name."] = "Wpisz nazwę użytkownika.";
        d["The user name can have at most 128 characters."] = "Nazwa użytkownika może mieć najwyżej 128 znaków.";
        d["The user name contains invalid characters."] = "Nazwa użytkownika zawiera niedozwolone znaki.";
        d["Use a password of at least 8 characters."] = "Użyj hasła o długości co najmniej 8 znaków.";
        d["The password can have at most 1024 characters."] = "Hasło może mieć najwyżej 1024 znaki.";
        d["A directory is configured; sign in with a domain account and add local accounts in Settings."] =
            "Katalog jest skonfigurowany; zaloguj się kontem domenowym i dodaj konta lokalne w Ustawieniach.";
        d["Setup is already complete. Sign in instead."] = "Konfiguracja została już ukończona. Zaloguj się.";
        d["A local account with this name already exists."] = "Konto lokalne o tej nazwie już istnieje.";
        d["The local account was not found."] = "Nie znaleziono konta lokalnego.";
        d["This is the last enabled local account and no directory is configured; disabling it would lock everyone out."] =
            "To ostatnie włączone konto lokalne, a katalog nie jest skonfigurowany; jego wyłączenie zablokowałoby dostęp wszystkim.";

        // Accounts/AdminSignInService and Directory/DomainAuthenticator (sign-in results)
        d["Sign-in failed. Check the user name and password."] = "Logowanie nie powiodło się. Sprawdź nazwę użytkownika i hasło.";
        d["Sign-in failed. Check the account type, user name and password."] = "Logowanie nie powiodło się. Sprawdź typ konta, nazwę użytkownika i hasło.";
        d["Too many failed sign-ins. Wait a few minutes and try again."] = "Zbyt wiele nieudanych logowań. Odczekaj kilka minut i spróbuj ponownie.";
        d["Local sign-in is turned off (LocalAccounts:Enabled)."] = "Logowanie lokalne jest wyłączone (LocalAccounts:Enabled).";
        d["Domain sign-in is not available. Use a local account."] = "Logowanie domenowe jest niedostępne. Użyj konta lokalnego.";
        d["The directory could not be reached."] = "Nie udało się połączyć z katalogiem.";
        d["The domain could not be reached. Try again, or use a local account."] = "Nie udało się połączyć z domeną. Spróbuj ponownie albo użyj konta lokalnego.";
        d["Your account is not allowed to use the DomainNotice Console."] = "Twoje konto nie ma dostępu do konsoli DomainNotice.";
        d["Domain sign-in is not configured."] = "Logowanie domenowe nie jest skonfigurowane.";
        d["Enter your user name and password."] = "Wpisz nazwę użytkownika i hasło.";
        d["The user name or password is incorrect."] = "Nieprawidłowa nazwa użytkownika lub hasło.";
        d["Your account was not found under the configured directory base."] = "Nie znaleziono Twojego konta w skonfigurowanej bazie katalogu.";
        d["Your account is not in a DomainNotice admin or viewer group."] = "Twoje konto nie należy do grupy administratorów ani obserwatorów DomainNotice.";

        // AgentLink/ComputerAdminService and AdLinkService (via ComputerAdminService)
        d["Computer not found."] = "Nie znaleziono komputera.";
        d["The computer is already revoked."] = "Komputer jest już odwołany.";
        d["This computer is not awaiting approval."] = "Ten komputer nie oczekuje na zatwierdzenie.";
        d["This computer is revoked or replaced."] = "Ten komputer jest odwołany lub zastąpiony.";

        // AgentLink/Enrolment (install password, Settings)
        d["The install password must have at least 8 characters."] = "Hasło instalacji musi mieć co najmniej 8 znaków.";

        // Directory/DirectoryAdmin
        d["Cancelled."] = "Anulowano.";
        d["Enter the password."] = "Wpisz hasło.";
        d["The directory is not configured (Directory:Hosts and Directory:BaseDn)."] = "Katalog nie jest skonfigurowany (Directory:Hosts i Directory:BaseDn).";

        // Updates/AgentUpdateService
        d[$"The agent package for version {AppVersion.Current} is not in the cache. It is downloaded from the GitHub release on \"Check now\" (Settings, About and updates)."] =
            $"Pakietu agenta dla wersji {AppVersion.Current} nie ma w pamięci podręcznej. Zostanie pobrany z wydania w GitHub po kliknięciu „Sprawdź teraz” (Ustawienia, Informacje i aktualizacje).";
        d["Update task not found."] = "Nie znaleziono zadania aktualizacji.";
        d["The task is already Done."] = "Zadanie jest już zakończone.";
        d["The task is already Failed."] = "Zadanie już się nie powiodło.";
        d["The task is already Cancelled."] = "Zadanie jest już anulowane.";
        d["The agent reported a failure."] = "Agent zgłosił błąd.";

        // Updates/AgentPackageCache
        d["Updates are not configured."] = "Aktualizacje nie są skonfigurowane.";

        // Updates/ServerUpdateService and ServerInstallation
        d["Self-update is not supported on this server."] = "Samoaktualizacja nie jest obsługiwana na tym serwerze.";
        d["Unsupported CPU architecture."] = "Nieobsługiwana architektura procesora.";
        d["An update is already being prepared."] = "Aktualizacja jest już przygotowywana.";
        d["An update is already in progress."] = "Aktualizacja już trwa.";
        d["No release found."] = "Nie znaleziono wydania.";
        d["Running the new version."] = "Działa nowa wersja.";
        d["The download was interrupted."] = "Pobieranie zostało przerwane.";
        d["Self-update works only on Ubuntu Linux (installed with install.sh)."] = "Samoaktualizacja działa tylko w systemie Ubuntu Linux (instalacja przez install.sh).";
        d["Self-update needs Ubuntu 24.04 LTS or newer."] = "Samoaktualizacja wymaga systemu Ubuntu 24.04 LTS lub nowszego.";
        d["The domainnotice-update.path unit is not installed."] = "Jednostka domainnotice-update.path nie jest zainstalowana.";
    }
}
