using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OfficeIMO;
using OfficeIMO.PowerPoint;
using Survoler.Documents;

namespace Survoler.Rendering;

public sealed class PresentationPreviewRenderer : IDocumentPreviewRenderer
{
    public bool CanRender(OfficeFileKind kind) =>
        kind is OfficeFileKind.Ppt or OfficeFileKind.Pptx;

    public async Task<IDocumentPreview> CreateAsync(
        DocumentSession session,
        CancellationToken cancellationToken)
    {
        var loadOptions = new PowerPointLoadOptions
        {
            AccessMode = DocumentAccessMode.ReadOnly,
            PersistenceMode = DocumentPersistenceMode.Explicit,
            MaxInputBytes = PreviewLimits.MaxInputBytes,
            PackageSecurity = PreviewLimits.CreatePackageSecurity()
        };

        PowerPointPresentation presentation = await PowerPointPresentation.LoadAsync(
            session.LocalPath,
            loadOptions,
            cancellationToken);

        try
        {
            PowerPointSlide[] visibleSlides = presentation.Slides
                .Where(slide => !slide.Hidden)
                .ToArray();

            if (visibleSlides.Length == 0)
            {
                throw new DocumentOpenException("This presentation has no visible slides.");
            }

            var preview = new PresentationDocumentPreview(presentation, visibleSlides);
            await preview.InitializeAsync(cancellationToken);
            return preview;
        }
        catch
        {
            presentation.Dispose();
            throw;
        }
    }
}
