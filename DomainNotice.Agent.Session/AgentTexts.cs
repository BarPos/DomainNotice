using System.Globalization;
using DomainNotice.Shared;

namespace DomainNotice.Agent.Session;

/// <summary>
/// Texts of the tray and the announcement window in the language chosen in the Console (Settings → General).
/// Keys are the English texts; a missing translation shows the English text. Messages from the service and the
/// server (status lines, rejected confirmations) are fixed English sentences and are translated here as well.
/// </summary>
internal static class AgentTexts
{
    private static volatile string _language = AppLanguages.Default;

    public static string Language => _language;

    /// <summary>Sets the language; returns true when it changed (the tray then relabels itself).</summary>
    public static bool SetLanguage(string? language)
    {
        var value = AppLanguages.Normalize(language);
        if (value == _language) return false;
        _language = value;
        return true;
    }

    public static string Get(string english) =>
        _language == AppLanguages.Polish && Polish.TryGetValue(english, out var pl) ? pl : english;

    public static string F(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(englishFormat), args);

    private static readonly Dictionary<string, string> Polish = new(StringComparer.Ordinal)
    {
        // Window
        ["Announcement"] = "Ogłoszenie",
        ["Your answers in this announcement are anonymous."] = "Twoje odpowiedzi w tym ogłoszeniu są anonimowe.",
        ["Sending your answer…"] = "Wysyłanie odpowiedzi…",
        ["You can continue in {0}s."] = "Możesz kontynuować za {0} s.",
        ["Select each required choice to continue."] = "Zaznacz wszystkie wymagane odpowiedzi, aby kontynuować.",
        ["You can continue."] = "Możesz kontynuować.",
        ["The DomainNotice Agent service is not reachable. Please try again in a moment."] =
            "Usługa DomainNotice Agent jest niedostępna. Spróbuj ponownie za chwilę.",
        ["Your answer was not accepted. Please try again."] = "Twoja odpowiedź nie została przyjęta. Spróbuj ponownie.",
        ["The connection to the DomainNotice Agent service was lost. Please try again."] =
            "Utracono połączenie z usługą DomainNotice Agent. Spróbuj ponownie.",

        // Tray
        ["Starting"] = "Uruchamianie",
        ["Show pending announcement"] = "Pokaż oczekujące ogłoszenie",
        ["Show pending announcement ({0})"] = "Pokaż oczekujące ogłoszenia ({0})",
        ["About"] = "O programie",
        ["About DomainNotice"] = "O programie DomainNotice",
        ["DomainNotice Agent {0}\n\nStatus: {1}\n\nAnnouncements from your organisation are shown here. This program runs as part of the DomainNotice Agent service."] =
            "DomainNotice Agent {0}\n\nStan: {1}\n\nTutaj wyświetlane są ogłoszenia Twojej organizacji. Ten program działa jako część usługi DomainNotice Agent.",
        ["Waiting for the DomainNotice Agent service"] = "Oczekiwanie na usługę DomainNotice Agent",
        ["Connected to {0}"] = "Połączono z {0}",
        ["Not connected to {0}"] = "Brak połączenia z {0}",
        ["Not connected to {0}: {1}"] = "Brak połączenia z {0}: {1}",
        ["the server"] = "serwerem",

        // Status lines from the service
        ["The server is older than this agent"] = "Serwer jest starszy niż ten agent",
        ["Connecting to the server"] = "Łączenie z serwerem",
        ["Cannot reach the server"] = "Nie można połączyć się z serwerem",
        ["Not configured"] = "Brak konfiguracji",
        ["The server's key does not match"] = "Klucz serwera się nie zgadza",
        ["The server closed the connection"] = "Serwer zamknął połączenie",
        ["Enrolment was refused by the server"] = "Serwer odrzucił rejestrację",
        ["Enrolling again"] = "Ponowna rejestracja",
        ["Not enrolled"] = "Nie zarejestrowano",
        ["Enrolling again with a new key"] = "Ponowna rejestracja z nowym kluczem",
        ["This computer was revoked on the server"] = "Ten komputer został odwołany na serwerze",
        ["This computer was replaced on the server"] = "Ten komputer został zastąpiony na serwerze",
        ["Too many attempts; waiting"] = "Zbyt wiele prób; oczekiwanie",
        ["Refused by the server"] = "Odrzucono przez serwer",
        ["Disconnected from the server"] = "Rozłączono z serwerem",

        // Rejected confirmations (service and server)
        ["Not connected to the server. Your answer was not sent; please try again in a moment."] =
            "Brak połączenia z serwerem. Odpowiedź nie została wysłana; spróbuj ponownie za chwilę.",
        ["The connection to the server was lost before your answer was confirmed. Please try again."] =
            "Połączenie z serwerem zostało przerwane przed potwierdzeniem odpowiedzi. Spróbuj ponownie.",
        ["This button is not part of the announcement."] = "Ten przycisk nie należy do ogłoszenia.",
        ["This announcement is no longer available."] = "To ogłoszenie nie jest już dostępne.",
        ["This announcement was withdrawn."] = "To ogłoszenie zostało wycofane.",
        ["The window was not reported as shown."] = "Okno nie zostało zgłoszone jako wyświetlone.",
        ["This announcement is not active."] = "To ogłoszenie nie jest aktywne.",
        ["That button is not part of this announcement."] = "Ten przycisk nie należy do tego ogłoszenia.",
        ["An answer does not belong to this announcement."] = "Jedna z odpowiedzi nie należy do tego ogłoszenia.",
        ["Only one option may be chosen for a single-choice question."] = "W pytaniu jednokrotnego wyboru można wybrać tylko jedną odpowiedź.",
        ["A required question was not answered."] = "Nie odpowiedziano na wymagane pytanie.",
        ["The announcement cannot be dismissed yet."] = "Tego ogłoszenia nie można jeszcze zamknąć.",
        ["This announcement was already confirmed as often as allowed."] = "To ogłoszenie zostało już potwierdzone maksymalną liczbę razy.",
        ["The server could not store the confirmation. Please try again."] = "Serwer nie mógł zapisać potwierdzenia. Spróbuj ponownie.",
    };

    /// <summary>For tests: every key that has a Polish text.</summary>
    internal static IReadOnlyCollection<string> PolishKeys => Polish.Keys;
}
