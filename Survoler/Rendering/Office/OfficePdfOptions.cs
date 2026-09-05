using System;
using System.Collections.Generic;
using System.Linq;
using OfficeIMO.Drawing;
using OfficeIMO.Excel.Pdf;
using OfficeIMO.Pdf;
using OfficeIMO.PowerPoint.Pdf;
using OfficeIMO.Word.Pdf;

namespace Survoler.Rendering;

internal static class OfficePdfOptions
{
    internal static PdfResourcePolicy CreateResourcePolicy()
    {
        PdfResourcePolicy policy = PdfResourcePolicy.CreateDefault();
        policy.AllowDocumentFontEmbedding = true;
        return policy;
    }

    internal static void ApplyRenderingResources(
        WordPdfSaveOptions options,
        OfficePdfRenderingResources? resources)
    {
        options.UseRenderingProfile(resources?.Profile ?? OfficeRenderingProfile.Managed);
        ApplyFontSubstitutions(options.PdfOptions!, resources);
    }

    internal static void ApplyRenderingResources(
        ExcelPdfSaveOptions options,
        OfficePdfRenderingResources? resources)
    {
        options.UseRenderingProfile(resources?.Profile ?? OfficeRenderingProfile.Managed);
        ApplyFontSubstitutions(options.PdfOptions!, resources);
    }

    internal static void ApplyRenderingResources(
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

    private static void ApplyDefaultFont(PdfOptions options, OfficePdfRenderingResources resources)
    {
        if (resources.DefaultFontFamily is not { } familyName)
        {
            return;
        }

        OfficeFontFace[] faces = resources.Profile.Fonts.Faces
            .Where(face => string.Equals(face.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        OfficeFontFace? regular = faces.FirstOrDefault(face => face.Style == OfficeFontStyle.Regular);
        if (regular is null)
        {
            return;
        }

        byte[]? Face(OfficeFontStyle style) => faces.FirstOrDefault(face => face.Style == style)?.Data;

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
}
