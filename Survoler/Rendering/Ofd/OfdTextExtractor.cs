using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Survoler.Documents;
using Survoler.Resources;

namespace Survoler.Rendering;

internal static class OfdTextExtractor
{
    private static readonly XNamespace Ofd = "http://www.ofdspec.org/2016";

    internal static List<List<string>>? Extract(OfdPackage package, CancellationToken token)
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
            root.Elements(Ofd + "DocBody").Count() != 1) return null;
        XElement body = One(root, "DocBody")!;
        if (body.Attributes().Any(a => !a.IsNamespaceDeclaration) ||
            body.Elements().Any(e => e.Name != Ofd + "DocInfo" && e.Name != Ofd + "DocRoot" &&
                e.Name != Ofd + "Signatures")) return null;
        XElement docRoot = One(body, "DocRoot")!;
        if (docRoot.HasElements) throw Invalid("UnexpectedText", "DocRoot");
        string documentPath = OfdPackage.Resolve("OFD.xml", docRoot.Value.Trim());
        XElement document = package.ReadXml(documentPath).Root ?? throw Invalid("EmptyXml");
        string[] documentChildren = { "CommonData", "Pages", "Outlines", "Permissions", "Actions",
            "VPreferences", "Bookmarks", "Annotations", "CustomTags", "Attachments", "Extensions" };
        if (document.Name != Ofd + "Document" ||
            document.Attributes().Any(a => !a.IsNamespaceDeclaration) ||
            document.Elements().Any(e => e.Name.Namespace != Ofd || !documentChildren.Contains(e.Name.LocalName)))
            return null;

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
        return hasText ? textPages : null;

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
