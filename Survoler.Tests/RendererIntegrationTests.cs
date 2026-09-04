using Survoler.Documents;
using Survoler.Rendering;

namespace Survoler.Tests;

[TestClass]
public sealed class RendererIntegrationTests
{
    [TestMethod]
    public async Task RendersWordSample()
    {
        using TestSession test = TestSession.Create("sample.docx", OfficeFileKind.Docx);
        var renderer = new WordPreviewRenderer();

        using IDocumentPreview preview = await renderer.CreateAsync(test.Session, CancellationToken.None);

        StringAssert.Contains(preview.Html, "Survoler Word sample");
        StringAssert.Contains(preview.Html, "Content-Security-Policy");
    }

    [TestMethod]
    public async Task RendersSpreadsheetSample()
    {
        using TestSession test = TestSession.Create("sample.xlsx", OfficeFileKind.Xlsx);
        var renderer = new SpreadsheetPreviewRenderer();

        using IDocumentPreview preview = await renderer.CreateAsync(test.Session, CancellationToken.None);

        StringAssert.Contains(preview.Html, "Documents");
        StringAssert.Contains(preview.Html, "6");
        Assert.AreEqual(1, preview.NavigationItems.Count);
    }

    [TestMethod]
    public async Task RendersPresentationSample()
    {
        using TestSession test = TestSession.Create("sample.pptx", OfficeFileKind.Pptx);
        var renderer = new PresentationPreviewRenderer();

        using IDocumentPreview preview = await renderer.CreateAsync(test.Session, CancellationToken.None);

        StringAssert.Contains(preview.Html, "<svg");
        StringAssert.Contains(preview.Html, "Survoler Presentation");
        Assert.AreEqual(1, preview.NavigationItems.Count);
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

        public void Dispose()
        {
            Session.Dispose();
        }
    }
}
