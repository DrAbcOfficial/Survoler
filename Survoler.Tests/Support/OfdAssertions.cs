using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using OfficeIMO.Pdf;
using Survoler.Rendering;
using Survoler.Resources;

namespace Survoler.Tests;

internal static class OfdAssertions
{
    internal static void AssertTextOnly(ConvertedPdfDocument converted, params string[] markers)
    {
        var resources = new ResourceManager("Survoler.Resources.OfdStrings", typeof(Strings).Assembly);
        string warning, title;
        try
        {
            warning = resources.GetString("TextOnlyPreviewWarning", CultureInfo.CurrentUICulture)!;
            title = resources.GetString("TextOnlyPreviewTitle", CultureInfo.CurrentUICulture)!;
        }
        finally { resources.ReleaseAllResources(); }
        Assert.IsFalse(string.IsNullOrWhiteSpace(warning));
        Assert.IsFalse(string.IsNullOrWhiteSpace(title));
        Assert.AreEqual(warning, converted.Warning);
        byte[] bytes = File.ReadAllBytes(converted.Path);
        CollectionAssert.AreEqual("%PDF-"u8.ToArray(), bytes[..5]);
        PdfReadDocument pdf = PdfReadDocument.Open(converted.Path);
        Assert.IsGreaterThan(0, pdf.Pages.Count);
        Assert.IsTrue(Compact(pdf.Pages[0].ExtractText()).StartsWith(Compact(title), StringComparison.Ordinal));
        var mapped = new StringBuilder();
        var selected = new StringBuilder();
        for (int i = 0; i < pdf.Pages.Count; i++)
        {
            Assert.AreEqual(0, pdf.Pages[i].GetImages().Count);
            PdfPageInteractionMap map = PdfPageInteractionMap.Create(bytes, i + 1);
            Assert.IsGreaterThan(0, map.TextRegions.Count);
            mapped.Append(string.Concat(map.TextRegions.Select(r => r.Text)));
            var (width, height) = pdf.Pages[i].GetPageSize();
            selected.Append(map.GetSelectedText(0, 0, width, height));
        }
        foreach (string marker in markers.Prepend(title))
        {
            StringAssert.Contains(Compact(pdf.ExtractText()), Compact(marker));
            StringAssert.Contains(Compact(mapped.ToString()), Compact(marker));
            StringAssert.Contains(Compact(selected.ToString()), Compact(marker));
        }
    }

    internal static string Compact(string text) => Regex.Replace(text, @"\s", "");

    internal sealed class UnusedRenderer : IPdfPageRenderer
    {
        public int PageCount => 1;
        public Task<Avalonia.Media.Imaging.Bitmap> RenderPageAsync(int index, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Only warning propagation is tested.");
        public void Dispose() { }
    }
}
