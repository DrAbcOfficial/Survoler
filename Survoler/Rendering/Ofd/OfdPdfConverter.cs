using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Survoler.Documents;
using Survoler.Resources;
using static Survoler.Rendering.OfdXml;

namespace Survoler.Rendering;

/// <summary>Writes a deliberately restricted OFD subset to a temporary, selectable PDF.</summary>
public sealed class OfdPdfConverter
{
    public async Task<ConvertedPdfDocument> ConvertAsync(
        DocumentSession session, OfficePdfRenderingResources? resources, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(session);
        token.ThrowIfCancellationRequested();
        string directory = Path.Combine(Path.GetTempPath(), "survoler");
        Directory.CreateDirectory(directory);
        var result = new ConvertedPdfDocument(Path.Combine(directory, $"{Guid.NewGuid():N}.pdf"));
        try
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (new FileInfo(session.LocalPath).Length > 64L * 1024 * 1024)
                    throw Invalid("ConversionInputTooLarge");
                using var package = new OfdPackage(session.LocalPath, token);
                try
                {
                    using var renderer = new OfdPdfRenderer(package, resources, token);
                    renderer.Write(result.Path);
                    result.Warning = renderer.Warning;
                }
                catch (NotSupportedException exception) when (exception.Data.Contains(OfdStrings.DiagnosticMarker))
                {
                    // Start a separate text document, never overlay a failed/partial page rendering.
                    File.Delete(result.Path);
                    var pages = OfdTextExtractor.Extract(package, token);
                    if (pages is null) throw;
                    OfdTextPdfWriter.Write(pages, result.Path, resources, token);
                    result.Warning = OfdStrings.Get("TextOnlyPreviewWarning");
                }
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception exception)
        {
            result.Dispose();
            if (exception is System.Xml.XmlException)
                throw new DocumentOpenException(OfdStrings.Get("InvalidOfdXml"));
            if (exception is InvalidDataException or NotSupportedException)
                throw new DocumentOpenException(exception.Data.Contains(OfdStrings.DiagnosticMarker)
                    ? exception.Message : OfdStrings.Get("InvalidOfdPackage"));
            throw;
        }
    }
}
