using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using OfficeIMO.Drawing;
using OfficeIMO.Pdf;
using SkiaSharp;
using Survoler.Documents;
using Survoler.Rendering;

namespace Survoler.Tests;

[TestClass]
public sealed class OfdPreviewTests
{
    private const string Ns = "http://www.ofdspec.org/2016";
    private const double Pt = 72d / 25.4;
    private const string Text = "<TextObject ID='10' Boundary='10 20 80 30' Font='1' Size='4'><TextCode X='2' Y='8'>ABCD</TextCode></TextObject>";
    private const string Image = "<ImageObject ID='11' Boundary='30 40 50 40' ResourceID='2' CTM='20 0 0 10 3 4'/>";

    // Isolate each experiment from unrelated conversions; the workload itself remains eight-way.
    [TestMethod]
    [DoNotParallelize]
    public Task ConcurrentConversionsThenSerialExtractionPreserveUniqueText() => ConcurrentRoundTrips(true);

    [TestMethod]
    [DoNotParallelize]
    public Task SerialConversionsThenConcurrentExtractionPreserveUniqueText() => ConcurrentRoundTrips(false);

    [TestMethod]
    [DoNotParallelize]
    public Task ConcurrentConversionsWithOwnedEmbeddedTypefacesPreserveUniqueText() => ConcurrentRoundTrips(true, true);

    private static async Task ConcurrentRoundTrips(bool concurrentConversion, bool embeddedFont = false)
    {
        string[] names = ["Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta", "Theta"];
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        int reported = 0;
        byte[]? fontBytes = null;
        if (embeddedFont)
        {
            using SKStreamAsset stream = SKTypeface.Default.OpenStream();
            using SKData data = SKData.Create(stream);
            fontBytes = data.ToArray();
        }
        for (int pass = 0; pass < 3; pass++)
        {
            string[] texts = names.Select(n => $"{n}Pass{pass}").ToArray();
            var sessions = texts.Select(t =>
            {
                var parts = Parts(Text.Replace("ABCD", t));
                if (fontBytes is not null)
                {
                    Set(parts, "Doc/Res.xml", Xml(parts, "Doc/Res.xml").Replace("FontName='Missing-Test-Font'/>", "FontName='Missing-Test-Font'><FontFile>font.ttf</FontFile></Font>"));
                    parts.Add(("Doc/font.ttf", fontBytes));
                }
                return Session(Zip(parts));
            }).ToArray();
            var converted = new ConvertedPdfDocument?[names.Length];
            try
            {
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                async Task Convert(int i)
                {
                    converted[i] = await new OfficePdfConverter().ConvertAsync(sessions[i], CancellationToken.None);
                }
                if (concurrentConversion)
                {
                    Task[] tasks = Enumerable.Range(0, names.Length).Select(i => Task.Run(async () => { await start.Task; await Convert(i); })).ToArray();
                    start.SetResult();
                    await Task.WhenAll(tasks);
                }
                else
                    for (int i = 0; i < names.Length; i++) await Convert(i);

                void Extract(int i)
                {
                    byte[] bytes = File.ReadAllBytes(converted[i]!.Path);
                    // Text-run segmentation and inferred spaces depend on the platform font.
                    string plain = string.Concat(PdfReadDocument.Open(converted[i]!.Path).Pages[0]
                        .GetTextSpans().Select(span => span.Text));
                    string mapped = string.Concat(PdfPageInteractionMap.Create(bytes, 1).TextRegions.Select(r => r.Text));
                    if (plain != texts[i] || mapped != texts[i])
                    {
                        failures.Enqueue($"pass={pass}, document={i}, expected={texts[i]}, text={plain.Replace("\0", "\\0")}, map={mapped.Replace("\0", "\\0")}");
                        if (Interlocked.Exchange(ref reported, 1) == 0)
                        {
                            string decoded = DecodedStreams(bytes);
                            failures.Enqueue("Text operators: " + Regex.Match(decoded, @"(?s)BT.*?ET").Value +
                                " Unicode map: " + Regex.Match(decoded, @"(?s)begincmap.*?endcmap").Value);
                        }
                    }
                }
                if (concurrentConversion)
                    for (int i = 0; i < names.Length; i++) Extract(i);
                else
                {
                    Task[] tasks = Enumerable.Range(0, names.Length).Select(i => Task.Run(async () => { await start.Task; Extract(i); })).ToArray();
                    start.SetResult();
                    await Task.WhenAll(tasks);
                }
            }
            finally
            {
                foreach (var pdf in converted) pdf?.Dispose();
                foreach (var session in sessions) session.Dispose();
            }
        }
        Assert.IsTrue(failures.IsEmpty, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public async Task DeltaYOnlyKeepsXConstant()
    {
        using DocumentSession session = Session(Zip(Parts(Text.Replace("X='2'", "X='2' DeltaY='3 g 2 4'"))));
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        var spans = PdfReadDocument.Open(converted.Path).Pages[0].GetTextSpans().ToArray();
        Assert.AreEqual(4, spans.Length);
        double[] y = [28, 31, 35, 39];
        for (int i = 0; i < spans.Length; i++)
        {
            Assert.AreEqual(12 * Pt, spans[i].X, 0.03);
            Assert.AreEqual(Math.Round(120 * Pt) - y[i] * Pt, spans[i].Y, 0.03);
        }
    }

    [TestMethod]
    public async Task TextCodesInheritLastGlyphPositionForOmittedCoordinates()
    {
        string text = Text.Replace("<TextCode X='2' Y='8'>ABCD</TextCode>",
            "<TextCode X='2' Y='8' DeltaX='5'>AB</TextCode><TextCode Y='14'>C</TextCode><TextCode X='20'>D</TextCode><TextCode>E</TextCode>");
        using DocumentSession session = Session(Zip(Parts(text)));
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        var spans = PdfReadDocument.Open(converted.Path).Pages[0].GetTextSpans().ToArray();
        Assert.AreEqual("ABCDE", string.Concat(spans.Select(s => s.Text)));
        double[] x = [12, 17, 17, 30, 30], y = [28, 28, 34, 34, 34];
        for (int i = 0; i < spans.Length; i++)
        {
            Assert.AreEqual(x[i] * Pt, spans[i].X, 0.03);
            Assert.AreEqual(Math.Round(120 * Pt) - y[i] * Pt, spans[i].Y, 0.03);
        }
    }

    [TestMethod]
    [DataRow("5 7 7")]
    [DataRow("5 g 2 7")]
    [DataRow("5 7 7 9")]
    public async Task PositionedTextUsesDeltasAndDesktopFontFallback(string delta)
    {
        byte[] bytes = Zip(Parts(Text.Replace("X='2'", $"X='2' DeltaX='{delta}'")));
        using DocumentSession session = Session(bytes);
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        byte[] pdfBytes = File.ReadAllBytes(converted.Path);
        CollectionAssert.AreEqual("%PDF-"u8.ToArray(), pdfBytes[..5]);
        PdfReadDocument pdf = PdfReadDocument.Open(converted.Path);
        Assert.AreEqual(1, pdf.Pages.Count);
        Assert.AreEqual("ABCD", Regex.Replace(pdf.ExtractText(), @"\s", ""));
        var (width, height) = pdf.Pages[0].GetPageSize();
        // Skia rounds PDF MediaBox dimensions to whole points.
        Assert.AreEqual(Math.Round(100 * Pt), width, 0.02);
        Assert.AreEqual(Math.Round(120 * Pt), height, 0.02);
        var spans = pdf.Pages[0].GetTextSpans().ToArray();
        Assert.AreEqual(4, spans.Length);
        double[] x = [12, 17, 24, 31];
        for (int i = 0; i < spans.Length; i++)
        {
            Assert.AreEqual("ABCD"[i].ToString(), spans[i].Text);
            Assert.AreEqual(x[i] * Pt, spans[i].X, 0.03);
            Assert.AreEqual(height - 28 * Pt, spans[i].Y, 0.03);
        }
        PdfPageInteractionMap map = PdfPageInteractionMap.Create(pdfBytes, 1);
        Assert.AreEqual("ABCD", string.Concat(map.TextRegions.Select(r => r.Text)));
        Assert.AreEqual("B", map.GetSelectedText(16 * Pt, 20 * Pt, 21 * Pt, 30 * Pt));
        converted.Dispose();
        Assert.IsFalse(File.Exists(converted.Path));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(session.LocalPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TemplatesPaintBackgroundThenBodyThenForeground(bool overrideOrder)
    {
        var parts = Parts(Text.Replace("ABCD", "Body"));
        string definitions = "<TemplatePage ID='20' BaseLoc='Back.xml' ZOrder='Background'/>" +
            "<TemplatePage ID='21' BaseLoc='Front.xml' ZOrder='Foreground'/>";
        Set(parts, "Doc/Document.xml", Xml(parts, "Doc/Document.xml").Replace("</CommonData>", definitions + "</CommonData>"));
        string references = overrideOrder
            ? "<Template TemplateID='20' ZOrder='Foreground'/><Template TemplateID='21' ZOrder='Background'/>"
            : "<Template TemplateID='21'/><Template TemplateID='20'/>";
        string foreground = "<Layer ID='7' Type='Foreground'>" + Text.Replace("ID='10'", "ID='13'").Replace("ABCD", "Top") + "</Layer>";
        Set(parts, "Doc/Page.xml", Xml(parts, "Doc/Page.xml").Replace("<Content>", references + "<Content>").Replace("</Content>", foreground + "</Content>"));
        Set(parts, "Doc/Back.xml", Page(Text.Replace("ABCD", "Back")));
        Set(parts, "Doc/Front.xml", Page(Text.Replace("ABCD", "Front")));
        using DocumentSession session = Session(Zip(parts));
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        var spans = PdfReadDocument.Open(converted.Path).Pages[0].GetTextSpans();
        Assert.AreEqual(overrideOrder ? "FrontBodyBackTop" : "BackBodyFrontTop",
            string.Concat(spans.Select(s => s.Text)));
    }

    [TestMethod]
    public async Task PngUsesUnitSquareNotBoundarySizeBeforeCtm()
    {
        var parts = Parts(Text + Image);
        parts.Add(("Doc/pixel.png", Png(2, 3)));
        using DocumentSession session = Session(Zip(parts));
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        var page = PdfReadDocument.Open(converted.Path).Pages[0];
        var placement = page.GetImagePlacements().Single();
        Assert.AreEqual(33 * Pt, placement.X, 0.03);
        Assert.AreEqual(Math.Round(120 * Pt) - 54 * Pt, placement.Y, 0.03);
        Assert.AreEqual(20 * Pt, placement.Width, 0.03);
        Assert.AreEqual(10 * Pt, placement.Height, 0.03);
        Assert.AreEqual(1, page.GetImages().Count);
        StringAssert.Contains(page.ExtractText(), "ABCD");
    }

    [TestMethod]
    public async Task StartPathCommandProducesSameDrawingAsMove()
    {
        string? previous = null;
        foreach (string command in new[] { "M", "S" })
        {
            string path = $"<PathObject ID='12' Boundary='10 20 30 30'><AbbreviatedData>{command} 1 2 L 10 2 L 10 12 C</AbbreviatedData></PathObject>";
            using DocumentSession session = Session(Zip(Parts(path)));
            using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
            string drawing = DecodedStreams(File.ReadAllBytes(converted.Path));
            Assert.IsTrue(Regex.IsMatch(drawing, @"1 2 m\s+10 2 l\s+10 12 l"));
            if (previous is not null) Assert.AreEqual(previous, drawing);
            previous = drawing;
        }
    }

    [TestMethod]
    public async Task PagesFollowDocumentOrderWithIndependentSizesAndOrigins()
    {
        var parts = Parts(Text);
        Set(parts, "Doc/Document.xml", Xml(parts, "Doc/Document.xml").Replace("0 0 100 120", "5 10 100 120")
            .Replace("<Page ID='5' BaseLoc='Page.xml'/>", "<Page ID='99' BaseLoc='Z.xml'/><Page ID='5' BaseLoc='A.xml'/>"));
        Set(parts, "Doc/Z.xml", Page(Text.Replace("ABCD", "First")));
        Set(parts, "Doc/A.xml", Page(Text.Replace("ABCD", "Second")).Replace("<Content>",
            "<Area><PhysicalBox>-10 -20 60 80</PhysicalBox></Area><Content>"));
        using DocumentSession session = Session(Zip(parts));
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        var pdf = PdfReadDocument.Open(converted.Path);
        Assert.AreEqual(2, pdf.Pages.Count);
        string[] texts = ["First", "Second"];
        double[] widths = [100, 60], heights = [120, 80], x = [7, 22], y = [18, 48];
        for (int i = 0; i < 2; i++)
        {
            var (width, height) = pdf.Pages[i].GetPageSize();
            Assert.AreEqual(Math.Round(widths[i] * Pt), width, 0.02);
            Assert.AreEqual(Math.Round(heights[i] * Pt), height, 0.02);
            var spans = pdf.Pages[i].GetTextSpans().ToArray();
            Assert.AreEqual(texts[i], string.Concat(spans.Select(span => span.Text)));
            Assert.AreEqual(x[i] * Pt, spans[0].X, 0.03);
            foreach (var span in spans) Assert.AreEqual(height - y[i] * Pt, span.Y, 0.03);
            var map = PdfPageInteractionMap.Create(File.ReadAllBytes(converted.Path), i + 1);
            Assert.AreEqual(texts[i], string.Concat(map.TextRegions.Select(r => r.Text)));
        }
    }

    [TestMethod]
    public async Task RelativePublicResourceBaseLocReusesOneImageForTwoPlacements()
    {
        var parts = Parts(Image + Image.Replace("ID='11'", "ID='12'").Replace("30 40 50 40", "10 60 50 40"));
        string resource = Xml(parts, "Doc/Res.xml").Replace("<Res ", "<Res BaseLoc='../Assets' ");
        parts.RemoveAll(p => p.Name == "Doc/Res.xml");
        Set(parts, "Shared/Res.xml", resource);
        Set(parts, "Doc/Document.xml", Xml(parts, "Doc/Document.xml").Replace("Res.xml", "../Shared/Res.xml"));
        parts.Add(("Assets/pixel.png", Png(2, 3)));
        using DocumentSession session = Session(Zip(parts));
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        var page = PdfReadDocument.Open(converted.Path).Pages[0];
        Assert.AreEqual(1, page.GetImages().Count);
        var placements = page.GetImagePlacements().ToArray();
        Assert.AreEqual(2, placements.Length);
        Assert.AreEqual(placements[0].ObjectNumber, placements[1].ObjectNumber);
        Assert.AreEqual(33 * Pt, placements[0].X, 0.03);
        Assert.AreEqual(13 * Pt, placements[1].X, 0.03);
        Assert.AreEqual(20 * Pt, placements[1].Width, 0.03);
    }

    [TestMethod]
    public async Task DecodedImageBudgetAcceptsSixteenMillionPixelsAndRejectsFifthImage()
    {
        var parts = Parts("");
        var resources = new StringBuilder();
        var objects = new StringBuilder();
        for (int i = 0; i < 5; i++)
        {
            parts.Add(($"Doc/image{i}.png", Png(2000, 2000, new SKColor((byte)(i * 40), 80, 160))));
            resources.Append($"<MultiMedia ID='{i + 2}' Type='Image' Format='PNG'><MediaFile>image{i}.png</MediaFile></MultiMedia>");
            objects.Append(Image.Replace("ID='11'", $"ID='{i + 20}'").Replace("ResourceID='2'", $"ResourceID='{i + 2}'"));
            if (i < 3) continue;
            Set(parts, "Doc/Res.xml", $"<Res xmlns='{Ns}'><MultiMedias>{resources}</MultiMedias></Res>");
            Set(parts, "Doc/Page.xml", Page(objects.ToString()));
            using DocumentSession session = Session(Zip(parts));
            if (i == 3)
            {
                using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
                Assert.AreEqual(4, PdfReadDocument.Open(converted.Path).Pages[0].GetImages().Count);
            }
            else
            {
                var error = await Assert.ThrowsExactlyAsync<DocumentOpenException>(async () =>
                { using var unexpected = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None); });
                StringAssert.Contains(error.Message, "decoded image budget exceeds 16 million pixels");
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RepeatedEmbeddedFontReferencesReuseUnicodeMaps(bool chinese)
    {
        // Noto CJK maps U+6587 and U+2F42 to the same glyph: preserve their distinct source text.
        string text = chinese ? "\u4E2D\u6587\u2F42\u6587" : "AlphaBeta";
        using SKTypeface? face = chinese ? SKFontManager.Default.MatchCharacter('\u4E2D') : SKTypeface.FromFamilyName(SKTypeface.Default.FamilyName);
        if (face is null) Assert.Inconclusive("No installed font covers the requested characters.");
        using SKStreamAsset stream = face.OpenStream();
        using SKData data = SKData.Create(stream);
        using SKTypeface embeddedFace = SKTypeface.FromData(data);
        using var font = new SKFont(embeddedFace);
        if (!font.ContainsGlyphs(text)) Assert.Inconclusive("No compatible first-face font is available for this optional character set.");
        var parts = Parts(Text.Replace("ABCD", text) + Text.Replace("ABCD", text).Replace("ID='10'", "ID='12'")
            .Replace("Font='1'", "Font='3'").Replace("10 20 80 30", "10 60 80 30"));
        Set(parts, "Doc/Res.xml", $"<Res xmlns='{Ns}'><Fonts><Font ID='1' FontName='First'><FontFile>font.ttf</FontFile></Font>" +
            "<Font ID='3' FontName='Alias'><FontFile>font.ttf</FontFile></Font></Fonts></Res>");
        parts.Add(("Doc/font.ttf", data.ToArray()));
        using DocumentSession session = Session(Zip(parts));
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        byte[] pdf = File.ReadAllBytes(converted.Path);
        Assert.AreEqual(text + text, Regex.Replace(PdfReadDocument.Open(converted.Path).ExtractText(), @"\s", ""));
        Assert.AreEqual(text + text, string.Concat(PdfPageInteractionMap.Create(pdf, 1).TextRegions.Select(r => r.Text)));
        // Distinct source scalars sharing a glyph may need separate PDF font subsets.
        // A second reference should reuse those maps rather than duplicate the resources.
        Set(parts, "Doc/Page.xml", Page(Text.Replace("ABCD", text)));
        using DocumentSession singleUse = Session(Zip(parts));
        using ConvertedPdfDocument singlePdf = await new OfficePdfConverter().ConvertAsync(singleUse, CancellationToken.None);
        int singleMapCount = Regex.Matches(Encoding.Latin1.GetString(File.ReadAllBytes(singlePdf.Path)), @"/ToUnicode \d+ 0 R").Count;
        Assert.IsGreaterThan(0, singleMapCount);
        Assert.AreEqual(singleMapCount, Regex.Matches(Encoding.Latin1.GetString(pdf), @"/ToUnicode \d+ 0 R").Count);
    }

    [TestMethod]
    public async Task RegisteredFallbackFontPreservesSelectableText()
    {
        using SKStreamAsset stream = SKTypeface.Default.OpenStream();
        using SKData data = SKData.Create(stream);
        var fonts = new OfficeFontFaceCollection();
        Assert.IsTrue(fonts.TryAdd("Registered fallback", data.ToArray(), OfficeFontStyle.Regular));
        var provider = new FontResourcesProvider(new OfficePdfRenderingResources(
            new OfficeRenderingProfile("ofd-test", fonts), defaultFontFamily: "Registered fallback"));
        using DocumentSession session = Session(Zip(Parts(Text)));
        using ConvertedPdfDocument converted = await new OfficePdfConverter(provider).ConvertAsync(session, CancellationToken.None);
        Assert.AreEqual(1, provider.Calls);
        Assert.AreEqual("ABCD", PdfReadDocument.Open(converted.Path).ExtractText().Trim());
        Assert.AreEqual("ABCD", string.Concat(PdfPageInteractionMap.Create(File.ReadAllBytes(converted.Path), 1).TextRegions.Select(r => r.Text)));
    }

    private sealed class FontResourcesProvider(OfficePdfRenderingResources resources) : IOfficePdfRenderingResourcesProvider
    {
        public int Calls { get; private set; }
        public OfficePdfRenderingResources GetResources() { Calls++; return resources; }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("PK")]
    [DataRow("%PDF-1.7")]
    [DataRow("<OFD/>")]
    public async Task CoordinatorRejectsRawNonZipContentDespiteOfdFilename(string content)
    {
        using var file = FakeStorageFile.Create("mislabelled.OFD", Encoding.UTF8.GetBytes(content));
        using var coordinator = new DocumentOpenCoordinator();
        var error = await Assert.ThrowsExactlyAsync<DocumentOpenException>(() => coordinator.OpenAsync(file));
        Assert.AreEqual("The file content does not match its extension.", error.Message);
    }

    [TestMethod]
    public async Task PathsPreserveLineCubicQuadraticArcCloseAndTransform()
    {
        const string path = "<PathObject ID='12' Boundary='10 20 80 60' CTM='2 0 0 3 4 5' Fill='true' Stroke='false'>" +
            "<FillColor Value='255 0 0'/><AbbreviatedData>M 1 2 L 4 2 B 5 2 6 3 7 4 Q 8 5 9 4 A 2 2 0 0 1 11 6 C</AbbreviatedData></PathObject>";
        using DocumentSession session = Session(Zip(Parts(path)));
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        Assert.AreEqual(1, PdfReadDocument.Open(converted.Path).Pages.Count);
        // Inspect decoded drawing operators, not compressed bytes or merely the PDF header.
        string content = DecodedStreams(File.ReadAllBytes(converted.Path));
        Assert.IsTrue(Regex.IsMatch(content, @"1 2 m\s+4 2 l"), "Missing move/line coordinates.");
        Assert.IsTrue(content.Contains("5 2 6 3 7 4 c"), "Missing cubic coordinates.");
        Match[] curves = Regex.Matches(content, @"(?m)^([\d. ]+) c$").ToArray();
        Assert.IsTrue(curves.Length >= 3, "Cubic, quadratic, and arc must all produce curves.");
        double[] quadratic = curves[1].Groups[1].Value.Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        double[] controls = [7 + 2d / 3, 4 + 2d / 3, 8 + 1d / 3, 4 + 2d / 3, 9, 4];
        Assert.AreEqual(controls.Length, quadratic.Length);
        for (int i = 0; i < controls.Length; i++) Assert.AreEqual(controls[i], quadratic[i], 0.0001);
        Assert.IsTrue(Regex.IsMatch(content, @"11 6 c\s+1 2 l"), "Arc must end at (11, 6) before closing.");
        Assert.IsTrue(Regex.IsMatch(content, @"h\s+f"), "Missing closed fill.");
        Match matrix = Regex.Matches(content, @"(?m)^([\d.]+) 0 0 ([\d.]+) ([\d.]+) ([\d.]+) cm$").Single();
        double[] expected = [2 * Pt, 3 * Pt, 14 * Pt, 25 * Pt];
        for (int i = 0; i < expected.Length; i++)
            Assert.AreEqual(expected[i], double.Parse(matrix.Groups[i + 1].Value, CultureInfo.InvariantCulture), 0.001);
    }

    [TestMethod]
    public async Task CoordinatorAcceptsUppercaseOfdZipAndConversionPreservesInput()
    {
        byte[] bytes = Zip(Parts(Text));
        CollectionAssert.AreEqual(bytes, Zip(Parts(Text)), "ZIP fixture generation must be deterministic.");
        using var file = FakeStorageFile.Create("generated.OFD", bytes);
        using var coordinator = new DocumentOpenCoordinator();
        DocumentSession? session = await coordinator.OpenAsync(file);
        Assert.IsNotNull(session);
        Assert.AreEqual("generated.OFD", session.SourceName);
        Assert.AreEqual(OfficeFileKind.Ofd, session.Kind);
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        Assert.IsNull(converted.Warning);
        StringAssert.Contains(PdfReadDocument.Open(converted.Path).ExtractText(), "ABCD");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(session.LocalPath));
        coordinator.Dispose();
        Assert.IsFalse(File.Exists(session.LocalPath));
        Assert.IsTrue(File.Exists(converted.Path));
    }

    [TestMethod]
    [DataRow("clips", "Clips")]
    [DataRow("glyphs", "CGTransform")]
    [DataRow("composite", "CompositeObject")]
    [DataRow("drawparams", "DrawParams")]
    [DataRow("gradient", "AxialShd")]
    [DataRow("multidoc", "DocBody")]
    [DataRow("missing-resource", "Missing package entry")]
    [DataRow("bad-reference", "Unknown Font")]
    [DataRow("traversal-reference", "escapes the package")]
    [DataRow("traversal-entry", "Unsafe ZIP entry")]
    [DataRow("duplicate", "duplicate ZIP entry")]
    [DataRow("external", "Invalid package resource reference")]
    [DataRow("dtd", "DTD")]
    [DataRow("namespace", "Expected OFD")]
    [DataRow("number", "non-finite")]
    [DataRow("delta-bomb", "delta expansion")]
    [DataRow("short-delta", "delta")]
    [DataRow("page-size", "14,400")]
    [DataRow("image-size", "five-million-pixel")]
    [DataRow("zip-ratio", "ZIP expansion")]
    [DataRow("missing-manifest", "no OFD.xml entry")]
    [DataRow("xml-depth", "XML complexity limit")]
    [DataRow("xml-nodes", "XML node budget")]
    [DataRow("template-cycle", "Cyclic template reference")]
    public async Task UnsupportedTextFallsBackButUnsafePackagesRemainRejectedWithoutChangingInput(string scenario, string message)
    {
        var parts = Parts(Text);
        string page = Xml(parts, "Doc/Page.xml"), document = Xml(parts, "Doc/Document.xml"), ofd = Xml(parts, "OFD.xml");
        switch (scenario)
        {
            case "clips": page = page.Replace("<TextCode", "<Clips/><TextCode"); break;
            case "glyphs": page = page.Replace("<TextCode", "<CGTransform/><TextCode"); break;
            case "composite": page = page.Replace(Text, "<CompositeObject ID='10' Boundary='0 0 10 10' ResourceID='3'/>"); break;
            case "drawparams": Set(parts, "Doc/Res.xml", Xml(parts, "Doc/Res.xml").Replace("</Res>", "<DrawParams><DrawParam ID='3'/></DrawParams></Res>")); break;
            case "gradient": page = page.Replace("<TextCode", "<FillColor><AxialShd/></FillColor><TextCode"); break;
            case "multidoc": ofd = ofd.Replace("</OFD>", "<DocBody><DocRoot>Doc/Document.xml</DocRoot></DocBody></OFD>"); break;
            case "missing-resource": parts.RemoveAll(p => p.Name == "Doc/Res.xml"); break;
            case "bad-reference": page = page.Replace("Font='1'", "Font='999'"); break;
            case "traversal-reference": document = document.Replace("Res.xml", "../../outside.xml"); break;
            case "traversal-entry": parts.Add(("../outside.xml", [])); break;
            case "duplicate": parts.Add(("ofd.xml", Encoding.UTF8.GetBytes(ofd))); break;
            case "external": document = document.Replace("Res.xml", "https://example.invalid/Res.xml"); break;
            case "dtd": ofd = "<!DOCTYPE OFD [<!ENTITY x SYSTEM 'file:///nonexistent'>]>" + ofd; break;
            case "namespace": ofd = ofd.Replace(Ns, "urn:not-ofd"); break;
            case "number": page = page.Replace("Size='4'", "Size='NaN'"); break;
            case "delta-bomb": page = page.Replace("X='2'", "X='2' DeltaX='g 2147483647 1'"); break;
            case "short-delta": page = page.Replace("X='2'", "X='2' DeltaX='5 7'"); break;
            case "page-size": document = document.Replace("100 120", "6000 120"); break;
            case "image-size": page = Page(Image); parts.Add(("Doc/pixel.png", Png(2500, 2001))); break;
            case "zip-ratio": parts.Add(("padding", new byte[1024 * 1024])); break;
            case "missing-manifest": break;
            case "xml-depth": ofd = $"<OFD xmlns='{Ns}'>" + string.Concat(Enumerable.Repeat("<x>", 65)) + string.Concat(Enumerable.Repeat("</x>", 65)) + "</OFD>"; break;
            case "xml-nodes": ofd = $"<OFD xmlns='{Ns}'>" + string.Concat(Enumerable.Repeat("<x/>", 200001)) + "</OFD>"; break;
            case "template-cycle":
                document = document.Replace("</CommonData>", "<TemplatePage ID='20' BaseLoc='Cycle.xml'/></CommonData>");
                page = page.Replace("<Content>", "<Template TemplateID='20'/><Content>");
                Set(parts, "Doc/Cycle.xml", $"<Page xmlns='{Ns}'><Template TemplateID='20'/></Page>");
                break;
            default: Assert.Fail("Unknown scenario."); break;
        }
        Set(parts, "Doc/Page.xml", page);
        Set(parts, "Doc/Document.xml", document);
        Set(parts, "OFD.xml", ofd);
        if (scenario == "missing-manifest") parts.RemoveAll(p => p.Name == "OFD.xml");
        byte[] bytes = Zip(parts, scenario == "zip-ratio" ? CompressionLevel.SmallestSize : CompressionLevel.NoCompression);
        using DocumentSession session = Session(bytes);
        if (scenario is "clips" or "glyphs" or "drawparams" or "gradient" or "short-delta")
        {
            using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
            OfdAssertions.AssertTextOnly(converted, "ABCD");
        }
        else
        {
            DocumentOpenException exception = await Assert.ThrowsExactlyAsync<DocumentOpenException>(
                async () => { using var unexpected = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None); });
            StringAssert.Contains(exception.Message, message);
        }
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(session.LocalPath));
        using var exclusive = new FileStream(session.LocalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [TestMethod]
    public async Task PreCancellationPreservesInputAndAllowsSubsequentConversion()
    {
        byte[] bytes = Zip(Parts(Text));
        using DocumentSession session = Session(bytes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        OperationCanceledException exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => new OfficePdfConverter().ConvertAsync(session, cancellation.Token));
        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(session.LocalPath));
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        StringAssert.Contains(PdfReadDocument.Open(converted.Path).ExtractText(), "ABCD");
    }

    [TestMethod]
    public async Task CancellationAtPreviewHandoffDeletesOnlyOwnedPdf()
    {
        byte[] bytes = Zip(Parts(Text));
        using DocumentSession session = Session(bytes);
        using var cancellation = new CancellationTokenSource();
        var factory = new CancelingFactory(cancellation);
        var renderer = new OfficePdfPreviewRenderer(new OfficePdfConverter(), factory);
        Assert.IsTrue(renderer.CanRender(OfficeFileKind.Ofd));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => renderer.CreateAsync(session, cancellation.Token));
        Assert.IsNotNull(factory.Path);
        Assert.AreNotEqual(session.LocalPath, factory.Path);
        Assert.IsFalse(File.Exists(factory.Path));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(session.LocalPath));
    }

    [TestMethod]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    [DataRow(true, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, true, true)]
    public async Task SkipsOverlayReferencesWithoutReadingTheirContents(
        bool signatures, bool annotations, bool missing)
    {
        var parts = Parts(Text + Image);
        parts.Add(("Doc/pixel.png", Png(2, 3)));
        if (signatures)
            Set(parts, "OFD.xml", Xml(parts, "OFD.xml").Replace("</DocBody>",
                "<Signatures>Signs.xml</Signatures></DocBody>"));
        if (annotations)
            Set(parts, "Doc/Document.xml", Xml(parts, "Doc/Document.xml").Replace("</Document>",
                "<Annotations>Annotations.xml</Annotations></Document>"));
        if (!missing)
        {
            // Invalid XML/DTD would fail if these skipped resources were opened.
            Set(parts, "Signs.xml", "<!DOCTYPE Signatures [<!ENTITY x SYSTEM 'file:///not-read'>]><Signatures>&x;</Signatures>");
            Set(parts, "Doc/Annotations.xml", "Not XML: annotation-only text must not enter the PDF.");
        }
        byte[] original = Zip(parts);
        using DocumentSession session = Session(original);
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        Assert.IsNotNull(converted.Warning);
        StringAssert.Contains(converted.Warning, "Partial OFD preview");
        StringAssert.Contains(converted.Warning, "were skipped");
        StringAssert.Contains(converted.Warning, "does not verify digital signatures");
        if (signatures)
        {
            StringAssert.Contains(converted.Warning, "seals");
            StringAssert.Contains(converted.Warning, "signatures");
        }
        if (annotations) StringAssert.Contains(converted.Warning, "annotations");
        var pdf = PdfReadDocument.Open(converted.Path);
        Assert.AreEqual(1, pdf.Pages.Count);
        Assert.AreEqual("ABCD", pdf.ExtractText().Trim());
        Assert.AreEqual(1, pdf.Pages[0].GetImages().Count, "Normal body images must not be stripped.");
        PdfPageInteractionMap map = PdfPageInteractionMap.Create(File.ReadAllBytes(converted.Path), 1);
        Assert.AreEqual("ABCD", string.Concat(map.TextRegions.Select(region => region.Text)));
        using IDocumentPreview preview = new PdfDocumentPreview(new UnusedPageRenderer(), converted);
        Assert.AreEqual(converted.Warning, preview.Warning);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(session.LocalPath));
    }

    [TestMethod]
    public async Task SkippingOverlaysDoesNotHideUnsupportedBodyFeatures()
    {
        var parts = Parts(Text.Replace("<TextCode", "<Clips/><TextCode"));
        Set(parts, "OFD.xml", Xml(parts, "OFD.xml").Replace("</DocBody>",
            "<Signatures>Missing.xml</Signatures></DocBody>"));
        byte[] original = Zip(parts);
        using DocumentSession session = Session(original);
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        OfdAssertions.AssertTextOnly(converted, "ABCD");
        using IDocumentPreview preview = new PdfDocumentPreview(new UnusedPageRenderer(), converted);
        Assert.AreEqual(converted.Warning, preview.Warning);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(session.LocalPath));
    }

    private sealed class UnusedPageRenderer : IPdfPageRenderer
    {
        public int PageCount => 1;
        public Task<Avalonia.Media.Imaging.Bitmap> RenderPageAsync(int index, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This test only checks warning propagation.");
        public void Dispose() { }
    }

    private sealed class CancelingFactory(CancellationTokenSource cancellation) : IPdfPageRendererFactory
    {
        public string? Path { get; private set; }
        public Task<IPdfPageRenderer> OpenAsync(string pdfPath, CancellationToken cancellationToken)
        {
            Path = pdfPath;
            StringAssert.Contains(PdfReadDocument.Open(pdfPath).ExtractText(), "ABCD");
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new AssertFailedException("Expected cancellation.");
        }
    }

    private static List<(string Name, byte[] Bytes)> Parts(string objects)
    {
        var parts = new List<(string Name, byte[] Bytes)>();
        Set(parts, "OFD.xml", $"<OFD xmlns='{Ns}' Version='1.0' DocType='OFD'><DocBody><DocRoot>Doc/Document.xml</DocRoot></DocBody></OFD>");
        Set(parts, "Doc/Document.xml", $"<Document xmlns='{Ns}'><CommonData><PageArea><PhysicalBox>0 0 100 120</PhysicalBox></PageArea><PublicRes>Res.xml</PublicRes></CommonData><Pages><Page ID='5' BaseLoc='Page.xml'/></Pages></Document>");
        Set(parts, "Doc/Res.xml", $"<Res xmlns='{Ns}'><Fonts><Font ID='1' FontName='Missing-Test-Font'/></Fonts><MultiMedias><MultiMedia ID='2' Type='Image' Format='PNG'><MediaFile>pixel.png</MediaFile></MultiMedia></MultiMedias></Res>");
        Set(parts, "Doc/Page.xml", Page(objects));
        return parts;
    }

    private static string Page(string objects) => $"<Page xmlns='{Ns}'><Content><Layer ID='6'>{objects}</Layer></Content></Page>";
    private static string Xml(List<(string Name, byte[] Bytes)> parts, string name) => Encoding.UTF8.GetString(parts.Single(p => p.Name == name).Bytes);
    private static void Set(List<(string Name, byte[] Bytes)> parts, string name, string xml)
    {
        parts.RemoveAll(p => p.Name == name);
        parts.Add((name, Encoding.UTF8.GetBytes(xml)));
    }

    private static byte[] Zip(List<(string Name, byte[] Bytes)> parts, CompressionLevel compression = CompressionLevel.NoCompression)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var part in parts.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var entry = zip.CreateEntry(part.Name, compression);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using Stream stream = entry.Open();
                stream.Write(part.Bytes);
            }
        return output.ToArray();
    }

    private static DocumentSession Session(byte[] bytes)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"survoler-ofd-test-{Guid.NewGuid():N}.OFD");
        File.WriteAllBytes(path, bytes);
        return new DocumentSession(Guid.NewGuid(), "generated.OFD", path, OfficeFileKind.Ofd);
    }

    private static byte[] Png(int width, int height, SKColor? color = null)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color ?? SKColors.Red);
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static string DecodedStreams(byte[] pdf)
    {
        string raw = Encoding.Latin1.GetString(pdf);
        var result = new StringBuilder();
        foreach (Match match in Regex.Matches(raw, @"/Filter /FlateDecode[^>]*>>\s*stream\r?\n(?<data>.*?)\r?\nendstream", RegexOptions.Singleline))
        {
            using var input = new MemoryStream(Encoding.Latin1.GetBytes(match.Groups["data"].Value));
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.Latin1);
            result.AppendLine(reader.ReadToEnd());
        }
        return result.ToString();
    }
}
