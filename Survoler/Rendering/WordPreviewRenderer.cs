using System.Threading;
using System.Threading.Tasks;
using OfficeIMO;
using OfficeIMO.Drawing;
using OfficeIMO.Word;
using OfficeIMO.Word.Html;
using Survoler.Documents;

namespace Survoler.Rendering;

public sealed class WordPreviewRenderer : IDocumentPreviewRenderer
{
    public bool CanRender(OfficeFileKind kind) =>
        kind is OfficeFileKind.Doc or OfficeFileKind.Docx;

    public async Task<IDocumentPreview> CreateAsync(
        DocumentSession session,
        CancellationToken cancellationToken)
    {
        var loadOptions = new WordLoadOptions
        {
            AccessMode = DocumentAccessMode.ReadOnly,
            PersistenceMode = DocumentPersistenceMode.Explicit,
            MaxInputBytes = PreviewLimits.MaxInputBytes,
            PackageSecurity = PreviewLimits.CreatePackageSecurity()
        };

        using WordDocument document = await WordDocument.LoadAsync(
            session.LocalPath,
            loadOptions,
            cancellationToken);

        var htmlOptions = WordToHtmlOptions.CreateSemanticDocumentProfile(OfficeVisualThemeKind.Plain);
        htmlOptions.TrackedChangePolicy = WordTrackedChangeExportPolicy.Final;
        htmlOptions.FieldPolicy = WordFieldExportPolicy.VisibleResult;
        htmlOptions.ExportComments = false;
        htmlOptions.ExportHeadersAndFooters = false;
        htmlOptions.IncludeCustomProperties = false;
        htmlOptions.IncludeSectionMetadata = false;
        htmlOptions.IncludeDrawingReviewMetadata = false;
        htmlOptions.EmbedImagesAsBase64 = true;
        htmlOptions.MaxDocumentElements = 500_000;
        htmlOptions.MaxEmbeddedImageBytes = PreviewLimits.MaxImageBytes;
        htmlOptions.MaxTotalEmbeddedImageBytes = PreviewLimits.MaxTotalImageBytes;
        htmlOptions.MaxOutputCharacters = PreviewLimits.MaxWordHtmlCharacters;
        htmlOptions.AdditionalLinkTags.Clear();
        htmlOptions.AdditionalMetaTags.Clear();

        string html = await Task.Run(
            () => document.ToHtml(htmlOptions),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return new StaticDocumentPreview(PreviewHtmlSanitizer.Sanitize(html));
    }
}
