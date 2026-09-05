using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OfficeIMO;
using OfficeIMO.Drawing;
using OfficeIMO.Excel;
using OfficeIMO.Excel.Pdf;
using OfficeIMO.Pdf;
using OfficeIMO.PowerPoint;
using OfficeIMO.PowerPoint.Pdf;
using OfficeIMO.Word;
using OfficeIMO.Word.Pdf;
using Survoler.Documents;

namespace Survoler.Rendering;

public sealed class OfficePdfConverter
{
    private readonly IOfficePdfRenderingResourcesProvider? _resourcesProvider;

    public OfficePdfConverter(IOfficePdfRenderingResourcesProvider? resourcesProvider = null)
    {
        _resourcesProvider = resourcesProvider;
    }

    public async Task<ConvertedPdfDocument> ConvertAsync(
        DocumentSession session,
        CancellationToken cancellationToken)
    {
        if (session.Kind == OfficeFileKind.Ofd)
        {
            OfficePdfRenderingResources? resources = await Task.Run(
                () => _resourcesProvider?.GetResources(), cancellationToken);
            return await new OfdPdfConverter().ConvertAsync(
                session, resources, cancellationToken);
        }

        string cacheDirectory = Path.Combine(Path.GetTempPath(), "survoler");
        Directory.CreateDirectory(cacheDirectory);
        string pdfPath = Path.Combine(cacheDirectory, $"{session.Id:N}.pdf");

        try
        {
            PdfSaveResult result = await Task.Run(
                () => Convert(session, pdfPath, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!result.Succeeded)
            {
                if (result.TextEncodingDiagnostics.Count > 0)
                {
                    throw new DocumentOpenException(
                        "This device has no PDF-embeddable font for some document characters.");
                }

                throw new DocumentOpenException("The document could not be converted to PDF.");
            }

            return new ConvertedPdfDocument(pdfPath);
        }
        catch
        {
            TryDelete(pdfPath);
            throw;
        }
    }

    private PdfSaveResult Convert(
        DocumentSession session,
        string pdfPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OfficePdfRenderingResources? resources = _resourcesProvider?.GetResources();

        PdfSaveResult result = session.Kind switch
        {
            OfficeFileKind.Doc or OfficeFileKind.Docx =>
                ConvertWord(session.LocalPath, pdfPath, resources),
            OfficeFileKind.Xls or OfficeFileKind.Xlsx or OfficeFileKind.Csv =>
                ConvertSpreadsheet(session, pdfPath, resources, cancellationToken),
            OfficeFileKind.Ppt or OfficeFileKind.Pptx =>
                ConvertPresentation(session.LocalPath, pdfPath, resources),
            _ => throw new NotSupportedException("No PDF converter is available for this file.")
        };

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static PdfSaveResult ConvertWord(
        string inputPath,
        string pdfPath,
        OfficePdfRenderingResources? resources)
    {
        var loadOptions = new WordLoadOptions
        {
            AccessMode = DocumentAccessMode.ReadOnly,
            PersistenceMode = DocumentPersistenceMode.Explicit,
            MaxInputBytes = PreviewLimits.MaxInputBytes,
            PackageSecurity = PreviewLimits.CreatePackageSecurity()
        };
        using WordDocument document = WordDocument.Load(inputPath, loadOptions);

        var options = new WordPdfSaveOptions();
        options.UseProfile(PdfExportProfile.Faithful);
        options.TextFallbacks |= PdfTextFallbackFeatures.MultilingualFonts;
        options.ResourcePolicy = CreateResourcePolicy();
        ApplyRenderingResources(options, resources);
        return document.TrySaveAsPdf(pdfPath, options);
    }

    private static PdfSaveResult ConvertSpreadsheet(
        DocumentSession session,
        string pdfPath,
        OfficePdfRenderingResources? resources,
        CancellationToken cancellationToken)
    {
        var loadOptions = new ExcelLoadOptions
        {
            AccessMode = DocumentAccessMode.ReadOnly,
            PersistenceMode = DocumentPersistenceMode.Explicit,
            MaxInputBytes = PreviewLimits.MaxInputBytes,
            PackageSecurity = PreviewLimits.CreatePackageSecurity()
        };
        using ExcelDocument document = session.Kind == OfficeFileKind.Csv
            ? CsvWorkbookReader.Load(session.LocalPath, cancellationToken)
            : ExcelDocument.Load(session.LocalPath, loadOptions);

        var options = new ExcelPdfSaveOptions();
        options.UseProfile(PdfExportProfile.Faithful);
        options.TextFallbacks |= PdfTextFallbackFeatures.MultilingualFonts;
        options.ResourcePolicy = CreateResourcePolicy();
        ApplyRenderingResources(options, resources);
        return document.TrySaveAsPdf(pdfPath, options);
    }

    private static PdfSaveResult ConvertPresentation(
        string inputPath,
        string pdfPath,
        OfficePdfRenderingResources? resources)
    {
        var loadOptions = new PowerPointLoadOptions
        {
            AccessMode = DocumentAccessMode.ReadOnly,
            PersistenceMode = DocumentPersistenceMode.Explicit,
            MaxInputBytes = PreviewLimits.MaxInputBytes,
            PackageSecurity = PreviewLimits.CreatePackageSecurity()
        };
        using PowerPointPresentation document = PowerPointPresentation.Load(inputPath, loadOptions);

        var options = new PowerPointPdfSaveOptions();
        options.UseProfile(PdfExportProfile.Faithful);
        options.TextFallbacks |= PdfTextFallbackFeatures.MultilingualFonts;
        options.ResourcePolicy = CreateResourcePolicy();
        ApplyRenderingResources(options, resources);
        return document.TrySaveAsPdf(pdfPath, options);
    }

    private static PdfResourcePolicy CreateResourcePolicy()
    {
        PdfResourcePolicy policy = PdfResourcePolicy.CreateDefault();
        policy.AllowDocumentFontEmbedding = true;
        return policy;
    }

    private static void ApplyRenderingResources(
        WordPdfSaveOptions options,
        OfficePdfRenderingResources? resources)
    {
        options.UseRenderingProfile(resources?.Profile ?? OfficeRenderingProfile.Managed);
        ApplyFontSubstitutions(options.PdfOptions!, resources);
    }

    private static void ApplyRenderingResources(
        ExcelPdfSaveOptions options,
        OfficePdfRenderingResources? resources)
    {
        options.UseRenderingProfile(resources?.Profile ?? OfficeRenderingProfile.Managed);
        ApplyFontSubstitutions(options.PdfOptions!, resources);
    }

    private static void ApplyRenderingResources(
        PowerPointPdfSaveOptions options,
        OfficePdfRenderingResources? resources)
    {
        options.UseRenderingProfile(resources?.Profile ?? OfficeRenderingProfile.Managed);
        ApplyFontSubstitutions(options.PdfOptions!, resources);
    }

    private static void ApplyFontSubstitutions(
        PdfOptions options,
        OfficePdfRenderingResources? resources)
    {
        if (resources is null)
        {
            return;
        }

        ApplyDefaultFont(options, resources);

        foreach (KeyValuePair<string, string> substitution in resources.FontSubstitutions)
        {
            options.RegisterFontFamilySubstitution(
                substitution.Key,
                substitution.Value,
                PdfFontFamilySubstitutionImpact.LayoutSensitive);
        }
    }

    private static void ApplyDefaultFont(
        PdfOptions options,
        OfficePdfRenderingResources resources)
    {
        if (resources.DefaultFontFamily is not { } familyName)
        {
            return;
        }

        OfficeFontFace[] faces = resources.Profile.Fonts.Faces
            .Where(face => string.Equals(
                face.FamilyName,
                familyName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        OfficeFontFace? regular = faces.FirstOrDefault(
            face => face.Style == OfficeFontStyle.Regular);
        if (regular is null)
        {
            return;
        }

        byte[]? Face(OfficeFontStyle style) =>
            faces.FirstOrDefault(face => face.Style == style)?.Data;

        var defaultFamily = new PdfEmbeddedFontFamily(
            familyName,
            regular.Data,
            Face(OfficeFontStyle.Bold),
            Face(OfficeFontStyle.Italic),
            Face(OfficeFontStyle.Bold | OfficeFontStyle.Italic));

        options.UseFontFamily(defaultFamily);
        options.RegisterFontFamily(PdfStandardFont.TimesRoman, defaultFamily);
        options.RegisterFontFamily(PdfStandardFont.Courier, defaultFamily);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
