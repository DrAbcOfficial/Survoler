using System.Globalization;
using System.Text;
using Avalonia.Platform.Storage;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using OfficeIMO.Excel;
using OfficeIMO.Pdf;
using Survoler.Documents;
using Survoler.Rendering;

namespace Survoler.Tests;

[TestClass]
public sealed class CsvPreviewTests
{
    [TestMethod]
    public void PreservesLiteralTextWithoutFormulasOrTypeInference()
    {
        string[] fields = ["00123", "2026-09-05", "1/2", "=1+1", "+SUM(A1:A2)", "-42", "@SUM(A1:A2)"];

        AssertWorkbook(Encoding.UTF8.GetBytes(string.Join(',', fields)), [fields]);
    }

    [TestMethod]
    public void ParsesQuotesMultilineWhitespaceAndTrailingEmptyFieldsSkippingBlankLines()
    {
        const string csv = "\r\n  \r\n\"a,b\",\"say \"\"hello\"\"\",\"line1\r\nline2\",  padded  ,\"  quoted  \",\r\n"
            + "\r\nleft;right,tab\tinside,,,,\r\n,,\r\n\r\n";

        AssertWorkbook(Encoding.UTF8.GetBytes(csv),
        [
            ["a,b", "say \"hello\"", "line1\nline2", "  padded  ", "  quoted  ", ""],
            ["left;right", "tab\tinside", "", "", "", ""],
            ["", "", ""]
        ]);
    }

    [TestMethod]
    [DataRow("utf8")]
    [DataRow("utf8-bom")]
    [DataRow("utf16-le")]
    [DataRow("utf16-be")]
    public void DecodesSupportedEncodingsIncludingChinese(string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "utf8" => new UTF8Encoding(false, true),
            "utf8-bom" => new UTF8Encoding(true, true),
            "utf16-le" => new UnicodeEncoding(false, true, true),
            _ => new UnicodeEncoding(true, true, true)
        };
        const string chinese = "\u4e2d\u6587\u9884\u89c8";
        byte[] content = [.. encoding.GetPreamble(), .. encoding.GetBytes($"{chinese},00123\n")];

        AssertWorkbook(content, [[chinese, "00123"]]);
    }

    [TestMethod]
    [DataRow("C328")]
    [DataRow("EFBBBFE4B8")]
    [DataRow("FFFE610062")]
    [DataRow("FEFF006100")]
    [DataRow("FFFE00D8")]
    [DataRow("FEFFD800")]
    [DataRow("FFFE000061000000")]
    [DataRow("0000FEFF00000061")]
    public void RejectsMalformedBytesAndUnsupportedUtf32(string hex)
    {
        AssertRejected(Convert.FromHexString(hex), "CSV must use UTF-8 or UTF-16 with a BOM.");
    }

    [TestMethod]
    [DataRow("before\0after")]
    [DataRow("before\u0001after")]
    [DataRow("\"before\u000bafter\"")]
    public void RejectsXmlControlCharacters(string csv)
    {
        AssertRejected(Encoding.UTF8.GetBytes(csv), "The CSV file contains unsupported control characters.");
    }

    [TestMethod]
    [DataRow("a,\"unterminated")]
    [DataRow("a,\"line1\nline2")]
    [DataRow("\"closed\"junk,b")]
    public void RejectsMalformedQuotedFields(string csv)
    {
        AssertRejected(Encoding.UTF8.GetBytes(csv), "The CSV file contains malformed quoted fields.");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("\r\n \t\r\n\n")]
    [DataRow("\uFEFF")]
    public void RejectsFilesWithoutRecords(string csv)
    {
        AssertRejected(Encoding.UTF8.GetBytes(csv), "The CSV file contains no records to preview.");
    }

    [TestMethod]
    [DataRow("rows")]
    [DataRow("columns")]
    [DataRow("cells")]
    [DataRow("field")]
    public void RejectsExceededPreviewLimits(string limit)
    {
        // Each input exceeds just one limit; empty cells keep the cell-budget case small.
        string csv = limit switch
        {
            "rows" => string.Concat(Enumerable.Repeat("x\n", 10001)),
            "columns" => new string(',', 256),
            "cells" => string.Concat(Enumerable.Repeat(new string(',', 249) + "\n", 400)) + "x",
            _ => new string('x', 32768)
        };
        string message = limit == "field"
            ? "A CSV field exceeds 32,767 characters."
            : "CSV preview is limited to 10,000 rows, 256 columns and 100,000 cells.";

        AssertRejected(Encoding.UTF8.GetBytes(csv), message);
    }

    [TestMethod]
    public void AcceptsMaximumFieldLengthAndColumnCount()
    {
        string[] fields = Enumerable.Repeat("", 256).ToArray();
        fields[0] = new string('x', 32767);
        fields[^1] = "last";

        AssertWorkbook(Encoding.UTF8.GetBytes(string.Join(',', fields)), [fields]);
    }

    [TestMethod]
    public void HonorsAlreadyCancelledToken()
    {
        using DocumentSession source = CreateSource("value"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException exception = Assert.ThrowsExactly<OperationCanceledException>(() =>
        {
            using ExcelDocument document = CsvWorkbookReader.Load(source.LocalPath, cancellation.Token);
        });

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
    }

    [TestMethod]
    public async Task OpensCsvAndConvertsToReadablePdfWithInteractionMapWithoutChangingSource()
    {
        const string sourceName = "report.CSV";
        byte[] original = "Preview marker,00123\r\nsecond,=1+1\r\n"u8.ToArray();
        using IStorageFile file = FakeStorageFile.Create(sourceName, original);
        using var coordinator = new DocumentOpenCoordinator();
        DocumentSession? session = await coordinator.OpenAsync(file);

        Assert.IsNotNull(session);
        Assert.AreEqual(sourceName, session.SourceName);
        Assert.AreEqual(OfficeFileKind.Csv, session.Kind);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(session.LocalPath));
        try
        {
            using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(
                session, CancellationToken.None);
            byte[] bytes = await File.ReadAllBytesAsync(converted.Path);
            CollectionAssert.AreEqual("%PDF-"u8.ToArray(), bytes.Take(5).ToArray());
            PdfReadDocument pdf = PdfReadDocument.Open(converted.Path);
            Assert.IsGreaterThan(0, pdf.Pages.Count);
            string text = pdf.ExtractText();
            PdfPageInteractionMap map = PdfPageInteractionMap.Create(bytes, 1);
            Assert.IsGreaterThan(0, map.TextRegions.Count);
            string mappedText = string.Concat(map.TextRegions.Select(region => region.Text));
            foreach (string expected in new[] { "Preview marker", "00123", "=1+1" })
            {
                StringAssert.Contains(text, expected);
                StringAssert.Contains(mappedText, expected);
            }
        }
        finally
        {
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(session.LocalPath),
                "Conversion must not modify the cached CSV source, even on failure.");
            await using Stream source = await file.OpenReadAsync();
            using var copy = new MemoryStream();
            await source.CopyToAsync(copy);
            CollectionAssert.AreEqual(original, copy.ToArray());
        }
    }

    private static void AssertWorkbook(byte[] content, string[][] expected)
    {
        using DocumentSession source = CreateSource(content);
        using ExcelDocument document = CsvWorkbookReader.Load(source.LocalPath, CancellationToken.None);
        using var stream = new MemoryStream();
        document.Save(stream);
        stream.Position = 0;
        using SpreadsheetDocument saved = SpreadsheetDocument.Open(stream, false);
        WorkbookPart workbook = saved.WorkbookPart!;
        Sheet sheet = workbook.Workbook!.Sheets!.Elements<Sheet>().Single();
        Assert.AreEqual("CSV", sheet.Name!.Value);
        Worksheet worksheet = ((WorksheetPart)workbook.GetPartById(sheet.Id!.Value!)).Worksheet!;
        Assert.IsFalse(worksheet.Descendants<CellFormula>().Any(), "CSV content must never become a formula.");
        Row[] rows = worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToArray();
        Assert.AreEqual(expected.Length, rows.Length);
        SharedStringItem[] sharedStrings = workbook.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>().ToArray() ?? [];
        for (int row = 0; row < expected.Length; row++)
        {
            Assert.AreEqual((uint)(row + 1), rows[row].RowIndex!.Value);
            Cell[] cells = rows[row].Elements<Cell>().ToArray();
            Assert.AreEqual(expected[row].Length, cells.Length, $"Cell count in row {row + 1}.");
            for (int column = 0; column < cells.Length; column++)
            {
                Cell cell = cells[column];
                Assert.IsTrue(cell.DataType?.Value == CellValues.SharedString ||
                    cell.DataType?.Value == CellValues.InlineString || cell.DataType?.Value == CellValues.String,
                    $"{cell.CellReference}: expected a text cell.");
                string value = cell.DataType?.Value == CellValues.SharedString
                    ? sharedStrings[int.Parse(cell.CellValue!.Text, CultureInfo.InvariantCulture)].InnerText
                    : cell.InlineString?.InnerText ?? cell.CellValue?.Text ?? "";
                Assert.AreEqual(expected[row][column], value,
                    $"Unexpected text at row {row + 1}, column {column + 1}.");
            }
        }
        CollectionAssert.AreEqual(content, File.ReadAllBytes(source.LocalPath));
    }

    private static void AssertRejected(byte[] content, string message)
    {
        using DocumentSession source = CreateSource(content);
        DocumentOpenException exception = Assert.ThrowsExactly<DocumentOpenException>(() =>
        {
            using ExcelDocument document = CsvWorkbookReader.Load(source.LocalPath, CancellationToken.None);
        });
        Assert.AreEqual(message, exception.Message);
    }

    private static DocumentSession CreateSource(byte[] content)
    {
        string name = $"survoler-csv-test-{Guid.NewGuid():N}.csv";
        string path = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllBytes(path, content);
        return new DocumentSession(Guid.NewGuid(), name, path, OfficeFileKind.Csv);
    }
}
