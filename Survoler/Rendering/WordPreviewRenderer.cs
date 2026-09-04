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
    private const string FixedPageCss = """
        html{min-height:100%;background:#e2ded5}
        body{box-sizing:border-box;width:max-content;min-width:100%;min-height:100vh;margin:0;padding:24px;background:#e2ded5}
        body .word-section{box-sizing:border-box;max-width:none;margin:0 auto 24px;background:#fff;box-shadow:0 12px 36px #0005}
        body .word-section:last-child{margin-bottom:0}
        """;

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

        var htmlOptions = WordToHtmlOptions.CreatePrintReviewProfile(OfficeVisualThemeKind.Plain);
        htmlOptions.TrackedChangePolicy = WordTrackedChangeExportPolicy.Final;
        htmlOptions.FieldPolicy = WordFieldExportPolicy.VisibleResult;
        htmlOptions.ExportComments = false;
        htmlOptions.ExportHeadersAndFooters = true;
        htmlOptions.IncludeCustomProperties = false;
        htmlOptions.IncludeSectionMetadata = true;
        htmlOptions.IncludeDrawingReviewMetadata = false;
        htmlOptions.UseSharedDocumentShell = false;
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

        return new StaticDocumentPreview(PreviewHtmlSanitizer.Sanitize(
            html,
            additionalCss: FixedPageCss));
    }
}
