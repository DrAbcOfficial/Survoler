using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OfficeIMO.Drawing;
using OfficeIMO.PowerPoint;
using Survoler.Documents;

namespace Survoler.Rendering;

public sealed class PresentationDocumentPreview : IDocumentPreview
{
    private const int MaxCachedSlides = 3;

    private readonly PowerPointPresentation _presentation;
    private readonly PowerPointSlide[] _slides;
    private readonly IReadOnlyList<string> _navigationItems;
    private readonly Dictionary<int, string> _htmlCache = new();
    private readonly Queue<int> _cacheOrder = new();

    public PresentationDocumentPreview(
        PowerPointPresentation presentation,
        PowerPointSlide[] slides)
    {
        _presentation = presentation;
        _slides = slides;
        _navigationItems = Enumerable.Range(1, slides.Length)
            .Select(index => $"Slide {index}")
            .ToArray();
    }

    public string Html { get; private set; } = string.Empty;

    public IReadOnlyList<string> NavigationItems => _navigationItems;

    public int SelectedIndex { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Html = await SelectAsync(0, cancellationToken);
    }

    public async Task<string> SelectAsync(int index, CancellationToken cancellationToken)
    {
        if ((uint)index >= (uint)_slides.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (_htmlCache.TryGetValue(index, out string? cachedHtml))
        {
            SelectedIndex = index;
            Html = cachedHtml;
            return cachedHtml;
        }

        PowerPointSlide slide = _slides[index];
        PowerPointImageExportOptions options = CreateOptions();
        OfficeImageExportResult result = await Task.Run(
            () => slide.ExportImage(OfficeImageExportFormat.Svg, options, cancellationToken),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        string svg = Encoding.UTF8.GetString(result.Bytes);
        string html = PreviewHtmlSanitizer.Sanitize(WrapSvg(svg), allowSvg: true);
        AddToCache(index, html);
        SelectedIndex = index;
        Html = html;
        return html;
    }

    public void Dispose()
    {
        _htmlCache.Clear();
        _cacheOrder.Clear();
        _presentation.Dispose();
    }

    private static PowerPointImageExportOptions CreateOptions() => new()
    {
        IncludeSlideBackground = true,
        IncludeSlideContent = true,
        IncludePictures = true,
        IncludeTextBoxes = true,
        IncludeAutoShapes = true,
        IncludeTables = true,
        IncludeCharts = false,
        IncludeSmartArt = false,
        IncludeHiddenShapes = false,
        MaxGroupShapeDepth = 16,
        MaximumEmbeddedImageBytes = PreviewLimits.MaxImageBytes,
        MaximumTotalEmbeddedImageBytes = PreviewLimits.MaxTotalImageBytes,
        MaximumOutputWidth = PreviewLimits.MaxSlideWidth,
        MaximumOutputHeight = PreviewLimits.MaxSlideHeight,
        MaximumOutputCount = 1,
        MaximumTotalEncodedBytes = 64L * 1024 * 1024,
        RenderTimeout = TimeSpan.FromSeconds(30)
    };

    private static string WrapSvg(string svg) => $$"""
        <!doctype html>
        <html>
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=5">
          <style>
            html,body{margin:0;min-height:100%;background:#202725;color:#fff}
            body{display:flex;align-items:center;justify-content:center;padding:12px;box-sizing:border-box}
            svg{display:block;max-width:100%;max-height:calc(100vh - 24px);width:auto;height:auto;box-shadow:0 12px 36px #0008}
          </style>
        </head>
        <body>{{svg}}</body>
        </html>
        """;

    private void AddToCache(int index, string html)
    {
        _htmlCache[index] = html;
        _cacheOrder.Enqueue(index);

        while (_cacheOrder.Count > MaxCachedSlides)
        {
            int expiredIndex = _cacheOrder.Dequeue();
            if (expiredIndex != index)
            {
                _htmlCache.Remove(expiredIndex);
            }
        }
    }
}
