using System;
using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Survoler.Rendering;

public static class PreviewHtmlSanitizer
{
    private const string ContentSecurityPolicy =
        "default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:; " +
        "script-src 'none'; object-src 'none'; frame-src 'none'; media-src 'none'; " +
        "connect-src 'none'; form-action 'none'; base-uri 'none'";

    public static string Sanitize(string html, bool allowSvg = false)
    {
        var parser = new HtmlParser();
        IDocument document = parser.ParseDocument(html);

        string blockedElements = allowSvg
            ? "script,iframe,frame,object,embed,foreignObject,form,base,link"
            : "script,iframe,frame,object,embed,foreignObject,form,base,link,svg";

        foreach (IElement element in document.QuerySelectorAll(blockedElements).ToArray())
        {
            element.Remove();
        }

        foreach (IElement meta in document.QuerySelectorAll("meta[http-equiv]").ToArray())
        {
            meta.Remove();
        }

        foreach (IElement element in document.All.ToArray())
        {
            foreach (IAttr attribute in element.Attributes.ToArray())
            {
                string name = attribute.Name;
                string value = attribute.Value.Trim();

                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("srcdoc", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("data-officeimo-powerpoint-hyperlink", StringComparison.OrdinalIgnoreCase))
                {
                    element.RemoveAttribute(name);
                    continue;
                }

                if (name.Equals("style", StringComparison.OrdinalIgnoreCase) && ContainsUnsafeCss(value))
                {
                    element.RemoveAttribute(name);
                    continue;
                }

                if (name.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("xlink:href", StringComparison.OrdinalIgnoreCase))
                {
                    bool isImageReference = element.LocalName.Equals("image", StringComparison.OrdinalIgnoreCase);
                    if ((!isImageReference && !value.StartsWith('#')) ||
                        (isImageReference && !IsSafeImageDataUri(value)))
                    {
                        element.RemoveAttribute(name);
                    }
                }

                if (name.Equals("src", StringComparison.OrdinalIgnoreCase) &&
                    !IsSafeImageDataUri(value))
                {
                    element.RemoveAttribute(name);
                }

                if (name.Equals("srcset", StringComparison.OrdinalIgnoreCase))
                {
                    element.RemoveAttribute(name);
                }
            }
        }

        foreach (IElement style in document.QuerySelectorAll("style").ToArray())
        {
            if (ContainsUnsafeCss(style.TextContent))
            {
                style.Remove();
            }
        }

        IElement head = document.Head ?? document.CreateElement("head");
        if (document.Head is null)
        {
            document.DocumentElement?.Prepend(head);
        }

        IElement csp = document.CreateElement("meta");
        csp.SetAttribute("http-equiv", "Content-Security-Policy");
        csp.SetAttribute("content", ContentSecurityPolicy);
        head.Prepend(csp);

        IElement referrer = document.CreateElement("meta");
        referrer.SetAttribute("name", "referrer");
        referrer.SetAttribute("content", "no-referrer");
        head.Prepend(referrer);

        string documentHtml = document.DocumentElement?.OuterHtml ?? "<html><body></body></html>";
        return $"<!doctype html>{Environment.NewLine}{documentHtml}";
    }

    private static bool IsSafeImageDataUri(string value) =>
        value.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("data:image/jpg;base64,", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("data:image/gif;base64,", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("data:image/webp;base64,", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsUnsafeCss(string value) =>
        value.Contains("@import", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("file:", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("content:", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("https://", StringComparison.OrdinalIgnoreCase);
}
