using System;
using System.Threading;
using System.Threading.Tasks;
using Survoler.Documents;

namespace Survoler.Rendering;

public sealed class OfficePdfPreviewRenderer : IDocumentPreviewRenderer
{
    private readonly OfficePdfConverter _converter;
    private readonly IPdfPageRendererFactory _pageRendererFactory;

    public OfficePdfPreviewRenderer(
        OfficePdfConverter converter,
        IPdfPageRendererFactory pageRendererFactory)
    {
        _converter = converter;
        _pageRendererFactory = pageRendererFactory;
    }

    public bool CanRender(OfficeFileKind kind) => Enum.IsDefined(kind);

    public async Task<IDocumentPreview> CreateAsync(
        DocumentSession session,
        CancellationToken cancellationToken)
    {
        ConvertedPdfDocument converted = session.Kind == OfficeFileKind.Pdf
            ? await ConvertedPdfDocument.CopyFromAsync(session.LocalPath, cancellationToken)
            : await _converter.ConvertAsync(session, cancellationToken);
        IPdfPageRenderer? renderer = null;
        PdfDocumentPreview? preview = null;

        try
        {
            renderer = await _pageRendererFactory.OpenAsync(converted.Path, cancellationToken);
            preview = new PdfDocumentPreview(renderer, converted);
            renderer = null;
            await preview.InitializeAsync(cancellationToken);
            return preview;
        }
        catch
        {
            preview?.Dispose();
            renderer?.Dispose();
            if (preview is null)
            {
                converted.Dispose();
            }
            throw;
        }
    }
}
