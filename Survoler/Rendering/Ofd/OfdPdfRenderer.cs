using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using OfficeIMO.Drawing;
using SkiaSharp;
using Survoler.Resources;
using static Survoler.Rendering.OfdXml;

namespace Survoler.Rendering;

internal sealed partial class OfdPdfRenderer : IDisposable
{
    private const int MaxObjects = 100000;
    private const int MaxText = 1000000;
    private readonly OfdPackage _package;
    private readonly OfficePdfRenderingResources? _resources;
    private readonly CancellationToken _token;
    private readonly Dictionary<string, Resource> _common = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Path, string Order)> _templates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SKTypeface> _fonts = new(StringComparer.Ordinal);
    private readonly OfficeFontFace[] _registeredFaces;
    private readonly Dictionary<string, SKImage> _images = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeTemplates = new(StringComparer.Ordinal);
    private int _objects;
    private int _text;
    private bool _hasObjects;
    private long _fontBytes;
    private long _assetBytes;
    private long _decodedPixels;
    private int _imageDraws;

    internal string? Warning { get; private set; }

    internal OfdPdfRenderer(OfdPackage package, OfficePdfRenderingResources? resources, CancellationToken token)
    {
        _package = package;
        _resources = resources;
        _token = token;
        _registeredFaces = resources?.Profile.Fonts.Faces.Where(f => f.Style == OfficeFontStyle.Regular).ToArray()
            ?? Array.Empty<OfficeFontFace>();
    }

    private void Tick()
    {
        _token.ThrowIfCancellationRequested();
        if (++_objects > MaxObjects) throw Invalid("ObjectBudget");
    }

    private XElement Read(string path, string root)
    {
        Tick();
        XElement element = _package.ReadXml(path).Root ?? throw Invalid("EmptyXml");
        if (element.Name != Ofd + root) throw Invalid("ExpectedRoot", root, path);
        return element;
    }

    internal void Write(string output)
    {
        XElement ofd = Read("OFD.xml", "OFD");
        Check(ofd, "Version DocType", "DocBody");
        if (ofd.Attribute("DocType") is { Value: not "OFD" }) throw Unsupported(OfdStrings.Get("DocumentType"));
        if (ofd.Elements().Count() != 1) throw Invalid("DocBodyCount");
        XElement body = One(ofd, "DocBody");
        Check(body, "", "DocInfo DocRoot Signatures");
        if (body.Element(Ofd + "DocInfo") is { } info)
        {
            Check(info, "", "DocID Title Author Subject Abstract CreationDate ModDate DocUsage Cover Keywords Creator CreatorVersion");
            foreach (XElement field in info.Elements())
            {
                if (field.Name == Ofd + "Keywords")
                {
                    Check(field, "", "Keyword");
                    foreach (XElement keyword in field.Elements()) Leaf(keyword);
                }
                else Leaf(field);
            }
        }
        string documentPath = OfdPackage.Resolve("OFD.xml", Text(One(body, "DocRoot")));
        XElement document = Read(documentPath, "Document");
        Check(document, "", "CommonData Pages Annotations");
        // These references describe overlays, not the page body. Do not open or verify them.
        bool skippedSignatures = body.Element(Ofd + "Signatures") is not null;
        bool skippedAnnotations = document.Element(Ofd + "Annotations") is not null;
        if (skippedSignatures || skippedAnnotations)
        {
            Warning = OfdStrings.Get(skippedSignatures && skippedAnnotations ? "SkippedSignaturesAndAnnotations" :
                skippedSignatures ? "SkippedSignatures" : "SkippedAnnotations");
        }
        XElement common = One(document, "CommonData");
        Check(common, "", "MaxUnitID PageArea PublicRes DocumentRes TemplatePage");
        if (common.Element(Ofd + "MaxUnitID") is { } maxId) Leaf(maxId);
        SKRect? defaultArea = common.Element(Ofd + "PageArea") is { } area ? Area(area) : null;
        LoadResources(common, documentPath, _common, "PublicRes", "DocumentRes");
        foreach (XElement template in common.Elements(Ofd + "TemplatePage"))
        {
            Tick();
            Check(template, "ID Name BaseLoc ZOrder", "");
            string order = template.Attribute("ZOrder")?.Value ?? "Background";
            if (order is not ("Background" or "Foreground")) throw Invalid("InvalidTemplateZOrder");
            if (!_templates.TryAdd(Id(template), (OfdPackage.Resolve(documentPath, Required(template, "BaseLoc")), order)))
                throw Invalid("DuplicateTemplateId");
        }
        XElement pages = One(document, "Pages");
        Check(pages, "", "Page");
        XElement[] entries = pages.Elements().ToArray();
        if (entries.Length is 0 or > 2000) throw Invalid("PageCount");
        var pageIds = new HashSet<string>(StringComparer.Ordinal);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var stream = new OfdBoundedPdfStream(file, _token);
        using var pdf = SKDocument.CreatePdf(stream);
        stream.ThrowIfFailed();
        if (pdf is null) throw Invalid("PdfWriterUnavailable");
        foreach (XElement entry in entries)
        {
            Tick();
            Check(entry, "ID BaseLoc", "");
            if (!pageIds.Add(Id(entry))) throw Invalid("DuplicatePageId");
            string pagePath = OfdPackage.Resolve(documentPath, Required(entry, "BaseLoc"));
            XElement page = Read(pagePath, "Page");
            SKRect box = page.Element(Ofd + "Area") is { } pageArea ? Area(pageArea) :
                defaultArea ?? throw Invalid("MissingPhysicalBox");
            SKCanvas canvas = pdf.BeginPage(box.Width * PointsPerMillimeter, box.Height * PointsPerMillimeter);
            canvas.Scale(PointsPerMillimeter);
            canvas.Translate(-box.Left, -box.Top);
            PaintPage(canvas, page, pagePath, 0);
            pdf.EndPage();
            stream.ThrowIfFailed();
        }
        _token.ThrowIfCancellationRequested();
        if (!_hasObjects) throw Invalid("NoGraphicObjects");
        pdf.Close();
        stream.Flush();
        stream.ThrowIfFailed();
    }

    private void PaintPage(SKCanvas canvas, XElement page, string path, int depth)
    {
        Tick();
        if (depth > 64) throw Invalid("TemplateDepth");
        Check(page, "", "Area PageRes Template Content");
        if (page.Element(Ofd + "Area") is { } area) Area(area);
        var scope = new Dictionary<string, Resource>(_common, StringComparer.Ordinal);
        LoadResources(page, path, scope, "PageRes");
        XElement[] templates = page.Elements(Ofd + "Template").ToArray();
        foreach (XElement template in templates)
        {
            Check(template, "TemplateID ZOrder", "");
            if ((template.Attribute("ZOrder")?.Value ?? "Background") is not ("Background" or "Foreground"))
                throw Invalid("InvalidTemplateZOrder");
            if (!_templates.ContainsKey(Required(template, "TemplateID"))) throw Invalid("UnknownTemplate");
        }
        PaintTemplates("Background");
        XElement[] layers = Array.Empty<XElement>();
        if (page.Element(Ofd + "Content") is { } content)
        {
            Check(content, "", "Layer");
            layers = content.Elements().ToArray();
            foreach (XElement layer in layers)
            {
                Check(layer, "ID Type", "TextObject PathObject ImageObject");
                Id(layer);
                if ((layer.Attribute("Type")?.Value ?? "Body") is not ("Background" or "Body" or "Foreground"))
                    throw Invalid("InvalidLayerType");
            }
        }
        PaintLayers("Background");
        PaintLayers("Body");
        PaintTemplates("Foreground");
        PaintLayers("Foreground");

        void PaintLayers(string type)
        {
            foreach (XElement layer in layers.Where(e => (e.Attribute("Type")?.Value ?? "Body") == type))
            {
                Tick();
                foreach (XElement obj in layer.Elements()) PaintObject(canvas, obj, scope);
            }
        }

        void PaintTemplates(string order)
        {
            foreach (XElement template in templates.Where(e =>
                (e.Attribute("ZOrder")?.Value ?? _templates[Required(e, "TemplateID")].Order) == order))
            {
                string id = Required(template, "TemplateID");
                if (!_activeTemplates.Add(id)) throw Invalid("CyclicTemplate");
                try
                {
                    string templatePath = _templates[id].Path;
                    PaintPage(canvas, Read(templatePath, "Page"), templatePath, depth + 1);
                }
                finally { _activeTemplates.Remove(id); }
            }
        }
    }

    private void PaintObject(SKCanvas canvas, XElement obj, Dictionary<string, Resource> scope)
    {
        Tick();
        _hasObjects = true;
        string kind = obj.Name.LocalName;
        string attributes = "ID Name Boundary CTM Alpha Visible ";
        switch (kind)
        {
            case "TextObject":
                Check(obj, attributes + "Font Size Fill Stroke", "FillColor TextCode");
                if (Boolean(obj, "Stroke", false) || !Boolean(obj, "Fill", true))
                    throw Unsupported(OfdStrings.Get("OutlinedText"));
                break;
            case "PathObject":
                Check(obj, attributes + "Fill Stroke LineWidth Rule Cap Join MiterLimit", "FillColor StrokeColor AbbreviatedData");
                break;
            case "ImageObject":
                Check(obj, attributes + "ResourceID", "");
                break;
            default: throw Unsupported(kind);
        }
        Id(obj);
        SKRect boundary = Box(Required(obj, "Boundary"));
        byte alpha = Byte(obj.Attribute("Alpha")?.Value ?? "255");
        bool visible = Boolean(obj, "Visible", true);
        canvas.Save();
        try
        {
            canvas.Translate(boundary.Left, boundary.Top);
            canvas.ClipRect(new SKRect(0, 0, boundary.Width, boundary.Height));
            if (obj.Attribute("CTM") is { } ctm)
            {
                float[] m = Numbers(ctm.Value, 6);
                var matrix = new SKMatrix(m[0], m[2], m[4], m[1], m[3], m[5], 0, 0, 1);
                canvas.Concat(in matrix);
            }
            // Hidden objects are still parsed and validated; only their paint is suppressed.
            if (!visible) canvas.ClipRect(SKRect.Empty);
            switch (kind)
            {
                case "TextObject": PaintText(canvas, obj, scope, alpha); break;
                case "PathObject": PaintPath(canvas, obj, scope, alpha); break;
                case "ImageObject": PaintImage(canvas, obj, scope, alpha); break;
            }
        }
        finally { canvas.Restore(); }
    }

    public void Dispose()
    {
        foreach (SKImage image in _images.Values) image.Dispose();
        _images.Clear();
        foreach (SKTypeface typeface in _fonts.Values) typeface.Dispose();
        _fonts.Clear();
    }
}
