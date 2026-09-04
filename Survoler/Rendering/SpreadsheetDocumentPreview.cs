using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OfficeIMO.Drawing;
using OfficeIMO.Excel;
using OfficeIMO.Excel.Html;
using Survoler.Documents;

namespace Survoler.Rendering;

public sealed class SpreadsheetDocumentPreview : IDocumentPreview
{
    private const int MaxCachedSheets = 3;

    private readonly ExcelDocument _document;
    private readonly ExcelSheet[] _sheets;
    private readonly IReadOnlyList<string> _navigationItems;
    private readonly Dictionary<int, string> _htmlCache = new();
    private readonly Queue<int> _cacheOrder = new();

    public SpreadsheetDocumentPreview(ExcelDocument document, ExcelSheet[] sheets)
    {
        _document = document;
        _sheets = sheets;
        _navigationItems = sheets.Select(sheet => sheet.Name).ToArray();
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
        if ((uint)index >= (uint)_sheets.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (_htmlCache.TryGetValue(index, out string? cachedHtml))
        {
            SelectedIndex = index;
            Html = cachedHtml;
            return cachedHtml;
        }

        ExcelSheet sheet = _sheets[index];
        ExcelHtmlSaveOptions options = CreateOptions(sheet.Name);
        string html = await Task.Run(() => sheet.ToHtml(options), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        html = PreviewHtmlSanitizer.Sanitize(html);
        AddToCache(index, html);
        SelectedIndex = index;
        Html = html;
        return html;
    }

    public void Dispose()
    {
        _htmlCache.Clear();
        _cacheOrder.Clear();
        _document.Dispose();
    }

    private static ExcelHtmlSaveOptions CreateOptions(string title)
    {
        ExcelHtmlSaveOptions options = ExcelHtmlSaveOptions.CreateSemanticTablesProfile(
            OfficeVisualThemeKind.Plain);
        options.Title = title;
        options.HeaderMode = ExcelHtmlHeaderMode.None;
        options.IncludePivotInventory = false;
        options.MaxRowsPerSheet = PreviewLimits.MaxSpreadsheetRows;
        options.MaxColumnsPerSheet = PreviewLimits.MaxSpreadsheetColumns;
        options.MaxCellsPerSheet = PreviewLimits.MaxSpreadsheetCells;
        options.MaxMergedRangesPerSheet = PreviewLimits.MaxSpreadsheetMergedRanges;
        return options;
    }

    private void AddToCache(int index, string html)
    {
        _htmlCache[index] = html;
        _cacheOrder.Enqueue(index);

        while (_cacheOrder.Count > MaxCachedSheets)
        {
            int expiredIndex = _cacheOrder.Dequeue();
            if (expiredIndex != index)
            {
                _htmlCache.Remove(expiredIndex);
            }
        }
    }
}
