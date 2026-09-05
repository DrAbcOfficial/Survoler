using OfficeIMO.Pdf;
using OfficeIMO.Word;
using Survoler.Documents;
using Survoler.Rendering;

namespace Survoler.Tests;

[TestClass]
public sealed class OfficePdfConversionTests
{
    [TestMethod]
    [DataRow("sample.xls", OfficeFileKind.Xls, "Documents")]
    [DataRow("sample.xlsx", OfficeFileKind.Xlsx, "Documents")]
    [DataRow("sample.ppt", OfficeFileKind.Ppt, "Survoler Presentation")]
    [DataRow("sample.pptx", OfficeFileKind.Pptx, "Survoler Presentation")]
    public async Task ConvertsOfficeSampleToReadablePdf(
        string fixtureName,
        OfficeFileKind kind,
        string expectedText)
    {
        using TestSession test = TestSession.Create(fixtureName, kind);
        await AssertConvertedPdfAsync(test.Session, expectedText);
    }

    [TestMethod]
    [DataRow(".doc", OfficeFileKind.Doc)]
    [DataRow(".docx", OfficeFileKind.Docx)]
    public async Task ConvertsWordToReadablePdf(string extension, OfficeFileKind kind)
    {
        const string expectedText = "Survoler quick Office preview";
        using TestSession test = TestSession.CreateWord(extension, kind, expectedText);
        await AssertConvertedPdfAsync(test.Session, expectedText);
    }

    private static async Task AssertConvertedPdfAsync(
        DocumentSession session,
        string expectedText)
    {
        var converter = new OfficePdfConverter();
        using ConvertedPdfDocument converted = await converter.ConvertAsync(
            session,
            CancellationToken.None);

        byte[] header = new byte[5];
        await using (FileStream stream = File.OpenRead(converted.Path))
        {
            await stream.ReadExactlyAsync(header);
        }

        CollectionAssert.AreEqual("%PDF-"u8.ToArray(), header);
        PdfReadDocument pdf = PdfReadDocument.Open(converted.Path);
        Assert.IsGreaterThan(0, pdf.Pages.Count);
        StringAssert.Contains(pdf.ExtractText(), expectedText);

    }

    private sealed class TestSession : IDisposable
    {
        private TestSession(DocumentSession session)
        {
            Session = session;
        }

        public DocumentSession Session { get; }

        public static TestSession Create(string fixtureName, OfficeFileKind kind)
        {
            string source = Path.Combine(AppContext.BaseDirectory, "TestData", fixtureName);
            string destination = Path.Combine(
                Path.GetTempPath(),
                $"survoler-test-{Guid.NewGuid():N}{Path.GetExtension(fixtureName)}");
            File.Copy(source, destination);
            return new TestSession(new DocumentSession(Guid.NewGuid(), fixtureName, destination, kind));
        }

        public static TestSession CreateWord(
            string extension,
            OfficeFileKind kind,
            string text)
        {
            string destination = Path.Combine(
                Path.GetTempPath(),
                $"survoler-test-{Guid.NewGuid():N}{extension}");
            using (WordDocument document = WordDocument.Create())
            {
                document.AddParagraph(text);
                document.Save(destination);
            }

            return new TestSession(new DocumentSession(
                Guid.NewGuid(),
                $"generated{extension}",
                destination,
                kind));
        }

        public void Dispose()
        {
            Session.Dispose();
        }
    }
}
