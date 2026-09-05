using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using OfficeIMO.Pdf;
using Survoler.Resources;

namespace Survoler.Rendering;

public sealed class PdfDocumentPreview : IDocumentPreview
{
    private const int MaxCachedPages = 2;

    private readonly IPdfPageRenderer _renderer;
    private readonly ConvertedPdfDocument _document;
    private readonly IReadOnlyList<string> _navigationItems;
    private readonly Dictionary<int, Bitmap> _pageCache = new();
    private readonly Dictionary<int, PdfPageInteractionMap> _interactionCache = new();
    private readonly LinkedList<int> _cacheOrder = new();
    private readonly Lazy<byte[]> _pdfBytes;
    private readonly object _interactionSync = new();
    private int _disposed;

    public PdfDocumentPreview(
        IPdfPageRenderer renderer,
        ConvertedPdfDocument document)
    {
        _renderer = renderer;
        _document = document;
        _pdfBytes = new Lazy<byte[]>(() => File.ReadAllBytes(document.Path));
        _navigationItems = Enumerable.Range(1, renderer.PageCount)
            .Select(index => Strings.Format("Page", index))
            .ToArray();
    }

    public Bitmap PageImage { get; private set; } = null!;

    public IReadOnlyList<string> NavigationItems => _navigationItems;

    public int SelectedIndex { get; private set; }

    public string? Warning => _document.Warning;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        PageImage = await SelectAsync(0, cancellationToken);
    }

    public async Task<Bitmap> SelectAsync(int index, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if ((uint)index >= (uint)_navigationItems.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (!_pageCache.TryGetValue(index, out Bitmap? image))
        {
            image = await _renderer.RenderPageAsync(index, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _pageCache[index] = image;
        }

        TouchCacheEntry(index);
        SelectedIndex = index;
        PageImage = image;
        return image;
    }

    public async Task<PdfPageInteractionMap?> GetInteractionMapAsync(
        int index,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if ((uint)index >= (uint)_navigationItems.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        lock (_interactionSync)
        {
            if (_interactionCache.TryGetValue(index, out PdfPageInteractionMap? cached))
            {
                return cached;
            }
        }

        try
        {
            PdfPageInteractionMap map = await Task.Run(
                () => PdfPageInteractionMap.Create(_pdfBytes.Value, index + 1),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_interactionSync)
            {
                _interactionCache[index] = map;
            }
            return map;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (Bitmap image in _pageCache.Values)
        {
            image.Dispose();
        }

        _pageCache.Clear();
        _cacheOrder.Clear();
        lock (_interactionSync)
        {
            _interactionCache.Clear();
        }
        _renderer.Dispose();
        _document.Dispose();
    }

    private void TouchCacheEntry(int index)
    {
        _cacheOrder.Remove(index);
        _cacheOrder.AddLast(index);

        while (_cacheOrder.Count > MaxCachedPages)
        {
            int expiredIndex = _cacheOrder.First!.Value;
            _cacheOrder.RemoveFirst();
            if (_pageCache.Remove(expiredIndex, out Bitmap? expiredImage))
            {
                expiredImage.Dispose();
            }
            lock (_interactionSync)
            {
                _interactionCache.Remove(expiredIndex);
            }
        }
    }
}
