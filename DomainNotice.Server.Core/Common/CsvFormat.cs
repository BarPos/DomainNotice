using System.Text;
using DomainNotice.Shared;

namespace DomainNotice.Server.Common;

/// <summary>
/// The one CSV writer of all exports (grid lists, confirmations, answers): UTF-8 with BOM, CRLF lines, RFC 4180 quoting.
/// The field separator follows the installation's language, because spreadsheets split a CSV file on their regional list
/// separator: ';' for Polish (where ',' is the decimal separator), ',' otherwise.
/// </summary>
public static class CsvFormat
{
    public const char Comma = ',';
    public const char Semicolon = ';';

    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static char SeparatorFor(string? language) => AppLanguages.Normalize(language) == AppLanguages.Polish ? Semicolon : Comma;

    /// <summary>
    /// One cell. A value a spreadsheet would run as a formula (leading = + - @ tab CR) gets a leading apostrophe; a value
    /// with either separator, a quote, CR/LF or outer spaces is quoted, so it always stays one cell whichever of the two
    /// separators the reading program uses.
    /// </summary>
    public static string Field(string? value)
    {
        value ??= "";
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        if (value.AsSpan().IndexOfAny("\",;\r\n") >= 0 || value != value.Trim())
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return value;
    }

    public static void AppendRow(StringBuilder sb, char separator, IEnumerable<string?> fields)
    {
        CheckSeparator(separator);
        var first = true;
        foreach (var field in fields)
        {
            if (!first) sb.Append(separator);
            sb.Append(Field(field));
            first = false;
        }
        sb.Append("\r\n");
    }

    /// <summary>UTF-8 with BOM (GetBytes alone does not write the preamble).</summary>
    public static byte[] Encode(StringBuilder sb) => [.. Utf8Bom.GetPreamble(), .. Utf8Bom.GetBytes(sb.ToString())];

    public static byte[] Write(char separator, IEnumerable<string?> header, IEnumerable<IEnumerable<string?>> rows)
    {
        var sb = new StringBuilder();
        AppendRow(sb, separator, header);
        foreach (var row in rows) AppendRow(sb, separator, row);
        return Encode(sb);
    }

    private static void CheckSeparator(char separator)
    {
        if (separator is not (Comma or Semicolon))
            throw new ArgumentOutOfRangeException(nameof(separator), separator, "The CSV separator must be ',' or ';'.");
    }
}
