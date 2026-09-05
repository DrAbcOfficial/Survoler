using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using OfficeIMO.Drawing;
using SkiaSharp;
using Survoler.Documents;

namespace Survoler.Rendering;

/// <summary>Writes a deliberately restricted OFD subset to a temporary, selectable PDF.</summary>
public sealed class OfdPdfConverter
{
    private static readonly XNamespace Ofd = "http://www.ofdspec.org/2016";
    private const int MaxObjects = 100000;
    private const int MaxText = 1000000;
    private const float PointsPerMillimeter = 72f / 25.4f;

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
                    throw Invalid("The input exceeds 64 MiB.");
                using var package = new OfdPackage(session.LocalPath, token);
                using var renderer = new Renderer(package, resources, token);
                renderer.Write(result.Path);
                result.Warning = renderer.Warning;
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception exception)
        {
            result.Dispose();
            if (exception is InvalidDataException or NotSupportedException or System.Xml.XmlException)
                throw new DocumentOpenException(exception.Message);
            throw;
        }
    }

    private sealed record Resource(XElement Element, string BaseFile);

    private sealed class BoundedPdfStream : SKAbstractManagedWStream
    {
        private const long MaxBytes = 64L * 1024 * 1024;
        private readonly Stream _destination;
        private readonly CancellationToken _token;
        private readonly byte[] _buffer = new byte[65536];
        private long _written;
        private bool _truncated;
        private Exception? _failure;

        internal BoundedPdfStream(Stream destination, CancellationToken token)
        {
            _destination = destination;
            _token = token;
        }

        protected override bool OnWrite(IntPtr buffer, IntPtr size)
        {
            // Never unwind a managed exception through a native Skia callback.
            try
            {
                if (_truncated || _failure is not null || _token.IsCancellationRequested) return false;
                long count = size.ToInt64();
                if (count < 0 || count > MaxBytes - _written)
                {
                    _truncated = true;
                    return false;
                }
                int offset = 0;
                while (offset < count)
                {
                    if (_token.IsCancellationRequested) return false;
                    int length = (int)Math.Min(_buffer.Length, count - offset);
                    Marshal.Copy(IntPtr.Add(buffer, offset), _buffer, 0, length);
                    _destination.Write(_buffer, 0, length);
                    _written += length;
                    offset += length;
                }
                return true;
            }
            catch (Exception exception)
            {
                _failure = exception;
                return false;
            }
        }

        protected override void OnFlush()
        {
            try
            {
                if (!_truncated && _failure is null && !_token.IsCancellationRequested) _destination.Flush();
            }
            catch (Exception exception) { _failure = exception; }
        }

        protected override IntPtr OnBytesWritten() => new(_written);

        internal void ThrowIfFailed()
        {
            _token.ThrowIfCancellationRequested();
            if (_truncated) throw Invalid("The generated PDF exceeds 64 MiB.");
            if (_failure is not null) throw new IOException("The generated PDF could not be written.", _failure);
        }
    }

    private sealed class Renderer : IDisposable
    {
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

        internal Renderer(OfdPackage package, OfficePdfRenderingResources? resources, CancellationToken token)
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
            if (++_objects > MaxObjects) throw Invalid("The object/template budget was exceeded.");
        }

        private XElement Read(string path, string root)
        {
            Tick();
            XElement element = _package.ReadXml(path).Root ?? throw Invalid("An XML document is empty.");
            if (element.Name != Ofd + root) throw Invalid($"Expected OFD {root} in {path}.");
            return element;
        }

        internal void Write(string output)
        {
            XElement ofd = Read("OFD.xml", "OFD");
            Check(ofd, "Version DocType", "DocBody");
            if (ofd.Attribute("DocType") is { Value: not "OFD" }) throw Unsupported("Document type");
            if (ofd.Elements().Count() != 1) throw Invalid("Exactly one DocBody is supported.");
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
                string omitted = skippedSignatures && skippedAnnotations ? "seals, signatures and annotations" :
                    skippedSignatures ? "seals and signatures" : "annotations";
                Warning = $"Partial OFD preview: {omitted} were skipped. " +
                    "This preview does not verify digital signatures.";
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
                if (order is not ("Background" or "Foreground")) throw Invalid("Invalid template ZOrder.");
                if (!_templates.TryAdd(Id(template), (OfdPackage.Resolve(documentPath, Required(template, "BaseLoc")), order)))
                    throw Invalid("Duplicate template ID.");
            }
            XElement pages = One(document, "Pages");
            Check(pages, "", "Page");
            XElement[] entries = pages.Elements().ToArray();
            if (entries.Length is 0 or > 2000) throw Invalid("The page count must be between 1 and 2000.");
            var pageIds = new HashSet<string>(StringComparer.Ordinal);
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var stream = new BoundedPdfStream(file, _token);
            using var pdf = SKDocument.CreatePdf(stream);
            stream.ThrowIfFailed();
            if (pdf is null) throw Invalid("The PDF writer is unavailable.");
            foreach (XElement entry in entries)
            {
                Tick();
                Check(entry, "ID BaseLoc", "");
                if (!pageIds.Add(Id(entry))) throw Invalid("Duplicate page ID.");
                string pagePath = OfdPackage.Resolve(documentPath, Required(entry, "BaseLoc"));
                XElement page = Read(pagePath, "Page");
                SKRect box = page.Element(Ofd + "Area") is { } pageArea ? Area(pageArea) :
                    defaultArea ?? throw Invalid("No physical page box is defined.");
                SKCanvas canvas = pdf.BeginPage(box.Width * PointsPerMillimeter, box.Height * PointsPerMillimeter);
                canvas.Scale(PointsPerMillimeter);
                canvas.Translate(-box.Left, -box.Top);
                PaintPage(canvas, page, pagePath, 0);
                pdf.EndPage();
                stream.ThrowIfFailed();
            }
            _token.ThrowIfCancellationRequested();
            if (!_hasObjects) throw Invalid("The document has no supported graphic objects.");
            pdf.Close();
            stream.Flush();
            stream.ThrowIfFailed();
        }

        private void LoadResources(XElement parent, string path, Dictionary<string, Resource> scope, params string[] names)
        {
            foreach (XElement reference in parent.Elements().Where(e => names.Contains(e.Name.LocalName)))
            {
                string resourcePath = OfdPackage.Resolve(path, Text(reference));
                XElement res = Read(resourcePath, "Res");
                Check(res, "BaseLoc", "Fonts MultiMedias ColorSpaces");
                // Resolve expects a base FILE, so append a dummy filename to the resource base directory.
                string baseFile = res.Attribute("BaseLoc") is not null
                    ? OfdPackage.Resolve(resourcePath, Required(res, "BaseLoc").TrimEnd('/') + "/__resource__")
                    : resourcePath;
                foreach (XElement group in res.Elements())
                {
                    string itemName = group.Name.LocalName switch
                    {
                        "Fonts" => "Font", "MultiMedias" => "MultiMedia", "ColorSpaces" => "ColorSpace",
                        _ => throw Unsupported(group.Name.LocalName)
                    };
                    Check(group, "", itemName);
                    foreach (XElement item in group.Elements())
                    {
                        Tick();
                        switch (itemName)
                        {
                            case "Font":
                                Check(item, "ID FontName FamilyName Charset", "FontFile");
                                if (item.Element(Ofd + "FontFile") is { } fontFile) Text(fontFile);
                                break;
                            case "MultiMedia":
                                Check(item, "ID Type Format", "MediaFile");
                                if (Required(item, "Type") != "Image") throw Unsupported("Non-image multimedia");
                                if (item.Attribute("Format") is { } format &&
                                    !new[] { "PNG", "JPEG", "JPG" }.Contains(format.Value.ToUpperInvariant()))
                                    throw Unsupported("Image format " + format.Value);
                                Text(One(item, "MediaFile"));
                                break;
                            case "ColorSpace":
                                Check(item, "ID Type BitsPerComponent", "");
                                if (Required(item, "Type") is not ("RGB" or "GRAY") ||
                                    (item.Attribute("BitsPerComponent")?.Value ?? "8") != "8")
                                    throw Unsupported("Only 8-bit RGB/GRAY color spaces are supported");
                                break;
                        }
                        if (!scope.TryAdd(Id(item), new Resource(item, baseFile)))
                            throw Invalid("Duplicate resource ID " + Id(item) + ".");
                    }
                }
            }
        }

        private void PaintPage(SKCanvas canvas, XElement page, string path, int depth)
        {
            Tick();
            if (depth > 64) throw Invalid("Template nesting exceeds 64 levels.");
            Check(page, "", "Area PageRes Template Content");
            if (page.Element(Ofd + "Area") is { } area) Area(area);
            var scope = new Dictionary<string, Resource>(_common, StringComparer.Ordinal);
            LoadResources(page, path, scope, "PageRes");
            XElement[] templates = page.Elements(Ofd + "Template").ToArray();
            foreach (XElement template in templates)
            {
                Check(template, "TemplateID ZOrder", "");
                if ((template.Attribute("ZOrder")?.Value ?? "Background") is not ("Background" or "Foreground"))
                    throw Invalid("Invalid template ZOrder.");
                if (!_templates.ContainsKey(Required(template, "TemplateID"))) throw Invalid("Unknown template reference.");
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
                        throw Invalid("Invalid layer type.");
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
                    if (!_activeTemplates.Add(id)) throw Invalid("Cyclic template reference.");
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
                        throw Unsupported("Outlined or non-filled text");
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

        private void PaintText(SKCanvas canvas, XElement obj, Dictionary<string, Resource> scope, byte alpha)
        {
            Resource definition = Lookup(scope, Required(obj, "Font"), "Font");
            float size = Positive(Required(obj, "Size"));
            XElement[] codes = obj.Elements(Ofd + "TextCode").ToArray();
            if (codes.Length == 0) throw Invalid("TextObject has no TextCode.");
            var fontText = new StringBuilder();
            foreach (XElement code in codes)
            {
                _token.ThrowIfCancellationRequested();
                string value = code.Value;
                if (value.Length > MaxText - _text - fontText.Length)
                    throw Invalid("The text budget exceeds one million characters.");
                fontText.Append(value);
            }
            using var font = new SKFont(Typeface(definition, fontText.ToString()), size);
            using var paint = new SKPaint { IsAntialias = true, Color = Color(obj.Element(Ofd + "FillColor"), scope, alpha) };
            float? previousX = null, previousY = null;
            foreach (XElement code in codes)
            {
                Tick();
                Check(code, "X Y DeltaX DeltaY", "", allowText: true);
                string text = code.Value;
                _text = checked(_text + text.Length);
                if (_text > MaxText) throw Invalid("The text budget exceeds one million characters.");
                if (text.Length == 0) throw Invalid("Empty TextCode.");
                if (!font.ContainsGlyphs(text)) throw Invalid("No glyph is available for some OFD text characters.");
                float x = code.Attribute("X") is { } xAttribute ? Number(xAttribute.Value) :
                    previousX ?? throw Invalid("The first TextCode must define X.");
                float y = code.Attribute("Y") is { } yAttribute ? Number(yAttribute.Value) :
                    previousY ?? throw Invalid("The first TextCode must define Y.");
                Rune[] runes = text.EnumerateRunes().ToArray();
                if (code.Attribute("DeltaX") is null && code.Attribute("DeltaY") is null)
                {
                    canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);
                    int lastLength = runes[^1].Utf16SequenceLength;
                    previousX = Finite(x + font.MeasureText(text[..^lastLength], paint));
                    previousY = y;
                    continue;
                }
                float[] dx = Deltas(code.Attribute("DeltaX")?.Value, runes.Length);
                float[] dy = Deltas(code.Attribute("DeltaY")?.Value, runes.Length);
                for (int i = 0; i < runes.Length; i++)
                {
                    _token.ThrowIfCancellationRequested();
                    string glyph = runes[i].ToString();
                    canvas.DrawText(glyph, x, y, SKTextAlign.Left, font, paint);
                    if (i + 1 == runes.Length) break;
                    x = Finite(x + (dx.Length == 0 ? 0 : dx[i]));
                    y = Finite(y + (dy.Length == 0 ? 0 : dy[i]));
                }
                previousX = x;
                previousY = y;
            }
        }

        private float[] Deltas(string? value, int count)
        {
            if (value is null) return Array.Empty<float>();
            string[] tokens = Words(value);
            if (tokens.Length == 0) throw Invalid("Empty text delta.");
            var result = new List<float>();
            for (int i = 0; i < tokens.Length; i++)
            {
                _token.ThrowIfCancellationRequested();
                int repeat = 1;
                if (tokens[i] == "g")
                {
                    if (i + 2 >= tokens.Length || !int.TryParse(tokens[++i], NumberStyles.None, CultureInfo.InvariantCulture, out repeat) || repeat <= 0)
                        throw Invalid("Invalid delta repetition.");
                    i++;
                }
                if (repeat > count - result.Count) throw Invalid("Text delta expansion exceeds its rune count.");
                float delta = Number(tokens[i]);
                for (int j = 0; j < repeat; j++) result.Add(delta);
            }
            if (result.Count < count - 1) throw Unsupported("Short text delta lists");
            return result.ToArray();
        }

        private SKTypeface Typeface(Resource resource, string text)
        {
            XElement definition = resource.Element;
            if (definition.Element(Ofd + "FontFile") is { } fontFile)
            {
                string key = "embedded:" + OfdPackage.Resolve(resource.BaseFile, Text(fontFile));
                if (!_fonts.TryGetValue(key, out SKTypeface? embedded))
                {
                    byte[] bytes = ReadAsset(key[9..]);
                    // The subset accepts TrueType outlines, including collections, not arbitrary font containers.
                    if (bytes.Length < 4 || !((bytes[0] == 0 && bytes[1] == 1 && bytes[2] == 0 && bytes[3] == 0) ||
                        Encoding.ASCII.GetString(bytes, 0, 4) is "true" or "ttcf"))
                        throw Unsupported("Embedded fonts must contain TrueType outlines");
                    embedded = Load(key, bytes);
                }
                if (!Covers(embedded)) throw Invalid("The embedded OFD font lacks some text glyphs.");
                return embedded;
            }

            string? name = definition.Attribute("FontName")?.Value;
            string? substitute = null;
            if (name is not null) _resources?.FontSubstitutions.TryGetValue(name, out substitute);
            string?[] families = { substitute, name, definition.Attribute("FamilyName")?.Value, _resources?.DefaultFontFamily };
            foreach (int index in Enumerable.Range(0, _registeredFaces.Length).OrderBy(i =>
            {
                int priority = Array.FindIndex(families, family => family is not null &&
                    string.Equals(_registeredFaces[i].FamilyName, family, StringComparison.OrdinalIgnoreCase));
                return priority < 0 ? families.Length : priority;
            }))
            {
                _token.ThrowIfCancellationRequested();
                string key = "registered:" + index.ToString(CultureInfo.InvariantCulture);
                if (!_fonts.TryGetValue(key, out SKTypeface? candidate)) candidate = Load(key, _registeredFaces[index].Data);
                if (Covers(candidate)) return candidate;
            }

            if (!OperatingSystem.IsAndroid())
            {
                const string key = "desktop:default";
                if (!_fonts.TryGetValue(key, out SKTypeface? desktop))
                {
                    // Shared native typefaces can corrupt concurrent PDF ToUnicode maps.
                    // Open the default font only as a byte source; PDF drawing uses a fresh owned face.
                    using SKStreamAsset source = SKTypeface.Default.OpenStream(out int collectionIndex)
                        ?? throw Invalid("The desktop fallback font cannot be read.");
                    ReserveFontBytes(source.Length);
                    using SKData data = SKData.Create(source) ?? throw Invalid("The desktop fallback font cannot be loaded.");
                    desktop = SKTypeface.FromData(data, collectionIndex) ?? throw Invalid("The desktop fallback font cannot be loaded.");
                    _fonts.Add(key, desktop);
                }
                if (Covers(desktop)) return desktop;
            }
            throw Invalid("No available OFD font covers all characters in the text object.");

            bool Covers(SKTypeface typeface)
            {
                _token.ThrowIfCancellationRequested();
                using var font = new SKFont(typeface);
                bool result = font.ContainsGlyphs(text);
                _token.ThrowIfCancellationRequested();
                return result;
            }

            SKTypeface Load(string key, byte[] bytes)
            {
                ReserveFontBytes(bytes.Length);
                using SKData data = SKData.CreateCopy(bytes);
                SKTypeface typeface = SKTypeface.FromData(data) ?? throw Invalid("An OFD font could not be loaded.");
                _fonts.Add(key, typeface);
                return typeface;
            }

            void ReserveFontBytes(long count)
            {
                _token.ThrowIfCancellationRequested();
                if (count <= 0 || count > 64L * 1024 * 1024 - _fontBytes)
                    throw Invalid("The loaded font budget exceeds 64 MiB or a font is empty.");
                _fontBytes += count;
            }
        }

        private void PaintPath(SKCanvas canvas, XElement obj, Dictionary<string, Resource> scope, byte alpha)
        {
            string data = Text(One(obj, "AbbreviatedData"));
            // Tokenize without accepting SVG syntax, implicit commands, or silently skipped garbage.
            Match match = Regex.Match(data, @"[SMLBQAC]|[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?|\S", RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2));
            using var path = new SKPath();
            path.FillType = (obj.Attribute("Rule")?.Value ?? "NonZero") switch
            {
                "NonZero" => SKPathFillType.Winding,
                "Even-Odd" => SKPathFillType.EvenOdd,
                _ => throw Unsupported("Path fill rule")
            };
            bool started = false;
            bool openSubpath = false;
            while (match.Success)
            {
                Tick();
                string command = Take();
                if (!openSubpath && command is not ("M" or "S")) throw Invalid("A subpath must start with S or M.");
                switch (command)
                {
                    case "M":
                        if (openSubpath) throw Unsupported("M within an open subpath");
                        goto case "S";
                    case "S": path.MoveTo(Next(), Next()); started = true; openSubpath = true; break;
                    case "L": path.LineTo(Next(), Next()); break;
                    case "B": path.CubicTo(Next(), Next(), Next(), Next(), Next(), Next()); break;
                    case "Q": path.QuadTo(Next(), Next(), Next(), Next()); break;
                    case "A":
                        float rx = Next(), ry = Next(), rotation = Next(), large = Next(), sweep = Next();
                        if (rx < 0 || ry < 0 || (large != 0 && large != 1) || (sweep != 0 && sweep != 1))
                            throw Invalid("Invalid elliptical arc.");
                        path.ArcTo(rx, ry, rotation, large == 0 ? SKPathArcSize.Small : SKPathArcSize.Large,
                            sweep == 0 ? SKPathDirection.CounterClockwise : SKPathDirection.Clockwise, Next(), Next());
                        break;
                    case "C": path.Close(); openSubpath = false; break;
                    default: throw Unsupported("Path command " + command);
                }
            }
            if (!started) throw Invalid("Empty path.");
            using var paint = new SKPaint { IsAntialias = true };
            // Validate colors even when their corresponding operation is disabled.
            SKColor fill = Color(obj.Element(Ofd + "FillColor"), scope, alpha);
            SKColor stroke = Color(obj.Element(Ofd + "StrokeColor"), scope, alpha);
            paint.StrokeWidth = Positive(obj.Attribute("LineWidth")?.Value ?? "0.353");
            paint.StrokeCap = (obj.Attribute("Cap")?.Value ?? "Butt") switch
            { "Butt" => SKStrokeCap.Butt, "Round" => SKStrokeCap.Round, "Square" => SKStrokeCap.Square, _ => throw Unsupported("Line cap") };
            paint.StrokeJoin = (obj.Attribute("Join")?.Value ?? "Miter") switch
            { "Miter" => SKStrokeJoin.Miter, "Round" => SKStrokeJoin.Round, "Bevel" => SKStrokeJoin.Bevel, _ => throw Unsupported("Line join") };
            paint.StrokeMiter = Positive(obj.Attribute("MiterLimit")?.Value ?? "3.528");
            if (Boolean(obj, "Fill", false))
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = fill;
                canvas.DrawPath(path, paint);
            }
            if (Boolean(obj, "Stroke", true))
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.Color = stroke;
                canvas.DrawPath(path, paint);
            }
            return;

            float Next()
            {
                return Number(Take());
            }

            string Take()
            {
                if (!match.Success) throw Invalid("Incomplete path command.");
                string value = match.Value;
                match = match.NextMatch();
                return value;
            }
        }

        private void PaintImage(SKCanvas canvas, XElement obj, Dictionary<string, Resource> scope, byte alpha)
        {
            _token.ThrowIfCancellationRequested();
            if (++_imageDraws > 10000) throw Invalid("The image draw budget exceeds 10,000 operations.");
            Resource resource = Lookup(scope, Required(obj, "ResourceID"), "MultiMedia");
            string path = OfdPackage.Resolve(resource.BaseFile, Text(One(resource.Element, "MediaFile")));
            if (!_images.TryGetValue(path, out SKImage? image))
            {
                byte[] bytes = ReadAsset(path);
                bool png = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
                bool jpeg = bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
                if (!png && !jpeg) throw Unsupported("Only PNG and JPEG images are supported");
                if (png)
                {
                    // Some native PNG codecs expose only the first APNG frame.
                    int offset = 8;
                    while (offset < bytes.Length)
                    {
                        Tick();
                        if (bytes.Length - offset < 12) throw Invalid("Truncated PNG chunk.");
                        uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
                        if (length > bytes.Length - offset - 12) throw Invalid("Invalid PNG chunk length.");
                        if (bytes.AsSpan(offset + 4, 4).SequenceEqual("acTL"u8)) throw Unsupported("Animated PNG images");
                        offset += (int)length + 12;
                    }
                }
                using SKData data = SKData.CreateCopy(bytes);
                using SKCodec codec = SKCodec.Create(data) ?? throw Invalid("Invalid image.");
                SKImageInfo info = codec.Info;
                long pixels = (long)info.Width * info.Height;
                if (info.Width <= 0 || info.Height <= 0 || pixels > 5000000)
                    throw Invalid("An image exceeds the five-million-pixel limit.");
                if (pixels > 16000000 - _decodedPixels) throw Invalid("The decoded image budget exceeds 16 million pixels.");
                if (codec.FrameCount > 1) throw Unsupported("Animated images");
                if (codec.EncodedOrigin != SKEncodedOrigin.TopLeft) throw Unsupported("Images with EXIF orientation");
                _token.ThrowIfCancellationRequested();
                _decodedPixels += pixels;
                using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) throw Invalid("Image decoding failed.");
                _token.ThrowIfCancellationRequested();
                // Immutable pixels can be retained by SKImage without a second decoded copy.
                bitmap.SetImmutable();
                image = SKImage.FromBitmap(bitmap) ?? throw Invalid("An image could not be retained.");
                _images.Add(path, image);
            }
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha(alpha), IsAntialias = true };
            // OFD images occupy the unit square; CTM supplies the physical placement and size.
            canvas.DrawImage(image, new SKRect(0, 0, 1, 1), new SKSamplingOptions(SKFilterMode.Linear), paint);
        }

        private byte[] ReadAsset(string path)
        {
            _token.ThrowIfCancellationRequested();
            byte[] bytes = _package.ReadBytes(path);
            _assetBytes += bytes.Length;
            if (_assetBytes > 256L * 1024 * 1024) throw Invalid("The cumulative asset-read budget exceeds 256 MiB.");
            return bytes;
        }

        private static SKColor Color(XElement? element, Dictionary<string, Resource> scope, byte objectAlpha)
        {
            if (element is null) return SKColors.Black.WithAlpha(objectAlpha);
            Check(element, "Value ColorSpace Alpha", "");
            string[] values = Words(Required(element, "Value"));
            string type = element.Attribute("ColorSpace") is { } reference
                ? Required(Lookup(scope, reference.Value, "ColorSpace").Element, "Type") : "RGB";
            if (values.Length != (type == "GRAY" ? 1 : 3)) throw Invalid("Invalid color component count.");
            byte r = Byte(values[0]), g = type == "GRAY" ? r : Byte(values[1]), b = type == "GRAY" ? r : Byte(values[2]);
            byte alpha = Byte(element.Attribute("Alpha")?.Value ?? "255");
            return new SKColor(r, g, b, (byte)((alpha * objectAlpha + 127) / 255));
        }

        private static Resource Lookup(Dictionary<string, Resource> scope, string id, string kind)
        {
            if (!scope.TryGetValue(id, out Resource? resource) || resource.Element.Name != Ofd + kind)
                throw Invalid($"Unknown {kind} resource {id}.");
            return resource;
        }

        public void Dispose()
        {
            foreach (SKImage image in _images.Values) image.Dispose();
            _images.Clear();
            foreach (SKTypeface typeface in _fonts.Values) typeface.Dispose();
            _fonts.Clear();
        }
    }

    private static void Check(XElement element, string attributes, string children, bool allowText = false)
    {
        if (element.Name.Namespace != Ofd) throw Unsupported("Foreign XML namespace");
        string[] allowedAttributes = Words(attributes), allowedChildren = Words(children);
        foreach (XAttribute attribute in element.Attributes())
            if (attribute.Name == XNamespace.Xml + "space" && attribute.Value is "default" or "preserve") continue;
            else if (!attribute.IsNamespaceDeclaration && (attribute.Name.Namespace != XNamespace.None || !allowedAttributes.Contains(attribute.Name.LocalName)))
                throw Unsupported(element.Name.LocalName + "@" + attribute.Name);
        foreach (XElement child in element.Elements())
            if (child.Name.Namespace != Ofd || !allowedChildren.Contains(child.Name.LocalName))
                throw Unsupported(element.Name.LocalName + "/" + child.Name);
        if (!allowText && element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            throw Invalid("Unexpected text in " + element.Name.LocalName + ".");
        // Repeated containers would otherwise be silently ignored by Element().
        foreach (var group in element.Elements().GroupBy(e => e.Name.LocalName))
            if (group.Count() > 1 && group.Key is not ("Page" or "Layer" or "TextObject" or "PathObject" or "ImageObject" or
                "TextCode" or "Template" or "TemplatePage" or "PublicRes" or "DocumentRes" or "PageRes" or
                "Font" or "MultiMedia" or "ColorSpace" or "Keyword"))
                throw Invalid("Duplicate " + group.Key + ".");
    }

    private static XElement One(XElement parent, string name) => parent.Element(Ofd + name) ?? throw Invalid("Missing " + name + ".");
    private static void Leaf(XElement element) => Check(element, "", "", allowText: true);
    private static string Text(XElement element)
    {
        Leaf(element);
        return string.IsNullOrWhiteSpace(element.Value) ? throw Invalid("Empty " + element.Name.LocalName + ".") : element.Value.Trim();
    }
    private static string Required(XElement element, string attribute) =>
        element.Attribute(attribute)?.Value is { } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw Invalid("Missing " + element.Name.LocalName + "@" + attribute + ".");
    private static string Id(XElement element)
    {
        string id = Required(element, "ID");
        if (!uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out uint number) || number == 0)
            throw Invalid("Invalid OFD ID.");
        return id;
    }
    private static string[] Words(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static float Number(string value) => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
        ? Finite(result) : throw Invalid("Invalid number.");
    private static float Finite(float value) => float.IsFinite(value) && Math.Abs(value) <= 1000000
        ? value : throw Invalid("A coordinate is non-finite or exceeds the supported range.");
    private static float Positive(string value)
    {
        float number = Number(value);
        return number > 0 ? number : throw Invalid("A dimension must be positive.");
    }
    private static float[] Numbers(string value, int count)
    {
        string[] words = Words(value);
        if (words.Length != count) throw Invalid("Invalid coordinate count.");
        return words.Select(Number).ToArray();
    }
    private static byte Byte(string value) => byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out byte result)
        ? result : throw Invalid("A color/alpha component must be an integer between 0 and 255.");
    private static bool Boolean(XElement element, string name, bool fallback) => element.Attribute(name)?.Value switch
    { null => fallback, "true" or "1" => true, "false" or "0" => false, _ => throw Invalid("Invalid boolean " + name + ".") };
    private static SKRect Box(string value)
    {
        float[] numbers = Numbers(value, 4);
        if (numbers[2] <= 0 || numbers[3] <= 0) throw Invalid("A boundary must have positive dimensions.");
        var box = new SKRect(numbers[0], numbers[1], Finite(numbers[0] + numbers[2]), Finite(numbers[1] + numbers[3]));
        if (box.Width <= 0 || box.Height <= 0) throw Invalid("A boundary is too small to represent.");
        return box;
    }
    private static SKRect Area(XElement element)
    {
        Check(element, "", "PhysicalBox ApplicationBox ContentBox BleedBox");
        foreach (XElement box in element.Elements()) Box(Text(box));
        SKRect result = Box(Text(One(element, "PhysicalBox")));
        // PDF's default user space cannot represent arbitrarily large physical pages reliably.
        if (result.Width * PointsPerMillimeter > 14400 || result.Height * PointsPerMillimeter > 14400)
            throw Invalid("The physical page exceeds 14,400 PDF points.");
        return result;
    }
    private static InvalidDataException Invalid(string message) => new("Invalid OFD: " + message);
    private static NotSupportedException Unsupported(string feature) => new("Unsupported OFD feature: " + feature + ".");
}
