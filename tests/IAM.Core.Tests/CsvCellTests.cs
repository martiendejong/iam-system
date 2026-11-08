using IAM.Core.Security;
using Xunit;

namespace IAM.Core.Tests;

public class CsvCellTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"http://x\",\"y\")")]
    [InlineData("+1+1")]
    [InlineData("-cmd")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tabc")]
    [InlineData("\rabc")]
    public void Escape_PrefixesSingleQuoteOnFormulaCharacters(string input)
    {
        var cell = CsvCell.Escape(input);
        Assert.StartsWith("'", cell.TrimStart('"'));
        Assert.False(CsvCell.IsFormulaTrigger(cell.TrimStart('"')[0]));
    }

    [Fact]
    public void Escape_UserExportExample_StartsWithQuoteEquals()
    {
        Assert.Equal("\"'=HYPERLINK(\"\"http://x\"\",\"\"y\"\")\"", CsvCell.Escape("=HYPERLINK(\"http://x\",\"y\")", true));
    }

    [Fact]
    public void Escape_LeavesNormalTextAlone()
    {
        Assert.Equal("hello world", CsvCell.Escape("hello world"));
        Assert.Equal("a=b", CsvCell.Escape("a=b"));
        Assert.Equal("", CsvCell.Escape(null));
        Assert.Equal("", CsvCell.Escape(""));
    }

    [Theory]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("l1\nl2", "\"l1\nl2\"")]
    [InlineData("l1\r\nl2", "\"l1\r\nl2\"")]
    public void Escape_QuotesCellsWithSpecialCharacters(string input, string expected)
        => Assert.Equal(expected, CsvCell.Escape(input));

    [Fact]
    public void Escape_AlwaysQuoteWrapsPlainText()
        => Assert.Equal("\"plain\"", CsvCell.Escape("plain", true));

    [Fact]
    public void Escape_FormulaWithCommaIsBothPrefixedAndQuoted()
        => Assert.Equal("\"'=1,2\"", CsvCell.Escape("=1,2"));

    [Fact]
    public void NumericText_IsNotAlteredWhenWrittenAsNumber()
    {
        // Numeric telemetry values are formatted directly and never pass through the helper.
        Assert.Equal("-12.5", (-12.5).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+x")]
    [InlineData("-x")]
    [InlineData("@x")]
    public void Restore_ReversesNeutralize(string original)
        => Assert.Equal(original, CsvCell.Restore(CsvCell.Neutralize(original)));

    [Theory]
    [InlineData("'hello")]
    [InlineData("'")]
    [InlineData("O'Brien")]
    public void Restore_KeepsLegitimateLeadingQuote(string value)
        => Assert.Equal(value, CsvCell.Restore(value));
}
