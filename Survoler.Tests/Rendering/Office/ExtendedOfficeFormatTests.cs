using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using OfficeIMO.Pdf;
using OfficeIMO.Word;
using Survoler.Documents;
using Survoler.Rendering;

namespace Survoler.Tests;

[TestClass]
public sealed class ExtendedOfficeFormatTests
{
    [TestMethod]
    [DataRow(".xlsm", SpreadsheetDocumentType.MacroEnabledWorkbook,
        "application/vnd.ms-excel.sheet.macroEnabled.main+xml")]
    [DataRow(".xltm", SpreadsheetDocumentType.MacroEnabledTemplate,
        "application/vnd.ms-excel.template.macroEnabled.main+xml")]
    [DataRow(".xlam", SpreadsheetDocumentType.AddIn,
        "application/vnd.ms-excel.addin.macroEnabled.main+xml")]
    public async Task ConvertsRealSpreadsheetTypeToReadablePdfWithoutChangingInput(
        string extension, SpreadsheetDocumentType documentType, string contentType)
    {
        using DocumentSession session = CreateSession(extension, OfficeFileKind.Xlsx);
        CopyFixture("sample.xlsx", session.LocalPath);
        using (SpreadsheetDocument document = SpreadsheetDocument.Open(session.LocalPath, true))
        {
            document.ChangeDocumentType(documentType);
        }

        using (SpreadsheetDocument document = SpreadsheetDocument.Open(session.LocalPath, false))
        {
            Assert.AreEqual(documentType, document.DocumentType);
            Assert.AreEqual(contentType, document.WorkbookPart!.ContentType);
            Assert.IsNull(document.WorkbookPart.VbaProjectPart);
            Assert.IsFalse(document.WorkbookPart.MacroSheetParts.Any());
            Assert.IsFalse(document.WorkbookPart.InternationalMacroSheetParts.Any());
        }

        // XLAM here has ordinary worksheet content, not a VBA-only add-in.
        await AssertConvertedPdfAsync(session, "Documents");
    }

    [TestMethod]
    public async Task ConvertsRealDotxTemplateToReadablePdfWithoutChangingInput()
    {
        const string expectedText = "Survoler quick Office preview";
        using DocumentSession session = CreateSession(".dotx", OfficeFileKind.Docx);
        using (WordDocument document = WordDocument.Create())
        {
            document.AddParagraph(expectedText);
            document.Save(session.LocalPath);
        }

        using (WordprocessingDocument document = WordprocessingDocument.Open(session.LocalPath, true))
        {
            document.ChangeDocumentType(WordprocessingDocumentType.Template);
        }

        using (WordprocessingDocument document = WordprocessingDocument.Open(session.LocalPath, false))
        {
            Assert.AreEqual(WordprocessingDocumentType.Template, document.DocumentType);
            Assert.AreEqual(
                "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml",
                document.MainDocumentPart!.ContentType);
            Assert.IsNull(document.MainDocumentPart.VbaProjectPart);
        }

        await AssertConvertedPdfAsync(session, expectedText);
    }

    [TestMethod]
    public async Task ConvertsRealPptmPresentationToReadablePdfWithoutChangingInput()
    {
        using DocumentSession session = CreateSession(".pptm", OfficeFileKind.Pptx);
        CopyFixture("sample.pptx", session.LocalPath);
        using (PresentationDocument document = PresentationDocument.Open(session.LocalPath, true))
        {
            document.ChangeDocumentType(PresentationDocumentType.MacroEnabledPresentation);
        }

        using (PresentationDocument document = PresentationDocument.Open(session.LocalPath, false))
        {
            Assert.AreEqual(PresentationDocumentType.MacroEnabledPresentation, document.DocumentType);
            Assert.AreEqual("application/vnd.ms-powerpoint.presentation.macroEnabled.main+xml",
                document.PresentationPart!.ContentType);
            Assert.IsNull(document.PresentationPart.VbaProjectPart);
        }

        await AssertConvertedPdfAsync(session, "Survoler Presentation");
    }

    [TestMethod]
    [DataRow(".xlt")]
    [DataRow(".xla")]
    public async Task ConvertsXlsExtensionAliasOnlyWithoutChangingInput(string extension)
    {
        // OfficeIMO.Core 3.3.0's OfficeCompoundFileReader/Writer are internal, not public
        // APIs. No BIFF TEMPLATE (0x0060) or ADDIN (0x0087) record is set here, so this
        // remains extension-routing coverage, not evidence of genuine XLT/XLA support.
        using DocumentSession session = CreateSession(extension, OfficeFileKind.Xls);
        CopyFixture("sample.xls", session.LocalPath);
        await AssertConvertedPdfAsync(session, "Documents");
    }

    [TestMethod]
    public async Task ConvertsDotExtensionAliasOnlyWithoutChangingInput()
    {
        const string expectedText = "Survoler quick Office preview";
        using DocumentSession session = CreateSession(".dot", OfficeFileKind.Doc);
        string docPath = Path.ChangeExtension(session.LocalPath, ".doc");
        try
        {
            using (WordDocument document = WordDocument.Create())
            {
                document.AddParagraph(expectedText);
                document.Save(docPath);
            }

            // Setting FibBase.fDot requires rewriting the WordDocument compound stream.
            // OfficeIMO.Core 3.3.0 exposes no public compound writer; no FIB flag is set.
            // This remains extension-routing coverage, not evidence of genuine DOT support.
            File.Move(docPath, session.LocalPath);
            await AssertConvertedPdfAsync(session, expectedText);
        }
        finally
        {
            File.Delete(docPath);
        }
    }

    private static DocumentSession CreateSession(string extension, OfficeFileKind kind)
    {
        string name = $"survoler-extended-test-{Guid.NewGuid():N}{extension}";
        Assert.IsTrue(OfficeFileKinds.TryFromFileName(name, out OfficeFileKind recognized),
            $"The filename classifier must recognize {extension}.");
        Assert.AreEqual(kind, recognized, $"Unexpected document kind for {extension}.");
        return new DocumentSession(Guid.NewGuid(), name, Path.Combine(Path.GetTempPath(), name), recognized);
    }

    private static void CopyFixture(string name, string destination) =>
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", name), destination);

    private static async Task AssertConvertedPdfAsync(DocumentSession session, string expectedText)
    {
        // Macro-enabled content types do not require VBA. These cases preview static content,
        // never install/run an add-in, and do not prove isolation of executable macro payloads.
        byte[] original = await File.ReadAllBytesAsync(session.LocalPath);
        try
        {
            using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(
                session, CancellationToken.None);
            byte[] bytes = await File.ReadAllBytesAsync(converted.Path);
            CollectionAssert.AreEqual("%PDF-"u8.ToArray(), bytes.Take(5).ToArray());
            PdfReadDocument pdf = PdfReadDocument.Open(converted.Path);
            Assert.IsGreaterThan(0, pdf.Pages.Count);
            StringAssert.Contains(pdf.ExtractText(), expectedText);

            PdfPageInteractionMap map = PdfPageInteractionMap.Create(bytes, 1);
            Assert.IsGreaterThan(0, map.TextRegions.Count);
            StringAssert.Contains(string.Concat(map.TextRegions.Select(region => region.Text)), expectedText);
        }
        finally
        {
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(session.LocalPath),
                "Conversion must leave the input byte-for-byte unchanged, even on failure.");
        }
    }
}
