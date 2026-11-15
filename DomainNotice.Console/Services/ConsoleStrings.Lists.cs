namespace DomainNotice.Console.Services;

internal static partial class ConsoleStrings
{
    /// <summary>
    /// Column, field and option labels of the lists (ConsoleLists, English in code), enum options humanized by
    /// GridField.Humanize ("MatchedByName" → "Matched by name") and raw enum names shown in cells.
    /// </summary>
    static partial void Lists(Dictionary<string, string> d)
    {
        // Columns and fields
        d["Status"] = "Status";
        d["Name"] = "Nazwa";
        d["Remote IP"] = "Zdalny IP";
        d["Local IPs"] = "Lokalne IP";
        d["IP (remote or local)"] = "IP (zdalny lub lokalny)";
        d["IP"] = "IP";
        d["Current users"] = "Bieżący użytkownicy";
        d["Current user"] = "Bieżący użytkownik";
        d["Last seen"] = "Ostatnio widziany";
        d["First seen"] = "Pierwszy raz widziany";
        d["Agent version"] = "Wersja agenta";
        d["OS"] = "System";
        d["OU"] = "OU";
        d["AD link"] = "Powiązanie z AD";
        d["AD link state"] = "Stan powiązania z AD";
        d["In directory"] = "W katalogu";
        d["Replaced"] = "Zastąpiony";
        d["Revoked"] = "Odwołany";
        d["Enrolled"] = "Zarejestrowano";
        d["Computer"] = "Komputer";
        d["Computer (current name)"] = "Komputer (bieżąca nazwa)";
        d["Current"] = "Aktualny";
        d["User"] = "Użytkownik";
        d["Logon"] = "Logowanie";
        d["Logoff"] = "Wylogowanie";
        d["Still logged on"] = "Nadal zalogowany";
        d["Session"] = "Sesja";
        d["Kind"] = "Rodzaj";
        d["Address"] = "Adres";
        d["Start"] = "Początek";
        d["End"] = "Koniec";
        d["Target"] = "Odbiorca";
        d["Title on the PCs"] = "Tytuł na komputerach";
        d["Anonymous"] = "Anonimowe";
        d["Anonymous answers"] = "Anonimowe odpowiedzi";
        d["Confirmation rate"] = "Odsetek potwierdzeń";
        d["Confirmation rate (%)"] = "Odsetek potwierdzeń (%)";
        d["Confirmed / delivered"] = "Potwierdzone / doręczone";
        d["Last change"] = "Ostatnia zmiana";
        d["Changed by"] = "Zmienione przez";
        d["Announcement"] = "Ogłoszenie";
        d["Button"] = "Przycisk";
        d["Time"] = "Czas";
        d["Sent"] = "Wysłano";
        d["Shown"] = "Wyświetlono";
        d["Confirmed"] = "Potwierdzono";
        d["Withdrawn"] = "Wycofano";
        d["Question"] = "Pytanie";
        d["Option"] = "Opcja";
        d["From"] = "Z wersji";
        d["To"] = "Do wersji";
        d["From version"] = "Z wersji";
        d["To version"] = "Do wersji";
        d["State"] = "Stan";
        d["Requested by"] = "Zlecone przez";
        d["Requested"] = "Zlecono";
        d["Finished"] = "Zakończono";
        d["Bytes sent"] = "Wysłane bajty";
        d["Error"] = "Błąd";
        d["Who"] = "Kto";
        d["Action"] = "Akcja";
        d["Target type"] = "Typ obiektu";
        d["Details"] = "Szczegóły";
        d["Account name"] = "Nazwa konta";
        d["Display name"] = "Nazwa wyświetlana";
        d["Distinguished name"] = "Nazwa wyróżniająca (DN)";
        d["Enabled"] = "Włączone";
        d["Gone from AD"] = "Usunięty z AD";
        d["Last synced"] = "Ostatnia synchronizacja";
        d["Agent"] = "Agent";
        d["Has an enrolled agent"] = "Ma zarejestrowanego agenta";
        d["No agent"] = "Brak agenta";

        // Yes/no options of bool fields
        d["Yes"] = "Tak";
        d["No"] = "Nie";
        d["Online"] = "Online";
        d["Offline"] = "Offline";
        d["In AD"] = "W AD";
        d["Not in AD"] = "Poza AD";

        // AdLinkState
        d["None"] = "Brak";
        d["Matched by name"] = "Dopasowany po nazwie";
        d["Proven"] = "Zweryfikowany";
        d["Awaiting approval"] = "Oczekujące na zatwierdzenie";
        d["Kept separate"] = "Zachowany osobno";

        // AnnouncementStatus (raw names)
        d["Draft"] = "Szkic";
        d["Scheduled"] = "Zaplanowane";
        d["Active"] = "Aktywne";
        d["Ended"] = "Zakończone";

        // AgentUpdateState (single words: humanized = raw)
        d["Pending"] = "Oczekuje";
        d["Downloading"] = "Pobieranie";
        d["Verifying"] = "Weryfikacja";
        d["Installing"] = "Instalacja";
        d["Done"] = "Gotowe";
        d["Failed"] = "Niepowodzenie";
        d["Cancelled"] = "Anulowano";

        // IpAddressKind
        d["Remote"] = "Zdalny";
        d["Local"] = "Lokalny";

        // DirectoryObjectKind and TargetKind
        d["Domain"] = "Domena";
        d["Organizational unit"] = "Jednostka organizacyjna";
        d["Container"] = "Kontener";
        d["Group"] = "Grupa";
        d["User group"] = "Grupa użytkowników";
        d["Computer group"] = "Grupa komputerów";

        // SessionState ("Active" above)
        d["Disconnected"] = "Rozłączona";
        d["Locked"] = "Zablokowana";
    }
}
