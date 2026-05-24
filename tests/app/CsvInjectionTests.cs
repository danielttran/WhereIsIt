using System;
using System.Linq;
using FluentAssertions;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// AUDIT FOLLOW-UP — KNOWN-FAILING until fixed on Windows.
///
/// CSV formula injection. A result row whose Name begins with one of
/// <c>= + - @</c> (or a leading TAB / CR) is written verbatim by
/// <see cref="ResultExporter"/> today, so opening the exported CSV/TSV in
/// Excel or LibreOffice executes the cell as a formula
/// (e.g. <c>=cmd|'/c calc'!A1</c>).
///
/// Intended fix: in <c>ResultExporter.Escape</c>, when a cell's first character
/// is a formula trigger, neutralise it (the usual convention is to prefix a
/// single apostrophe) WITHOUT dropping the original text. Do NOT change the EFU
/// writer — its column 0 is a full path that must round-trip with Everything.
///
/// Each finding is checked from two angles: the dangerous leading character is
/// neutralised (no exported field begins with it) AND the payload text survives.
/// The assertions are deliberately neutralisation-scheme-agnostic: they only
/// require that no data field starts with the trigger character.
/// </summary>
public class CsvInjectionTests
{
    private static ResultRowModel Row(string name)
        => new(name, @"C:\dir", 0, new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero), "A");

    [Fact]
    public void ToCsv_NameStartingWithEquals_IsNeutralised()
    {
        var csv = ResultExporter.ToCsv(new[] { Row("=1+1") });
        // The Name field leads each data row, so a data line beginning with '='
        // means the formula reached the file unneutralised.
        csv.Should().NotContain("\n=1+1");
        csv.Should().Contain("1+1");        // payload preserved, just defused
    }

    [Fact]
    public void ToCsv_NameStartingWithPlus_IsNeutralised()
    {
        var csv = ResultExporter.ToCsv(new[] { Row("+1+2") });
        csv.Should().NotContain("\n+1+2");
        csv.Should().Contain("1+2");
    }

    [Fact]
    public void ToCsv_NameStartingWithMinus_IsNeutralised()
    {
        var csv = ResultExporter.ToCsv(new[] { Row("-3+4") });
        csv.Should().NotContain("\n-3+4");
        csv.Should().Contain("3+4");
    }

    [Fact]
    public void ToCsv_NameStartingWithAt_IsNeutralised()
    {
        var csv = ResultExporter.ToCsv(new[] { Row("@SUM(1)") });
        csv.Should().NotContain("\n@SUM(1)");
        csv.Should().Contain("SUM(1)");
    }

    [Fact]
    public void ToCsv_RealisticFormulaPayload_IsNeutralised()
    {
        var payload = "=cmd|'/c calc'!A1.csv";
        var csv = ResultExporter.ToCsv(new[] { Row(payload) });
        // No data field may begin with the formula trigger.
        csv.Split('\n').Skip(1)              // skip the header line
           .Where(l => l.Length > 0)
           .Should().OnlyContain(l => l[0] != '=');
    }

    [Fact]
    public void ToTsv_NameStartingWithEquals_IsNeutralised()
    {
        var tsv = ResultExporter.ToTsv(new[] { Row("=danger") });
        tsv.Should().NotContain("\n=danger");
        tsv.Should().Contain("danger");
    }

    // GUARD — already passes; a benign name must NOT be altered by the fix.
    [Fact]
    public void ToCsv_NormalName_IsLeftUnchanged()
    {
        var csv = ResultExporter.ToCsv(new[] { Row("report.txt") });
        csv.Should().Contain("\nreport.txt,");
    }
}
