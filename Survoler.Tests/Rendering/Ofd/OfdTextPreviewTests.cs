using System.Globalization;
using System.IO.Compression;
using System.Text;
using OfficeIMO.Pdf;
using SkiaSharp;
using Survoler.Documents;
using Survoler.Rendering;
using static Survoler.Tests.OfdAssertions;

namespace Survoler.Tests;

[TestClass]
public sealed class OfdTextPreviewTests
{
    private const string Ns = "http://www.ofdspec.org/2016";
    private const string Text = "<TextObject ID='10' Boundary='0 0 80 30' Font='1' Size='4'><TextCode X='0' Y='8'>ReadableBody</TextCode></TextObject>";

    [TestMethod]
    public async Task MultilineAndLongWrappedTextSurvivePageBreaksWithoutLoss()
    {
        string[] lines = Enumerable.Range(0, 110).Select(i => $"Line{i:D3} " + new string('W', 100) + $" Tail{i:D3}").ToArray();
        string body = string.Join("\r\n", lines[..55]) + "\n" + string.Join("\n", lines[55..]);
        using DocumentSession session = Session(Parts(Text.Replace("ReadableBody", body)));
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        Assert.IsGreaterThan(1, PdfReadDocument.Open(converted.Path).Pages.Count);
        AssertTextOnly(converted, body);
    }

    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("fr-FR")]
    public async Task WarningAndPdfHeaderAreExplicitInChineseAndFallbackEnglish(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using DocumentSession session = Session(Parts(Text));
            using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
            AssertTextOnly(converted, "ReadableBody");
            StringAssert.Contains(converted.Warning!, culture == "zh-CN" ? "\u7eaf\u6587\u5b57" : "Text-only");
            StringAssert.Contains(converted.Warning!, culture == "zh-CN" ? "\u672a\u9a8c\u8bc1\u6570\u5b57\u7b7e\u540d" : "have not been verified");
            using IDocumentPreview preview = new PdfDocumentPreview(new UnusedRenderer(), converted);
            Assert.AreEqual(converted.Warning, preview.Warning);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [TestMethod]
    [DataRow("<CGTransform CodePosition='0' CodeCount='2' GlyphCount='1'><Glyphs>123</Glyphs></CGTransform>")]
    [DataRow("<Clips/>")]
    [DataRow("<FillColor><AxialShd/></FillColor>")]
    public async Task UnsupportedBodyGraphicsPreserveUnicodeText(string feature)
    {
        string text = Text.Replace("ReadableBody", "\u4e2d\u6587 ASCII");
        // Put the gradient before text so rich font coverage cannot mask this feature regression.
        string objects = feature.Contains("AxialShd", StringComparison.Ordinal)
            ? "<PathObject ID='12' Boundary='0 0 20 20' Fill='true'>" + feature +
                "<AbbreviatedData>M 0 0 L 10 0 L 10 10 C</AbbreviatedData></PathObject>" + text
            : text.Replace("<TextCode", feature + "<TextCode");
        var parts = Parts(objects, false);
        using DocumentSession session = Session(parts);
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        AssertTextOnly(converted, "\u4e2d\u6587 ASCII");
    }

    [TestMethod]
    [DataRow("TextObject")]
    [DataRow("PageBlock")]
    [DataRow("Layer")]
    public async Task InvisibleContainersNeverLeakText(string container)
    {
        string hidden = Text.Replace("ReadableBody", "HiddenSecret").Replace("ID='10'", "ID='11'");
        hidden = container == "TextObject" ? hidden.Replace("<TextObject ", "<TextObject Visible='false' ") :
            $"<{container} ID='20' Visible='false'>{hidden}</{container}>";
        var parts = Parts(Text + (container == "Layer" ? "" : hidden));
        if (container == "Layer") parts["Doc/Page.xml"] = parts["Doc/Page.xml"].Replace("</Content>", hidden + "</Content>");
        using DocumentSession session = Session(parts);
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        string plain = PdfReadDocument.Open(converted.Path).ExtractText();
        Assert.IsFalse(plain.Contains("HiddenSecret", StringComparison.Ordinal));
        Assert.IsFalse(string.Concat(PdfPageInteractionMap.Create(File.ReadAllBytes(converted.Path), 1)
            .TextRegions.Select(r => r.Text)).Contains("HiddenSecret", StringComparison.Ordinal));
        AssertTextOnly(converted, "ReadableBody");
    }

    [TestMethod]
    public async Task ClipMaskTextCodesAreNotBodyText()
    {
        string clip = "<Clips><Clip><Area>" + Text.Replace("ReadableBody", "MaskSecret") + "</Area></Clip></Clips>";
        using DocumentSession session = Session(Parts(Text.Replace("<TextCode", clip + "<TextCode")));
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        Assert.IsFalse(PdfReadDocument.Open(converted.Path).ExtractText().Contains("MaskSecret", StringComparison.Ordinal));
        Assert.IsFalse(string.Concat(PdfPageInteractionMap.Create(File.ReadAllBytes(converted.Path), 1)
            .TextRegions.Select(r => r.Text)).Contains("MaskSecret", StringComparison.Ordinal));
        AssertTextOnly(converted, "ReadableBody");
    }

    [TestMethod]
    [DataRow("cycle", "Cyclic template reference")]
    [DataRow("path", "escapes the package")]
    [DataRow("external", "Invalid package resource reference")]
    [DataRow("resource-path", "escapes the package")]
    [DataRow("dtd", "DTD")]
    [DataRow("resource-dtd", "DTD")]
    [DataRow("zip-bomb", "ZIP expansion")]
    public async Task UnsupportedMetadataFirstCannotBypassSafetyLimits(string scenario, string message)
    {
        var parts = Parts(Text);
        switch (scenario)
        {
            case "cycle":
                parts["Doc/Document.xml"] = parts["Doc/Document.xml"].Replace("</CommonData>", "<TemplatePage ID='20' BaseLoc='Cycle.xml'/></CommonData>");
                parts["Doc/Page.xml"] = parts["Doc/Page.xml"].Replace("<Content>", "<Template TemplateID='20'/><Content>");
                parts["Doc/Cycle.xml"] = $"<Page xmlns='{Ns}'><Template TemplateID='20'/></Page>";
                break;
            case "path": parts["Doc/Document.xml"] = parts["Doc/Document.xml"].Replace("Page.xml", "../../outside.xml"); break;
            case "external": parts["Doc/Document.xml"] = parts["Doc/Document.xml"].Replace("Page.xml", "https://example.invalid/page.xml"); break;
            case "resource-path": parts["Doc/Document.xml"] = parts["Doc/Document.xml"].Replace("Res.xml", "../../outside.xml"); break;
            case "dtd": parts["Doc/Page.xml"] = "<!DOCTYPE Page [<!ENTITY x SYSTEM 'file:///never-read'>]>" + parts["Doc/Page.xml"]; break;
            case "resource-dtd": parts["Doc/Res.xml"] = "<!DOCTYPE Res [<!ENTITY x SYSTEM 'file:///never-read'>]>" + parts["Doc/Res.xml"]; break;
            case "zip-bomb": parts["padding"] = new string('A', 1024 * 1024); break;
        }
        using DocumentSession session = Session(parts, scenario == "zip-bomb" ? CompressionLevel.SmallestSize : CompressionLevel.NoCompression);
        byte[] original = File.ReadAllBytes(session.LocalPath);
        DocumentOpenException error = await Assert.ThrowsExactlyAsync<DocumentOpenException>(async () =>
        { using var unexpected = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None); });
        StringAssert.Contains(error.Message, message);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(session.LocalPath));
        using var exclusive = new FileStream(session.LocalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("image")]
    public async Task NoExtractableTextDoesNotProduceFakeBlankPreview(string content)
    {
        string objects = content == "image" ? "<ImageObject ID='11' Boundary='0 0 20 20' ResourceID='2'/>" : Text.Replace("ReadableBody", content);
        var parts = Parts(objects);
        if (content == "image") parts["Doc/Res.xml"] = $"<Res xmlns='{Ns}'><MultiMedias><MultiMedia ID='2' Type='Image' Format='JBIG2'><MediaFile>image.jb2</MediaFile></MultiMedia></MultiMedias></Res>";
        using DocumentSession session = Session(parts);
        byte[] original = File.ReadAllBytes(session.LocalPath);
        await Assert.ThrowsExactlyAsync<DocumentOpenException>(async () =>
        { using var unexpected = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None); });
        CollectionAssert.AreEqual(original, File.ReadAllBytes(session.LocalPath));
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConcurrentFallbacksKeepIndependentAsciiAndChineseTextMaps(bool chinese)
    {
        string prefix = chinese ? "\u4e2d\u6587" : "Ascii";
        foreach (char character in prefix)
        {
            using SKTypeface? face = SKFontManager.Default.MatchCharacter(character);
            if (face is null) Assert.Inconclusive("No installed font covers the optional character set.");
            using var font = new SKFont(face);
            if (!font.ContainsGlyphs(character.ToString())) Assert.Inconclusive("Installed font lacks the optional glyph.");
        }
        for (int pass = 0; pass < 2; pass++)
        {
            string[] markers = Enumerable.Range(0, 8).Select(i => $"{prefix}Document{i}Pass{pass}").ToArray();
            var sessions = markers.Select(t => Session(Parts(Text.Replace("ReadableBody", t)))).ToArray();
            var outputs = new ConvertedPdfDocument?[sessions.Length];
            try
            {
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task[] tasks = Enumerable.Range(0, sessions.Length).Select(i => Task.Run(async () =>
                {
                    await start.Task;
                    outputs[i] = await new OfficePdfConverter().ConvertAsync(sessions[i], CancellationToken.None);
                })).ToArray();
                start.SetResult();
                await Task.WhenAll(tasks);
                for (int i = 0; i < outputs.Length; i++)
                {
                    string plain = Compact(PdfReadDocument.Open(outputs[i]!.Path).ExtractText());
                    foreach (string other in markers.Where(t => t != markers[i])) Assert.IsFalse(plain.Contains(other, StringComparison.Ordinal));
                    AssertTextOnly(outputs[i]!, markers[i]);
                }
            }
            finally
            {
                foreach (var output in outputs) output?.Dispose();
                foreach (var session in sessions) session.Dispose();
            }
        }
    }

    private static Dictionary<string, string> Parts(string objects, bool unsupportedMetadata = true) => new()
    {
        ["OFD.xml"] = $"<OFD xmlns='{Ns}' Version='1.0' DocType='OFD'><DocBody>" +
            (unsupportedMetadata ? "<DocInfo><CustomDatas/></DocInfo>" : "") + "<DocRoot>Doc/Document.xml</DocRoot></DocBody></OFD>",
        ["Doc/Document.xml"] = $"<Document xmlns='{Ns}'><CommonData><PageArea><PhysicalBox>0 0 100 120</PhysicalBox></PageArea><PublicRes>Res.xml</PublicRes></CommonData><Pages><Page ID='5' BaseLoc='Page.xml'/></Pages></Document>",
        ["Doc/Res.xml"] = $"<Res xmlns='{Ns}'><Fonts><Font ID='1' FontName='Missing-Test-Font'/></Fonts></Res>",
        ["Doc/Page.xml"] = $"<Page xmlns='{Ns}'><Content><Layer ID='6'>{objects}</Layer></Content></Page>"
    };

    private static DocumentSession Session(Dictionary<string, string> parts, CompressionLevel compression = CompressionLevel.NoCompression)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var part in parts.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var entry = zip.CreateEntry(part.Key, compression);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using Stream stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(part.Value));
            }
        string path = Path.Combine(Path.GetTempPath(), $"survoler-ofd-text-test-{Guid.NewGuid():N}.ofd");
        File.WriteAllBytes(path, output.ToArray());
        return new DocumentSession(Guid.NewGuid(), "generated.ofd", path, OfficeFileKind.Ofd);
    }
}
