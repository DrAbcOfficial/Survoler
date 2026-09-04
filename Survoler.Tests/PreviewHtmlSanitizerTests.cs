using Survoler.Rendering;

namespace Survoler.Tests;

[TestClass]
public sealed class PreviewHtmlSanitizerTests
{
    [TestMethod]
    public void RemovesActiveAndExternalContent()
    {
        const string html = """
            <html><head><style>body{color:#123}</style></head><body>
              <script>alert(1)</script>
              <a href="javascript:alert(1)" onclick="alert(2)">unsafe</a>
              <img src="https://example.test/image.png" onerror="alert(3)">
              <img src="data:image/png;base64,AA==">
              <iframe src="https://example.test"></iframe>
              <svg><foreignObject>unsafe</foreignObject></svg>
            </body></html>
            """;

        string sanitized = PreviewHtmlSanitizer.Sanitize(html);

        StringAssert.Contains(sanitized, "Content-Security-Policy");
        StringAssert.Contains(sanitized, "data:image/png;base64,AA==");
        Assert.IsFalse(sanitized.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(sanitized.Contains("javascript:", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(sanitized.Contains("onclick", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(sanitized.Contains("https://example.test", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(sanitized.Contains("<iframe", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(sanitized.Contains("<svg", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void KeepsSafeInlineSvgForPresentationPreview()
    {
        const string html = """
            <html><body><svg viewBox="0 0 10 10">
              <text x="1" y="5">Slide</text>
              <image href="data:image/png;base64,AA==" />
            </svg></body></html>
            """;

        string sanitized = PreviewHtmlSanitizer.Sanitize(html, allowSvg: true);

        StringAssert.Contains(sanitized, "<svg");
        StringAssert.Contains(sanitized, ">Slide</text>");
        StringAssert.Contains(sanitized, "data:image/png;base64,AA==");
    }
}
