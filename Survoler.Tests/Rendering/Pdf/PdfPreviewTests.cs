using Avalonia.Media.Imaging;
using OfficeIMO.Pdf;
using OfficeIMO.Word;
using Survoler.Documents;
using Survoler.Rendering;

namespace Survoler.Tests;

[TestClass]
public sealed class PdfPreviewTests
{
    private const string FixtureText = "Direct PDF preview preserves searchable text";

    [TestMethod]
    public async Task CoordinatorAcceptsUppercasePdfAndPreservesBytes()
    {
        using ConvertedPdfDocument source = await CreatePdfFixtureAsync();
        byte[] bytes = await File.ReadAllBytesAsync(source.Path);
        CollectionAssert.AreEqual("%PDF-"u8.ToArray(), bytes[..5]);
        using var file = FakeStorageFile.Create("report.PDF", bytes);
        using var coordinator = new DocumentOpenCoordinator();

        DocumentSession? session = await coordinator.OpenAsync(file);

        Assert.IsNotNull(session);
        Assert.AreEqual(OfficeFileKind.Pdf, session.Kind);
        Assert.AreEqual("report.PDF", session.SourceName);
        Assert.AreNotEqual(source.Path, session.LocalPath);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(session.LocalPath));
        coordinator.Dispose();
        Assert.IsFalse(File.Exists(session.LocalPath));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(source.Path));
        await using Stream input = await file.OpenReadAsync();
        Assert.AreEqual(bytes.Length, input.Length);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("%")]
    [DataRow("%P")]
    [DataRow("%PD")]
    [DataRow("%PDF")]
    [DataRow("%pdf-1.7")]
    [DataRow("not a PDF")]
    [DataRow(" %PDF-1.7")]
    public async Task CoordinatorRejectsEmptyTruncatedOrWrongHeader(string content)
    {
        using var file = FakeStorageFile.Create("report.PDF", System.Text.Encoding.ASCII.GetBytes(content));
        using var coordinator = new DocumentOpenCoordinator();

        DocumentOpenException exception = await Assert.ThrowsExactlyAsync<DocumentOpenException>(
            () => coordinator.OpenAsync(file));

        Assert.AreEqual("The file does not have a valid PDF header.", exception.Message);
    }

    [TestMethod]
    [DataRow("%PDF-")]
    [DataRow("%PDF-1.7\n")]
    public async Task CoordinatorOnlyValidatesHeaderNotPdfStructure(string header)
    {
        using var file = FakeStorageFile.Create("header.PDF", System.Text.Encoding.ASCII.GetBytes(header));
        using var coordinator = new DocumentOpenCoordinator();
        DocumentSession? session = await coordinator.OpenAsync(file);
        Assert.IsNotNull(session);
        Assert.AreEqual(OfficeFileKind.Pdf, session.Kind);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IndependentCopiesAndPreviewRespectSessionOwnership(bool disposeSessionFirst)
    {
        using ConvertedPdfDocument source = await CreatePdfFixtureAsync();
        byte[] bytes = await File.ReadAllBytesAsync(source.Path);
        using var file = FakeStorageFile.Create("report.PDF", bytes);
        using var coordinator = new DocumentOpenCoordinator();
        DocumentSession? session = await coordinator.OpenAsync(file);
        Assert.IsNotNull(session);
        using ConvertedPdfDocument copy = await ConvertedPdfDocument.CopyFromAsync(session.LocalPath, CancellationToken.None);
        using ConvertedPdfDocument secondCopy = await ConvertedPdfDocument.CopyFromAsync(session.LocalPath, CancellationToken.None);
        Assert.AreNotEqual(session.LocalPath, copy.Path);
        Assert.AreNotEqual(copy.Path, secondCopy.Path);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(copy.Path));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(secondCopy.Path));

        var renderer = new ThrowingPageRenderer(new InvalidOperationException("Rendering is not used by this test."));
        using var preview = new PdfDocumentPreview(renderer, copy);
        secondCopy.Dispose();
        secondCopy.Dispose();
        Assert.IsFalse(File.Exists(secondCopy.Path));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(session.LocalPath));

        if (disposeSessionFirst)
        {
            coordinator.Dispose();
            Assert.IsFalse(File.Exists(session.LocalPath));
        }
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(copy.Path));
        StringAssert.Contains(PdfReadDocument.Open(copy.Path).ExtractText(), FixtureText);
        PdfPageInteractionMap? map = await preview.GetInteractionMapAsync(0, CancellationToken.None);
        Assert.IsNotNull(map);
        Assert.IsGreaterThan(0, map.TextRegions.Count);
        StringAssert.Contains(string.Concat(map.TextRegions.Select(region => region.Text)), FixtureText);
        Assert.AreEqual(0, renderer.RenderCalls);

        preview.Dispose();
        preview.Dispose();
        Assert.AreEqual(1, renderer.DisposeCalls);
        Assert.IsFalse(File.Exists(copy.Path));
        if (!disposeSessionFirst)
        {
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(session.LocalPath));
            coordinator.Dispose();
            Assert.IsFalse(File.Exists(session.LocalPath));
        }
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(source.Path));
    }

    [TestMethod]
    public async Task CopyHonorsPreCancellationWithoutTouchingSource()
    {
        using ConvertedPdfDocument source = await CreatePdfFixtureAsync();
        byte[] bytes = await File.ReadAllBytesAsync(source.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException exception = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => ConvertedPdfDocument.CopyFromAsync(source.Path, cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(source.Path));
    }

    [TestMethod]
    public async Task CopyRejectsMissingSource()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"survoler-missing-{Guid.NewGuid():N}.pdf");
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => ConvertedPdfDocument.CopyFromAsync(missingPath, CancellationToken.None));
        Assert.IsFalse(File.Exists(missingPath));
    }

    [TestMethod]
    public async Task DirectRouteBypassesConversionAndCleansCopyWhenFactoryFails()
    {
        using ConvertedPdfDocument source = await CreatePdfFixtureAsync();
        byte[] bytes = await File.ReadAllBytesAsync(source.Path);
        using var file = FakeStorageFile.Create("report.PDF", bytes);
        using var coordinator = new DocumentOpenCoordinator();
        DocumentSession? session = await coordinator.OpenAsync(file);
        Assert.IsNotNull(session);
        var sentinel = new InvalidOperationException("Factory failure sentinel");
        using var cancellation = new CancellationTokenSource();
        var factory = new RecordingFactory(async (path, token) =>
        {
            Assert.AreEqual(cancellation.Token, token);
            Assert.AreNotEqual(session.LocalPath, path);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token));
            throw sentinel;
        });
        // The real converter does not support Pdf, so reaching the factory proves bypass.
        var sut = new OfficePdfPreviewRenderer(new OfficePdfConverter(), factory);
        Assert.IsTrue(sut.CanRender(OfficeFileKind.Pdf));

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => sut.CreateAsync(session, cancellation.Token));

        Assert.AreSame(sentinel, exception);
        Assert.AreEqual(1, factory.OpenCalls);
        Assert.IsNotNull(factory.OpenedPath);
        Assert.IsFalse(File.Exists(factory.OpenedPath));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(session.LocalPath));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(source.Path));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InitialRenderFailureOrCancellationDisposesRendererAndCopy(bool cancel)
    {
        using ConvertedPdfDocument source = await CreatePdfFixtureAsync();
        byte[] bytes = await File.ReadAllBytesAsync(source.Path);
        using var file = FakeStorageFile.Create("report.PDF", bytes);
        using var coordinator = new DocumentOpenCoordinator();
        DocumentSession? session = await coordinator.OpenAsync(file);
        Assert.IsNotNull(session);
        using var cancellation = new CancellationTokenSource();
        Exception sentinel = cancel
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("Initial render failure sentinel");
        var renderer = new ThrowingPageRenderer(sentinel, cancel ? cancellation : null);
        var factory = new RecordingFactory(async (path, token) =>
        {
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, token));
            return renderer;
        });
        var sut = new OfficePdfPreviewRenderer(new OfficePdfConverter(), factory);

        Exception exception = cancel
            ? await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => sut.CreateAsync(session, cancellation.Token))
            : await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sut.CreateAsync(session, cancellation.Token));

        Assert.AreSame(sentinel, exception);
        Assert.AreEqual(1, factory.OpenCalls);
        Assert.AreEqual(1, renderer.RenderCalls);
        Assert.AreEqual(0, renderer.RenderedIndex);
        Assert.AreEqual(cancellation.Token, renderer.RenderToken);
        Assert.AreEqual(1, renderer.DisposeCalls);
        Assert.IsNotNull(factory.OpenedPath);
        Assert.IsFalse(File.Exists(factory.OpenedPath));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(session.LocalPath));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(source.Path));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DirectRouteDoesNotOpenFactoryForMissingOrPreCanceledInput(bool cancel)
    {
        string path = Path.Combine(Path.GetTempPath(), $"survoler-missing-{Guid.NewGuid():N}.pdf");
        using var session = new DocumentSession(Guid.NewGuid(), "missing.PDF", path, OfficeFileKind.Pdf);
        using var cancellation = new CancellationTokenSource();
        if (cancel)
        {
            cancellation.Cancel();
        }
        var factory = new RecordingFactory((_, _) => throw new AssertFailedException("Factory must not be opened."));
        var sut = new OfficePdfPreviewRenderer(new OfficePdfConverter(), factory);

        if (cancel)
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => sut.CreateAsync(session, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => sut.CreateAsync(session, cancellation.Token));
        }
        Assert.AreEqual(0, factory.OpenCalls);
        Assert.IsNull(factory.OpenedPath);
        Assert.IsFalse(File.Exists(path));
    }

    private static async Task<ConvertedPdfDocument> CreatePdfFixtureAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"survoler-pdf-fixture-{Guid.NewGuid():N}.docx");
        using var session = new DocumentSession(Guid.NewGuid(), "fixture.docx", path, OfficeFileKind.Docx);
        using (WordDocument word = WordDocument.Create())
        {
            word.AddParagraph(FixtureText);
            word.Save(path);
        }
        return await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
    }

    private sealed class RecordingFactory(Func<string, CancellationToken, Task<IPdfPageRenderer>> open)
        : IPdfPageRendererFactory
    {
        public int OpenCalls { get; private set; }
        public string? OpenedPath { get; private set; }

        public Task<IPdfPageRenderer> OpenAsync(string pdfPath, CancellationToken cancellationToken)
        {
            OpenCalls++;
            OpenedPath = pdfPath;
            return open(pdfPath, cancellationToken);
        }
    }

    private sealed class ThrowingPageRenderer(Exception failure, CancellationTokenSource? cancellation = null)
        : IPdfPageRenderer
    {
        public int PageCount => 1;
        public int RenderCalls { get; private set; }
        public int RenderedIndex { get; private set; } = -1;
        public CancellationToken RenderToken { get; private set; }
        public int DisposeCalls { get; private set; }

        public Task<Bitmap> RenderPageAsync(int index, CancellationToken cancellationToken)
        {
            RenderCalls++;
            RenderedIndex = index;
            RenderToken = cancellationToken;
            cancellation?.Cancel();
            return Task.FromException<Bitmap>(failure);
        }

        public void Dispose() => DisposeCalls++;
    }
}
