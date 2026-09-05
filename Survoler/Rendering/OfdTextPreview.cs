using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using OfficeIMO.Drawing;
using SkiaSharp;
using Survoler.Documents;
using Survoler.Resources;

namespace Survoler.Rendering;

internal static class OfdTextPreview
{
    private static readonly XNamespace Ofd = "http://www.ofdspec.org/2016";

    internal static bool Write(OfdPackage package, string output,
        OfficePdfRenderingResources? resources, CancellationToken token)
    {
        int operations = 0, characters = 0;
        bool hasText = false;
        token.ThrowIfCancellationRequested();
        XElement root = package.ReadXml("OFD.xml").Root ?? throw Invalid("EmptyXml");
        // Only the ordinary single-document envelope is eligible, never encrypted variants.
        if (root.Name != Ofd + "OFD" ||
            root.Attribute("DocType") is { Value: not "OFD" } ||
            root.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name != "Version" && a.Name != "DocType") ||
            root.Elements().Any(e => e.Name != Ofd + "DocBody") ||
            root.Elements(Ofd + "DocBody").Count() != 1) return false;
        XElement body = One(root, "DocBody")!;
        if (body.Attributes().Any(a => !a.IsNamespaceDeclaration) ||
            body.Elements().Any(e => e.Name != Ofd + "DocInfo" && e.Name != Ofd + "DocRoot" &&
                e.Name != Ofd + "Signatures")) return false;
        XElement docRoot = One(body, "DocRoot")!;
        if (docRoot.HasElements) throw Invalid("UnexpectedText", "DocRoot");
        string documentPath = OfdPackage.Resolve("OFD.xml", docRoot.Value.Trim());
        XElement document = package.ReadXml(documentPath).Root ?? throw Invalid("EmptyXml");
        string[] documentChildren = { "CommonData", "Pages", "Outlines", "Permissions", "Actions",
            "VPreferences", "Bookmarks", "Annotations", "CustomTags", "Attachments", "Extensions" };
        if (document.Name != Ofd + "Document" ||
            document.Attributes().Any(a => !a.IsNamespaceDeclaration) ||
            document.Elements().Any(e => e.Name.Namespace != Ofd || !documentChildren.Contains(e.Name.LocalName)))
            return false;

        var templates = new Dictionary<string, (string Path, string Order)>(StringComparer.Ordinal);
        XElement? common = One(document, "CommonData", required: false);
        if (common is not null)
        {
            ValidateResources(common, documentPath, "PublicRes", "DocumentRes");
            foreach (XElement template in common.Elements(Ofd + "TemplatePage"))
            {
                Tick();
                if (!templates.TryAdd(Required(template, "ID"),
                    (OfdPackage.Resolve(documentPath, Required(template, "BaseLoc")), Order(template))))
                    throw Invalid("DuplicateTemplateId");
            }
        }
        XElement pages = One(document, "Pages")!;
        XElement[] entries = pages.Elements().ToArray();
        if (entries.Length == 0 || entries.Length > PreviewLimits.MaxPdfPages) throw Invalid("PageCount");
        var pageIds = new HashSet<string>(StringComparer.Ordinal);
        var activeTemplates = new HashSet<string>(StringComparer.Ordinal);
        var textPages = new List<List<string>>(entries.Length);
        foreach (XElement entry in entries)
        {
            Tick();
            if (entry.Name != Ofd + "Page") throw Invalid("ExpectedRoot", "Page", documentPath);
            if (!pageIds.Add(Required(entry, "ID"))) throw Invalid("DuplicatePageId");
            var paragraphs = new List<string>();
            ExtractPage(OfdPackage.Resolve(documentPath, Required(entry, "BaseLoc")), paragraphs, 0);
            textPages.Add(paragraphs);
        }
        if (!hasText) return false;

        OfficeFontFace[] registered = resources?.Profile.Fonts.Faces
            .OrderBy(f => f.Style == OfficeFontStyle.Regular ? 0 : 1).ToArray() ?? Array.Empty<OfficeFontFace>();
        var faces = new List<SKTypeface>();
        var fonts = new List<SKFont>();
        var metrics = new Dictionary<int, (SKFont Font, float Width)>();
        int loadedRegistered = 0;
        long fontBytes = 0;
        try
        {
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var stream = new OfdPdfConverter.BoundedPdfStream(file, token);
            using var pdf = SKDocument.CreatePdf(stream);
            stream.ThrowIfFailed();
            if (pdf is null) throw Invalid("PdfWriterUnavailable");
            using var paint = new SKPaint { IsAntialias = true, Color = SKColors.Black };
            const float width = 595.2756f, height = 841.8898f, margin = 36, lineHeight = 18;
            SKCanvas? canvas = null;
            int outputPages = 0;
            float x = margin, y = margin + 12, runX = margin;
            var run = new StringBuilder();
            SKFont? runFont = null;
            foreach (List<string> paragraphs in textPages)
            {
                BeginPage();
                foreach (string paragraph in paragraphs.Prepend(OfdStrings.Get("TextOnlyPreviewTitle")))
                {
                    bool previousCr = false;
                    int column = 0;
                    foreach (Rune rune in paragraph.EnumerateRunes())
                    {
                        token.ThrowIfCancellationRequested();
                        if (rune.Value == '\n' && previousCr) { previousCr = false; continue; }
                        previousCr = rune.Value == '\r';
                        if (rune.Value is '\r' or '\n')
                        {
                            NewLine();
                            column = 0;
                            continue;
                        }
                        if (rune.Value == '\t')
                        {
                            int spaces = 4 - column % 4;
                            for (int i = 0; i < spaces; i++) DrawRune(new Rune(' '));
                        }
                        else DrawRune(rune);
                    }
                    NewLine();

                    void DrawRune(Rune rune)
                    {
                        (SKFont font, float advance) = Measure(rune);
                        if (advance > width - 2 * margin) throw Invalid("CoordinateRange");
                        if (x + advance > width - margin) { NewLine(); column = 0; }
                        if (y > height - margin) { EndPage(); BeginPage(); }
                        if (runFont != font) FlushRun();
                        if (run.Length == 0) { runFont = font; runX = x; }
                        run.Append(rune.ToString());
                        x += advance;
                        column++;
                    }
                }
                EndPage();
            }
            token.ThrowIfCancellationRequested();
            pdf.Close();
            stream.Flush();
            stream.ThrowIfFailed();
            return true;

            void FlushRun()
            {
                if (run.Length == 0) return;
                canvas!.DrawText(run.ToString(), runX, y, SKTextAlign.Left, runFont!, paint);
                run.Clear();
            }

            void NewLine()
            {
                if (y > height - margin) { EndPage(); BeginPage(); }
                FlushRun();
                x = margin;
                y += lineHeight;
            }

            void BeginPage()
            {
                token.ThrowIfCancellationRequested();
                if (++outputPages > PreviewLimits.MaxPdfPages) throw Invalid("PageCount");
                canvas = pdf.BeginPage(width, height);
                stream.ThrowIfFailed();
                x = margin;
                y = margin + 12;
            }

            void EndPage()
            {
                FlushRun();
                pdf.EndPage();
                stream.Flush();
                stream.ThrowIfFailed();
            }

            (SKFont Font, float Width) Measure(Rune rune)
            {
                if (metrics.TryGetValue(rune.Value, out var result)) return result;
                string text = rune.ToString();
                SKFont? font = fonts.FirstOrDefault(f => f.ContainsGlyphs(text));
                while (font is null && loadedRegistered < registered.Length)
                {
                    byte[] bytes = registered[loadedRegistered++].Data;
                    ReserveFontBytes(bytes.Length);
                    using SKData data = SKData.CreateCopy(bytes);
                    SKFont candidate = Own(SKTypeface.FromData(data) ?? throw Invalid("FontLoadFailed"));
                    if (candidate.ContainsGlyphs(text)) font = candidate;
                }
                if (font is null && registered.Length == 0 && !OperatingSystem.IsAndroid())
                {
                    // Matched native faces are byte sources only: shared faces corrupt concurrent ToUnicode maps.
                    using SKTypeface matched = SKFontManager.Default.MatchCharacter(rune.Value)
                        ?? throw Invalid("NoFontCoverage");
                    using SKStreamAsset source = matched.OpenStream(out int collectionIndex)
                        ?? throw Invalid("FallbackFontReadFailed");
                    ReserveFontBytes(source.Length);
                    using SKData data = SKData.Create(source) ?? throw Invalid("FallbackFontLoadFailed");
                    font = Own(SKTypeface.FromData(data, collectionIndex) ?? throw Invalid("FallbackFontLoadFailed"));
                }
                if (font is null || !font.ContainsGlyphs(text)) throw Invalid("NoFontCoverage");
                float advance = font.MeasureText(text, paint);
                if (!float.IsFinite(advance) || advance < 0) throw Invalid("CoordinateRange");
                result = (font, advance);
                metrics.Add(rune.Value, result);
                return result;
            }
        }
        finally
        {
            foreach (SKFont font in fonts) font.Dispose();
            foreach (SKTypeface face in faces) face.Dispose();
        }

        SKFont Own(SKTypeface face)
        {
            faces.Add(face);
            var font = new SKFont(face, 12);
            fonts.Add(font);
            SKFontMetrics dimensions = font.Metrics;
            if (!float.IsFinite(dimensions.Ascent) || !float.IsFinite(dimensions.Descent) ||
                !float.IsFinite(dimensions.Leading) || dimensions.Ascent > 0 || dimensions.Descent < 0 ||
                dimensions.Descent - dimensions.Ascent > 14400) throw Invalid("CoordinateRange");
            return font;
        }

        void ReserveFontBytes(long count)
        {
            token.ThrowIfCancellationRequested();
            if (count <= 0 || count > 64L * 1024 * 1024 - fontBytes) throw Invalid("FontBudget");
            fontBytes += count;
        }

        void Tick()
        {
            token.ThrowIfCancellationRequested();
            if (++operations > 100000) throw Invalid("ObjectBudget");
        }

        void ExtractPage(string path, List<string> paragraphs, int depth)
        {
            Tick();
            if (depth > 64) throw Invalid("TemplateDepth");
            XElement page = package.ReadXml(path).Root ?? throw Invalid("EmptyXml");
            if (page.Name != Ofd + "Page") throw Invalid("ExpectedRoot", "Page", path);
            ValidateResources(page, path, "PageRes");
            if (!Visible(page)) return;
            ExtractTemplates("Background");
            XElement? content = One(page, "Content", required: false);
            if (content is not null && Visible(content))
            {
                Tick();
                foreach (XElement layer in content.Elements(Ofd + "Layer")) ExtractContainer(layer, paragraphs);
            }
            ExtractTemplates("Foreground");

            void ExtractTemplates(string order)
            {
                foreach (XElement reference in page.Elements(Ofd + "Template"))
                {
                    Tick();
                    if (!Visible(reference)) continue;
                    string id = Required(reference, "TemplateID");
                    if (!templates.TryGetValue(id, out var template)) throw Invalid("UnknownTemplate");
                    if (Order(reference, template.Order) != order) continue;
                    if (!activeTemplates.Add(id)) throw Invalid("CyclicTemplate");
                    try { ExtractPage(template.Path, paragraphs, depth + 1); }
                    finally { activeTemplates.Remove(id); }
                }
            }
        }

        void ExtractContainer(XElement container, List<string> paragraphs)
        {
            Tick();
            if (!Visible(container)) return;
            foreach (XElement child in container.Elements())
            {
                Tick();
                if (!Visible(child)) continue;
                if (child.Name == Ofd + "PageBlock") ExtractContainer(child, paragraphs);
                else if (child.Name == Ofd + "TextObject")
                {
                    var text = new StringBuilder();
                    // Direct TextCode only: clips and CGTransform may contain duplicate/non-body text.
                    foreach (XElement code in child.Elements(Ofd + "TextCode"))
                    {
                        Tick();
                        if (code.HasElements) throw Invalid("UnexpectedText", "TextCode");
                        string value = code.Value;
                        if (value.Length > 1000000 - characters) throw Invalid("TextBudget");
                        characters += value.Length;
                        text.Append(value);
                    }
                    if (text.Length == 0) continue;
                    string paragraph = text.ToString();
                    if (!string.IsNullOrWhiteSpace(paragraph)) hasText = true;
                    paragraphs.Add(paragraph);
                }
            }
        }

        void ValidateResources(XElement parent, string basePath, params string[] names)
        {
            // Reflow does not decode assets, but must not bypass referenced XML/path checks.
            foreach (XElement reference in parent.Elements().Where(e => names.Any(n => e.Name == Ofd + n)))
            {
                Tick();
                if (reference.HasElements) throw Invalid("UnexpectedText", reference.Name.LocalName);
                string path = OfdPackage.Resolve(basePath, reference.Value.Trim());
                XElement resource = package.ReadXml(path).Root ?? throw Invalid("EmptyXml");
                if (resource.Name != Ofd + "Res") throw Invalid("ExpectedRoot", "Res", path);
                string resourceBase = resource.Attribute("BaseLoc") is { } location
                    ? OfdPackage.Resolve(path, location.Value.TrimEnd('/') + "/__resource__") : path;
                foreach (XElement asset in resource.Descendants().Where(e => e.Name == Ofd + "FontFile" || e.Name == Ofd + "MediaFile"))
                {
                    Tick();
                    if (asset.HasElements) throw Invalid("UnexpectedText", asset.Name.LocalName);
                    OfdPackage.Resolve(resourceBase, asset.Value.Trim());
                }
            }
        }
    }

    private static XElement? One(XElement parent, string name, bool required = true)
    {
        XElement[] elements = parent.Elements(Ofd + name).Take(2).ToArray();
        if (elements.Length > 1) throw Invalid("DuplicateElement", name);
        if (elements.Length == 0 && required) throw Invalid("MissingElement", name);
        return elements.FirstOrDefault();
    }

    private static string Required(XElement element, string name) =>
        element.Attribute(name) is { Value.Length: > 0 } attribute ? attribute.Value :
            throw Invalid("MissingAttribute", element.Name.LocalName, name);

    private static bool Visible(XElement element) => element.Attribute("Visible")?.Value switch
    {
        null or "true" or "1" => true,
        "false" or "0" => false,
        _ => throw Invalid("InvalidBoolean", "Visible")
    };

    private static string Order(XElement element, string defaultOrder = "Background") =>
        (element.Attribute("ZOrder")?.Value ?? defaultOrder) switch
        {
            "Background" => "Background",
            "Foreground" => "Foreground",
            _ => throw Invalid("InvalidTemplateZOrder")
        };

    private static DocumentOpenException Invalid(string key, params object[] args) =>
        new(OfdStrings.Format("InvalidPrefix", OfdStrings.Format(key, args)));
}
