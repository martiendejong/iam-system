using System.Text;

namespace IAM.Core.Security;

/// <summary>
/// Shared CSV cell writer for text columns in exports. Neutralises spreadsheet formula
/// injection: a text cell that starts with = + - @ tab or CR gets a leading single quote so
/// Excel / LibreOffice show it as plain text instead of evaluating it.
/// Only use for TEXT cells; genuine numeric columns must be written without this helper.
/// </summary>
public static class CsvCell
{
    public static bool IsFormulaTrigger(char c) => c is '=' or '+' or '-' or '@' or '\t' or '\r';

    /// <summary>Prefixes a single quote when the text starts with a formula character.</summary>
    public static string Neutralize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        return IsFormulaTrigger(value[0]) ? "'" + value : value;
    }

    /// <summary>
    /// Neutralises and CSV-escapes a text cell. Quotes the cell when it contains a comma,
    /// double quote, CR or LF (or always when <paramref name="alwaysQuote"/> is set).
    /// </summary>
    public static string Escape(string? value, bool alwaysQuote = false)
    {
        var text = Neutralize(value);
        var needsQuotes = alwaysQuote || text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        return needsQuotes ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    /// <summary>
    /// Reverses <see cref="Neutralize"/> on import: drops the leading single quote only when it
    /// guards a formula character, so an export/import round trip keeps the original value.
    /// </summary>
    public static string Restore(string value)
    {
        return value.Length >= 2 && value[0] == '\'' && IsFormulaTrigger(value[1]) ? value[1..] : value;
    }
}
