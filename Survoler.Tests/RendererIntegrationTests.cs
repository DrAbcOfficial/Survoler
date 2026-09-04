using Survoler.Documents;
using Survoler.Rendering;

namespace Survoler.Tests;

[TestClass]
public sealed class RendererIntegrationTests
{
    [TestMethod]
    [DataRow("sample.doc", OfficeFileKind.Doc)]
    [DataRow("sample.docx", OfficeFileKind.Docx)]
    public async Task RendersWordSample(string fixtureName, OfficeFileKind kind)
    {
        using TestSession test = TestSession.Create(fixtureName, kind);
        var renderer = new WordPreviewRenderer();

        Assert.IsTrue(renderer.CanRender(kind));
        using IDocumentPreview preview = await renderer.CreateAsync(test.Session, CancellationToken.None);

        StringAssert.Contains(preview.Html, "In every seas");
        StringAssert.Contains(preview.Html, "Content-Security-Policy");
        StringAssert.Contains(preview.Html, "data-page-width-twips=");
        StringAssert.Contains(preview.Html, "data-page-height-twips=");
        StringAssert.Contains(preview.Html, "body .word-section");
    }

    [TestMethod]
    [DataRow("sample.xls", OfficeFileKind.Xls)]
    [DataRow("sample.xlsx", OfficeFileKind.Xlsx)]
    public async Task RendersSpreadsheetSample(string fixtureName, OfficeFileKind kind)
    {
        using TestSession test = TestSession.Create(fixtureName, kind);
        var renderer = new SpreadsheetPreviewRenderer();

        Assert.IsTrue(renderer.CanRender(kind));
        using IDocumentPreview preview = await renderer.CreateAsync(test.Session, CancellationToken.None);

        StringAssert.Contains(preview.Html, "Documents");
        StringAssert.Contains(preview.Html, "6");
        StringAssert.Contains(preview.Html, "user-scalable=yes");
        StringAssert.Contains(preview.Html, "touch-action:pan-x pan-y pinch-zoom");
        Assert.AreEqual(3, preview.NavigationItems.Count);

        for (int index = 0; index < preview.NavigationItems.Count; index++)
        {
            string html = await preview.SelectAsync(index, CancellationToken.None);
            StringAssert.Contains(html, "Content-Security-Policy");
            StringAssert.Contains(html, "user-scalable=yes");
        }
    }

    [TestMethod]
    [DataRow("sample.ppt", OfficeFileKind.Ppt)]
    [DataRow("sample.pptx", OfficeFileKind.Pptx)]
    public async Task RendersPresentationSample(string fixtureName, OfficeFileKind kind)
    {
        using TestSession test = TestSession.Create(fixtureName, kind);
        var renderer = new PresentationPreviewRenderer();

        Assert.IsTrue(renderer.CanRender(kind));
        using IDocumentPreview preview = await renderer.CreateAsync(test.Session, CancellationToken.None);

        StringAssert.Contains(preview.Html, "<svg");
        StringAssert.Contains(preview.Html, "Survoler Presentation");
        StringAssert.Contains(preview.Html, "data-slide-width-points=");
        StringAssert.Contains(preview.Html, "data-slide-height-points=");
        StringAssert.Contains(preview.Html, ".survoler-slide-page svg");
        Assert.AreEqual(2, preview.NavigationItems.Count);

        for (int index = 0; index < preview.NavigationItems.Count; index++)
        {
            string html = await preview.SelectAsync(index, CancellationToken.None);
            StringAssert.Contains(html, "Content-Security-Policy");
            StringAssert.Contains(html, "<svg");
        }
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
