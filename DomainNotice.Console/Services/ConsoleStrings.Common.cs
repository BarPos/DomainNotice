namespace DomainNotice.Console.Services;

internal static partial class ConsoleStrings
{
    /// <summary>Shared words, the grid (DataGrid), the filter bar (FilterBarItem), dialogs (PromptDialog), status chips.</summary>
    static partial void Common(Dictionary<string, string> d)
    {
        // Shared words
        d["Save"] = "Zapisz";
        d["Cancel"] = "Anuluj";
        d["Add"] = "Dodaj";
        d["Delete"] = "Usuń";
        d["Remove"] = "Usuń";
        d["Edit"] = "Edytuj";
        d["Close"] = "Zamknij";
        d["Apply"] = "Zastosuj";
        d["Refresh"] = "Odśwież";
        d["OK"] = "OK";
        d["Yes"] = "Tak";
        d["No"] = "Nie";
        d["Back"] = "Wstecz";
        d["Next"] = "Dalej";
        d["Name"] = "Nazwa";
        d["Never"] = "Nigdy";
        d["Default"] = "Domyślne";
        d["Saved."] = "Zapisano.";
        d["Not saved."] = "Nie zapisano.";
        d["Failed."] = "Nie powiodło się.";

        // Grid toolbar, header, footer, action bar
        d["Search"] = "Szukaj";
        d["Search this list"] = "Przeszukaj tę listę";
        d["Add filter"] = "Dodaj filtr";
        d["Add a filter"] = "Dodaj filtr";
        d["Choose columns"] = "Wybierz kolumny";
        d["Columns"] = "Kolumny";
        d["Default columns"] = "Domyślne kolumny";
        d["Reload"] = "Odśwież";
        d["CSV"] = "CSV";
        d["Export the filtered list as CSV"] = "Eksportuj przefiltrowaną listę do pliku CSV";
        d["Clear all"] = "Wyczyść wszystko";
        d["Select all rows on this page"] = "Zaznacz wszystkie wiersze na tej stronie";
        d["Select row"] = "Zaznacz wiersz";
        d["Filter {0}"] = "Filtruj: {0}";
        d["No items match."] = "Brak pasujących elementów.";
        d["Loading…"] = "Wczytywanie…";
        d["Rows per page"] = "Wierszy na stronę";
        d["{0} per page"] = "{0} na stronę";
        d["<b>{0}</b> selected"] = "Zaznaczone: <b>{0}</b>";
        d["Clear selection"] = "Wyczyść zaznaczenie";
        d["0 items"] = "0 elementów";
        d["{0}–{1} of {2}"] = "{0}–{1} z {2}";
        d["The list could not be loaded. Try again, or remove a filter."] =
            "Nie udało się wczytać listy. Spróbuj ponownie lub usuń filtr.";

        // Filter bar
        d["{0} condition"] = "{0}: warunek";
        d["type, then Enter"] = "wpisz i naciśnij Enter";
        d["number, then Enter"] = "liczba, potem Enter";
        d["days, then Enter"] = "dni, potem Enter";
        d["Apply filter"] = "Zastosuj filtr";
        d["any"] = "dowolne";
        d["days"] = "dni";
        d["{0} days"] = "{0}: dni";
        d["from ({0})"] = "od ({0})";
        d["to ({0})"] = "do ({0})";
        d["{0} from"] = "{0}: od";
        d["{0} to"] = "{0}: do";
        d["Remove the {0} filter"] = "Usuń filtr „{0}”";
        d["contains"] = "zawiera";
        d["is"] = "jest";
        d["between"] = "pomiędzy";
        d["older than"] = "starsze niż";
        d["within the last"] = "w ciągu ostatnich";
        d["at least"] = "co najmniej";
        d["at most"] = "co najwyżej";

        // Prompt and confirm dialogs
        d["Repeat {0}"] = "Powtórz {0}";
        d["Enter at least {0} characters."] = "Wpisz co najmniej {0} znaków.";
        d["Enter a value."] = "Wpisz wartość.";
        d["The values do not match."] = "Wartości nie są zgodne.";

        // Computer status dot
        d["Online"] = "Online";
        d["Offline"] = "Offline";
        d["Revoked"] = "Odwołany";

        // Announcement status chip
        d["Draft"] = "Szkic";
        d["Scheduled"] = "Zaplanowane";
        d["Active"] = "Aktywne";
        d["Ended"] = "Zakończone";
    }
}
