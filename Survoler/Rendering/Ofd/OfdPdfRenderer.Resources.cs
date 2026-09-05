using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Survoler.Resources;
using static Survoler.Rendering.OfdXml;

namespace Survoler.Rendering;

internal sealed partial class OfdPdfRenderer
{
    private sealed record Resource(XElement Element, string BaseFile);

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
                            if (Required(item, "Type") != "Image") throw Unsupported(OfdStrings.Get("NonImageMultimedia"));
                            if (item.Attribute("Format") is { } format &&
                                !new[] { "PNG", "JPEG", "JPG" }.Contains(format.Value.ToUpperInvariant()))
                                throw Unsupported(OfdStrings.Format("ImageFormat", format.Value));
                            Text(One(item, "MediaFile"));
                            break;
                        case "ColorSpace":
                            Check(item, "ID Type BitsPerComponent", "");
                            if (Required(item, "Type") is not ("RGB" or "GRAY") ||
                                (item.Attribute("BitsPerComponent")?.Value ?? "8") != "8")
                                throw Unsupported(OfdStrings.Get("ColorSpacesSupported"));
                            break;
                    }
                    if (!scope.TryAdd(Id(item), new Resource(item, baseFile)))
                        throw Invalid("DuplicateResourceId", Id(item));
                }
            }
        }
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
                    throw Unsupported(OfdStrings.Get("TrueTypeRequired"));
                embedded = Load(key, bytes);
            }
            if (!Covers(embedded)) throw Invalid("EmbeddedFontMissingGlyphs");
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
                    ?? throw Invalid("FallbackFontReadFailed");
                ReserveFontBytes(source.Length);
                using SKData data = SKData.Create(source) ?? throw Invalid("FallbackFontLoadFailed");
                desktop = SKTypeface.FromData(data, collectionIndex) ?? throw Invalid("FallbackFontLoadFailed");
                _fonts.Add(key, desktop);
            }
            if (Covers(desktop)) return desktop;
        }
        throw Invalid("NoFontCoverage");

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
            SKTypeface typeface = SKTypeface.FromData(data) ?? throw Invalid("FontLoadFailed");
            _fonts.Add(key, typeface);
            return typeface;
        }

        void ReserveFontBytes(long count)
        {
            _token.ThrowIfCancellationRequested();
            if (count <= 0 || count > 64L * 1024 * 1024 - _fontBytes)
                throw Invalid("FontBudget");
            _fontBytes += count;
        }
    }

    private byte[] ReadAsset(string path)
    {
        _token.ThrowIfCancellationRequested();
        byte[] bytes = _package.ReadBytes(path);
        _assetBytes += bytes.Length;
        if (_assetBytes > 256L * 1024 * 1024) throw Invalid("AssetReadBudget");
        return bytes;
    }

    private static Resource Lookup(Dictionary<string, Resource> scope, string id, string kind)
    {
        if (!scope.TryGetValue(id, out Resource? resource) || resource.Element.Name != Ofd + kind)
            throw Invalid("UnknownResource", kind, id);
        return resource;
    }
}
