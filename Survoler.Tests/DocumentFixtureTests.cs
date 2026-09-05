using System.Globalization;
using System.IO.Compression;
using System.Resources;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using OfficeIMO;
using OfficeIMO.Excel;
using OfficeIMO.Pdf;
using OfficeIMO.Word;
using Survoler.Documents;
using Survoler.Rendering;
using Survoler.Resources;
using static Survoler.Tests.DocumentOpenCoordinatorTests;

namespace Survoler.Tests;

[TestClass]
public sealed class DocumentFixtureTests
{
    // Shared, non-identifying title observed in the Word, PDF and OFD fixtures.
    private const string Title = "\u4eca\u5929\u665a\u4e0a\u5403\u4ec0\u4e48";

    [TestMethod]
    [DataRow("sample.csv", OfficeFileKind.Csv)]
    [DataRow("sample.ofd", OfficeFileKind.Ofd)]
    [DataRow("sample.pdf", OfficeFileKind.Pdf)]
    [DataRow("sample.doc", OfficeFileKind.Doc)]
    [DataRow("sample.docx", OfficeFileKind.Docx)]
    public async Task CoordinatorClassifiesFixturePreservesBytesAndDeletesOnlySessionCopy(
        string name, OfficeFileKind kind)
    {
        byte[] original = ReadFixture(name);
        using var file = FakeStorageFile.Create(name, original);
        using var coordinator = new DocumentOpenCoordinator();
        DocumentSession? session = await coordinator.OpenAsync(file);
        Assert.IsNotNull(session);
        Assert.AreEqual(kind, session.Kind);
        Assert.AreEqual(name, session.SourceName);
        Assert.AreNotEqual(Path.Combine(AppContext.BaseDirectory, "TestData", name), session.LocalPath);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(session.LocalPath));

        coordinator.Dispose();
        Assert.IsFalse(File.Exists(session.LocalPath));
        await using Stream source = await file.OpenReadAsync();
        using var copy = new MemoryStream();
        await source.CopyToAsync(copy);
        CollectionAssert.AreEqual(original, copy.ToArray());
        CollectionAssert.AreEqual(original, ReadFixture(name));
    }

    [TestMethod]
    public async Task CsvFixtureParsesAllThirteenRowsAsEightLiteralTextCells()
    {
        string[] expected =
        [
            "O,A1,A2,A3,A4,A5,A6,SUM",
            "B1,167,190,178,192,269,181,1177",
            "B2,253,249,96,128,108,283,1117",
            "B3,200,91,203,97,246,112,949",
            "B4,149,211,275,288,193,282,1398",
            "B5,109,285,187,232,219,284,1316",
            "B6,292,173,241,86,298,274,1364",
            "B7,51,141,285,84,43,75,679",
            "B8,228,139,220,97,282,274,1240",
            "B9,141,139,74,139,146,140,779",
            "B10,162,209,85,298,87,142,983",
            "B11,92,195,210,203,115,287,1102",
            "B12,195,212,142,103,114,273,1039"
        ];
        byte[] original = ReadFixture("sample.csv");
        Assert.IsTrue(original.All(b => b < 128), "This fixture is ASCII-compatible UTF-8 without a BOM.");
        Assert.AreEqual(string.Join("\r\n", expected) + "\r\n", new UTF8Encoding(false, true).GetString(original));
        using var file = FakeStorageFile.Create("sample.csv", original);
        using var coordinator = new DocumentOpenCoordinator();
        DocumentSession? session = await coordinator.OpenAsync(file);
        Assert.IsNotNull(session);
        using (ExcelDocument document = CsvWorkbookReader.Load(session.LocalPath, CancellationToken.None))
        {
            using var stream = new MemoryStream();
            document.Save(stream);
            stream.Position = 0;
            using SpreadsheetDocument saved = SpreadsheetDocument.Open(stream, false);
            WorkbookPart workbook = saved.WorkbookPart!;
            Sheet sheet = workbook.Workbook!.Sheets!.Elements<Sheet>().Single();
            Assert.AreEqual("CSV", sheet.Name!.Value);
            Worksheet worksheet = ((WorksheetPart)workbook.GetPartById(sheet.Id!.Value!)).Worksheet!;
            Assert.IsFalse(worksheet.Descendants<CellFormula>().Any());
            Row[] rows = worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToArray();
            Assert.AreEqual(expected.Length, rows.Length);
            SharedStringItem[] strings = workbook.SharedStringTablePart?.SharedStringTable?
                .Elements<SharedStringItem>().ToArray() ?? [];
            for (int r = 0; r < rows.Length; r++)
            {
                Assert.AreEqual((uint)(r + 1), rows[r].RowIndex!.Value);
                Cell[] cells = rows[r].Elements<Cell>().ToArray();
                string[] fields = expected[r].Split(',');
                Assert.AreEqual(8, cells.Length);
                for (int c = 0; c < cells.Length; c++)
                {
                    Cell cell = cells[c];
                    Assert.AreEqual($"{(char)('A' + c)}{r + 1}", cell.CellReference!.Value);
                    Assert.IsTrue(cell.DataType?.Value == CellValues.SharedString ||
                        cell.DataType?.Value == CellValues.InlineString || cell.DataType?.Value == CellValues.String);
                    string value = cell.DataType?.Value == CellValues.SharedString
                        ? strings[int.Parse(cell.CellValue!.Text, CultureInfo.InvariantCulture)].InnerText
                        : cell.InlineString?.InnerText ?? cell.CellValue?.Text ?? "";
                    Assert.AreEqual(fields[c], value, $"Unexpected text at {cell.CellReference}.");
                }
            }
        }
        CollectionAssert.AreEqual(original, File.ReadAllBytes(session.LocalPath));
    }

    [TestMethod]
    public async Task CsvFixtureConvertsToThreeSelectablePdfPagesWithIndependentLifetime()
    {
        byte[] original = ReadFixture("sample.csv");
        using var file = FakeStorageFile.Create("sample.csv", original);
        using var coordinator = new DocumentOpenCoordinator();
        DocumentSession? session = await coordinator.OpenAsync(file);
        Assert.IsNotNull(session);
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        Assert.IsNull(converted.Warning);
        byte[] bytes = File.ReadAllBytes(converted.Path);
        CollectionAssert.AreEqual("%PDF-"u8.ToArray(), bytes[..5]);
        PdfReadDocument pdf = PdfReadDocument.Open(converted.Path);
        Assert.AreEqual(3, pdf.Pages.Count);
        string text = pdf.ExtractText();
        var mapped = new StringBuilder();
        for (int page = 1; page <= pdf.Pages.Count; page++)
        {
            PdfPageInteractionMap map = PdfPageInteractionMap.Create(bytes, page);
            Assert.IsGreaterThan(0, map.TextRegions.Count);
            mapped.Append(string.Concat(map.TextRegions.Select(r => r.Text)));
        }
        foreach (string marker in new[] { "A1", "A6", "SUM", "B12", "1177", "1039" })
        {
            StringAssert.Contains(text, marker);
            StringAssert.Contains(mapped.ToString(), marker);
        }
        CollectionAssert.AreEqual(original, File.ReadAllBytes(session.LocalPath));
        coordinator.Dispose();
        Assert.IsFalse(File.Exists(session.LocalPath));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(converted.Path));
        converted.Dispose();
        Assert.IsFalse(File.Exists(converted.Path));
        CollectionAssert.AreEqual(original, ReadFixture("sample.csv"));
    }

    [TestMethod]
    public async Task PdfFixtureDirectRoutePreservesBothTextPagesAndCleansCopyOnFactoryFailure()
    {
        byte[] original = ReadFixture("sample.pdf");
        CollectionAssert.AreEqual("%PDF-1.7"u8.ToArray(), original[..8]);
        using var file = FakeStorageFile.Create("sample.pdf", original);
        using var coordinator = new DocumentOpenCoordinator();
        DocumentSession? session = await coordinator.OpenAsync(file);
        Assert.IsNotNull(session);
        var sentinel = new InvalidOperationException("Fixture factory failure sentinel");
        var factory = new InspectingPdfFactory(original, sentinel);
        // The real converter rejects Pdf; reaching this factory proves the direct-copy route.
        var renderer = new OfficePdfPreviewRenderer(new OfficePdfConverter(), factory);
        Assert.IsTrue(renderer.CanRender(session.Kind));
        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => renderer.CreateAsync(session, CancellationToken.None));
        Assert.AreSame(sentinel, error);
        Assert.AreEqual(1, factory.Calls);
        Assert.IsNotNull(factory.Path);
        Assert.AreNotEqual(session.LocalPath, factory.Path);
        Assert.IsFalse(File.Exists(factory.Path));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(session.LocalPath));
        coordinator.Dispose();
        Assert.IsFalse(File.Exists(session.LocalPath));
        CollectionAssert.AreEqual(original, ReadFixture("sample.pdf"));
    }

    [TestMethod]
    [DataRow("sample.doc", 20)]
    [DataRow("sample.docx", 79)]
    public async Task WordFixtureReadOnlyLoadPreservesChineseTitleAndParagraphStructure(string name, int paragraphs)
    {
        byte[] original = ReadFixture(name);
        using var file = FakeStorageFile.Create(name, original);
        using var coordinator = new DocumentOpenCoordinator();
        DocumentSession? session = await coordinator.OpenAsync(file);
        Assert.IsNotNull(session);
        // Both samples hit MissingPdfFont under managed defaults (also with installed SimHei).
        // Reader coverage is unconditional; it does not imply PDF conversion or layout fidelity.
        using (WordDocument word = WordDocument.Load(session.LocalPath, new WordLoadOptions
        {
            AccessMode = DocumentAccessMode.ReadOnly,
            PersistenceMode = DocumentPersistenceMode.Explicit,
            MaxInputBytes = PreviewLimits.MaxInputBytes,
            PackageSecurity = PreviewLimits.CreatePackageSecurity()
        }))
        {
            Assert.AreEqual(paragraphs, word.Paragraphs.Count);
            Assert.AreEqual(0, word.Tables.Count);
            string text = string.Concat(word.Paragraphs.Select(p => p.Text));
            StringAssert.Contains(text, Title);
            StringAssert.Contains(text, "114514");
            Assert.IsGreaterThan(1000, text.Length);
        }
        CollectionAssert.AreEqual(original, File.ReadAllBytes(session.LocalPath));
        coordinator.Dispose();
        Assert.IsFalse(File.Exists(session.LocalPath));
        CollectionAssert.AreEqual(original, ReadFixture(name));
    }

    [TestMethod]
    public async Task OfdFixtureContainsTwoChinesePagesButRejectsDocInfoCustomDatasExplicitly()
    {
        byte[] original = ReadFixture("sample.ofd");
        using (var zip = new ZipArchive(new MemoryStream(original), ZipArchiveMode.Read))
        {
            XNamespace ns = "http://www.ofdspec.org/2016";
            XDocument manifest = ReadXml(zip, "OFD.xml");
            XElement body = manifest.Root!.Elements(ns + "DocBody").Single();
            Assert.IsNotNull(body.Element(ns + "DocInfo")!.Element(ns + "CustomDatas"));
            Assert.AreEqual("Doc_0/Document.xml", body.Element(ns + "DocRoot")!.Value);
            XDocument document = ReadXml(zip, "Doc_0/Document.xml");
            Assert.AreEqual(2, document.Root!.Element(ns + "Pages")!.Elements(ns + "Page").Count());
            for (int page = 0; page < 2; page++)
            {
                XDocument content = ReadXml(zip, $"Doc_0/Pages/Page_{page}/Content.xml");
                StringAssert.Contains(string.Concat(content.Descendants(ns + "TextCode").Select(e => e.Value)), Title);
                Assert.IsTrue(content.Descendants(ns + "CGTransform").Any());
                if (page == 1)
                {
                    Assert.IsTrue(content.Descendants(ns + "Clips").Any());
                    Assert.IsTrue(content.Descendants(ns + "AxialShd").Any());
                }
            }
        }
        using var file = FakeStorageFile.Create("sample.ofd", original);
        using var coordinator = new DocumentOpenCoordinator();
        DocumentSession? session = await coordinator.OpenAsync(file);
        Assert.IsNotNull(session);
        DocumentOpenException error = await Assert.ThrowsExactlyAsync<DocumentOpenException>(async () =>
        {
            using var unexpected = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        });
        var resources = new ResourceManager("Survoler.Resources.OfdStrings", typeof(Strings).Assembly);
        try
        {
            string? format = resources.GetString("UnsupportedPrefix", CultureInfo.CurrentUICulture);
            Assert.IsNotNull(format);
            Assert.AreEqual(string.Format(CultureInfo.CurrentUICulture, format,
                "DocInfo/{http://www.ofdspec.org/2016}CustomDatas"), error.Message);
        }
        finally
        {
            resources.ReleaseAllResources();
        }
        CollectionAssert.AreEqual(original, File.ReadAllBytes(session.LocalPath));
        coordinator.Dispose();
        Assert.IsFalse(File.Exists(session.LocalPath));
        CollectionAssert.AreEqual(original, ReadFixture("sample.ofd"));
    }

    [TestMethod]
    [DataRow("sample.doc")]
    [DataRow("sample.docx")]
    public async Task WordFixtureReportsMissingFontWithManagedDefaultsWithoutLeavingPdf(string name)
    {
        byte[] original = ReadFixture(name);
        using var file = FakeStorageFile.Create(name, original);
        using var coordinator = new DocumentOpenCoordinator();
        DocumentSession? session = await coordinator.OpenAsync(file);
        Assert.IsNotNull(session);
        // This records the desktop managed-font limitation, not Android's system-font behavior.
        DocumentOpenException error = await Assert.ThrowsExactlyAsync<DocumentOpenException>(async () =>
        {
            using var unexpected = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        });
        Assert.AreEqual(Strings.Get("MissingPdfFont"), error.Message);
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetTempPath(), "survoler", $"{session.Id:N}.pdf")));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(session.LocalPath));
        CollectionAssert.AreEqual(original, ReadFixture(name));
    }

    private static byte[] ReadFixture(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", name);
        using FileStream stream = File.OpenRead(path);
        Assert.IsGreaterThan(0L, stream.Length);
        Assert.IsLessThan(8L * 1024 * 1024, stream.Length, "Bound fixture inspection before allocating or parsing.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static XDocument ReadXml(ZipArchive zip, string name)
    {
        ZipArchiveEntry? entry = zip.GetEntry(name);
        Assert.IsNotNull(entry);
        Assert.IsLessThan(4L * 1024 * 1024, entry.Length);
        using Stream stream = entry.Open();
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 4 * 1024 * 1024
        });
        XDocument document = XDocument.Load(reader);
        Assert.AreEqual("UTF-8", document.Declaration?.Encoding);
        return document;
    }

    private sealed class InspectingPdfFactory(byte[] original, Exception sentinel) : IPdfPageRendererFactory
    {
        public string? Path { get; private set; }
        public int Calls { get; private set; }

        public Task<IPdfPageRenderer> OpenAsync(string pdfPath, CancellationToken cancellationToken)
        {
            Path = pdfPath;
            Calls++;
            CollectionAssert.AreEqual(original, File.ReadAllBytes(pdfPath));
            PdfReadDocument pdf = PdfReadDocument.Open(pdfPath);
            Assert.AreEqual(2, pdf.Pages.Count);
            StringAssert.Contains(pdf.Pages[0].ExtractText(), Title);
            for (int page = 0; page < 2; page++)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(pdf.Pages[page].ExtractText()));
                Assert.AreEqual(page == 0 ? 0 : 5, pdf.Pages[page].GetImages().Count);
                PdfPageInteractionMap map = PdfPageInteractionMap.Create(original, page + 1);
                Assert.IsGreaterThan(0, map.TextRegions.Count);
                if (page == 0) StringAssert.Contains(string.Concat(map.TextRegions.Select(r => r.Text)), Title);
            }
            return Task.FromException<IPdfPageRenderer>(sentinel);
        }
    }
}
