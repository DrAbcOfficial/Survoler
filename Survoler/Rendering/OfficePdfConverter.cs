using System;
using System.Collections.Generic;
using System.IO;
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

                throw new DocumentOpenException("OfficeIMO could not convert this document to PDF.");
            }

            return new ConvertedPdfDocument(pdfPath, CreateWarningText(session, result));
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
            OfficeFileKind.Xls or OfficeFileKind.Xlsx =>
                ConvertSpreadsheet(session.LocalPath, pdfPath, resources),
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
        options.ResourcePolicy = CreateResourcePolicy();
        ApplyRenderingResources(options, resources);
        return document.TrySaveAsPdf(pdfPath, options);
    }

    private static PdfSaveResult ConvertSpreadsheet(
        string inputPath,
        string pdfPath,
        OfficePdfRenderingResources? resources)
    {
        var loadOptions = new ExcelLoadOptions
        {
            AccessMode = DocumentAccessMode.ReadOnly,
            PersistenceMode = DocumentPersistenceMode.Explicit,
            MaxInputBytes = PreviewLimits.MaxInputBytes,
            PackageSecurity = PreviewLimits.CreatePackageSecurity()
        };
        using ExcelDocument document = ExcelDocument.Load(inputPath, loadOptions);

        var options = new ExcelPdfSaveOptions();
        options.UseProfile(PdfExportProfile.Faithful);
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

        foreach (KeyValuePair<string, string> substitution in resources.FontSubstitutions)
        {
            options.RegisterFontFamilySubstitution(
                substitution.Key,
                substitution.Value,
                PdfFontFamilySubstitutionImpact.LayoutSensitive);
        }
    }

    private static string? CreateWarningText(DocumentSession session, PdfSaveResult result)
    {
        var warnings = new List<string>(3);
        if (session.IsLegacy)
        {
            warnings.Add("Legacy Office conversion may omit unsupported binary content.");
        }

        if (result.HasLoss)
        {
            warnings.Add("OfficeIMO detected content or layout that may not be reproduced exactly.");
        }
        else if (result.HasWarnings)
        {
            warnings.Add("OfficeIMO applied font or layout substitutions while creating the PDF.");
        }

        return warnings.Count == 0 ? null : string.Join(" ", warnings);
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
