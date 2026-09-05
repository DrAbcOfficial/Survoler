using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SkiaSharp;
using Survoler.Resources;

namespace Survoler.Rendering;

internal static class OfdXml
{
    internal static readonly XNamespace Ofd = "http://www.ofdspec.org/2016";
    internal const float PointsPerMillimeter = 72f / 25.4f;

    internal static void Check(XElement element, string attributes, string children, bool allowText = false)
    {
        if (element.Name.Namespace != Ofd) throw Unsupported(OfdStrings.Get("ForeignXmlNamespace"));
        string[] allowedAttributes = Words(attributes), allowedChildren = Words(children);
        foreach (XAttribute attribute in element.Attributes())
            if (attribute.Name == XNamespace.Xml + "space" && attribute.Value is "default" or "preserve") continue;
            else if (!attribute.IsNamespaceDeclaration && (attribute.Name.Namespace != XNamespace.None || !allowedAttributes.Contains(attribute.Name.LocalName)))
                throw Unsupported(element.Name.LocalName + "@" + attribute.Name);
        foreach (XElement child in element.Elements())
            if (child.Name.Namespace != Ofd || !allowedChildren.Contains(child.Name.LocalName))
                throw Unsupported(element.Name.LocalName + "/" + child.Name);
        if (!allowText && element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            throw Invalid("UnexpectedText", element.Name.LocalName);
        // Repeated containers would otherwise be silently ignored by Element().
        foreach (var group in element.Elements().GroupBy(e => e.Name.LocalName))
            if (group.Count() > 1 && group.Key is not ("Page" or "Layer" or "TextObject" or "PathObject" or "ImageObject" or
                "TextCode" or "Template" or "TemplatePage" or "PublicRes" or "DocumentRes" or "PageRes" or
                "Font" or "MultiMedia" or "ColorSpace" or "Keyword"))
                throw Invalid("DuplicateElement", group.Key);
    }

    internal static XElement One(XElement parent, string name) => parent.Element(Ofd + name) ?? throw Invalid("MissingElement", name);
    internal static void Leaf(XElement element) => Check(element, "", "", allowText: true);
    internal static string Text(XElement element)
    {
        Leaf(element);
        return string.IsNullOrWhiteSpace(element.Value) ? throw Invalid("EmptyElement", element.Name.LocalName) : element.Value.Trim();
    }
    internal static string Required(XElement element, string attribute) =>
        element.Attribute(attribute)?.Value is { } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw Invalid("MissingAttribute", element.Name.LocalName, attribute);
    internal static string Id(XElement element)
    {
        string id = Required(element, "ID");
        if (!uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out uint number) || number == 0)
            throw Invalid("InvalidId");
        return id;
    }
    internal static string[] Words(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    internal static float Number(string value) => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
        ? Finite(result) : throw Invalid("InvalidNumber");
    internal static float Finite(float value) => float.IsFinite(value) && Math.Abs(value) <= 1000000
        ? value : throw Invalid("CoordinateRange");
    internal static float Positive(string value)
    {
        float number = Number(value);
        return number > 0 ? number : throw Invalid("PositiveDimension");
    }
    internal static float[] Numbers(string value, int count)
    {
        string[] words = Words(value);
        if (words.Length != count) throw Invalid("CoordinateCount");
        return words.Select(Number).ToArray();
    }
    internal static byte Byte(string value) => byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out byte result)
        ? result : throw Invalid("ColorComponentRange");
    internal static bool Boolean(XElement element, string name, bool fallback) => element.Attribute(name)?.Value switch
    { null => fallback, "true" or "1" => true, "false" or "0" => false, _ => throw Invalid("InvalidBoolean", name) };
    internal static SKRect Box(string value)
    {
        float[] numbers = Numbers(value, 4);
        if (numbers[2] <= 0 || numbers[3] <= 0) throw Invalid("PositiveBoundary");
        var box = new SKRect(numbers[0], numbers[1], Finite(numbers[0] + numbers[2]), Finite(numbers[1] + numbers[3]));
        if (box.Width <= 0 || box.Height <= 0) throw Invalid("BoundaryTooSmall");
        return box;
    }
    internal static SKRect Area(XElement element)
    {
        Check(element, "", "PhysicalBox ApplicationBox ContentBox BleedBox");
        foreach (XElement box in element.Elements()) Box(Text(box));
        SKRect result = Box(Text(One(element, "PhysicalBox")));
        // PDF's default user space cannot represent arbitrarily large physical pages reliably.
        if (result.Width * PointsPerMillimeter > 14400 || result.Height * PointsPerMillimeter > 14400)
            throw Invalid("PhysicalPageLimit");
        return result;
    }
    internal static InvalidDataException Invalid(string key, params object[] args)
    {
        var exception = new InvalidDataException(OfdStrings.Format("InvalidPrefix", OfdStrings.Format(key, args)));
        exception.Data[OfdStrings.DiagnosticMarker] = true;
        return exception;
    }

    internal static NotSupportedException Unsupported(string feature)
    {
        var exception = new NotSupportedException(OfdStrings.Format("UnsupportedPrefix", feature));
        exception.Data[OfdStrings.DiagnosticMarker] = true;
        return exception;
    }
}
