using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Android.Graphics.Pdf;
using Android.OS;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Survoler.Documents;
using Survoler.Rendering;
using AndroidBitmap = Android.Graphics.Bitmap;

namespace Survoler.Android;

public sealed class AndroidPdfPageRendererFactory : IPdfPageRendererFactory
{
    public Task<IPdfPageRenderer> OpenAsync(
        string pdfPath,
        CancellationToken cancellationToken) =>
        Task.Run<IPdfPageRenderer>(
            () => AndroidPdfPageRenderer.Open(pdfPath, cancellationToken),
            cancellationToken);
}

internal sealed class AndroidPdfPageRenderer : IPdfPageRenderer
{
    private readonly PdfRenderer _renderer;
    private readonly ParcelFileDescriptor _descriptor;
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly int _targetWidth;
    private int _disposed;

    private AndroidPdfPageRenderer(
        PdfRenderer renderer,
        ParcelFileDescriptor descriptor,
        int targetWidth)
    {
        _renderer = renderer;
        _descriptor = descriptor;
        _targetWidth = targetWidth;
    }

    public int PageCount => _renderer.PageCount;

    public static AndroidPdfPageRenderer Open(
        string pdfPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ParcelFileDescriptor? descriptor = null;
        PdfRenderer? renderer = null;
        try
        {
            descriptor = ParcelFileDescriptor.Open(
                new Java.IO.File(pdfPath),
                ParcelFileMode.ReadOnly);
            if (descriptor is null)
            {
                throw new IOException("The PDF could not be opened.");
            }

            renderer = new PdfRenderer(descriptor);

            if (renderer.PageCount == 0)
            {
                throw new DocumentOpenException("The PDF has no pages.");
            }

            if (renderer.PageCount > PreviewLimits.MaxPdfPages)
            {
                throw new DocumentOpenException("The PDF has too many pages for quick preview.");
            }

            int screenWidth = global::Android.App.Application.Context.Resources?
                .DisplayMetrics?.WidthPixels ?? 1_080;
            int targetWidth = Math.Min(
                PreviewLimits.MaxPdfPageWidth,
                Math.Max(1_024, checked(screenWidth * 2)));

            var result = new AndroidPdfPageRenderer(renderer, descriptor, targetWidth);
            renderer = null;
            descriptor = null;
            return result;
        }
        catch (Exception exception)
        {
            renderer?.Dispose();
            descriptor?.Dispose();
            if (exception is Java.Lang.SecurityException)
            {
                throw new DocumentOpenException("Password-protected or restricted PDFs are not supported.");
            }
            throw;
        }
    }

    public async Task<Bitmap> RenderPageAsync(
        int index,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if ((uint)index >= (uint)PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        await _renderGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await Task.Run(
                () => RenderPage(index, cancellationToken),
                CancellationToken.None);
        }
        finally
        {
            _renderGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _renderGate.Wait();
        try
        {
            try
            {
                _renderer.Dispose();
            }
            finally
            {
                _descriptor.Dispose();
            }
        }
        finally
        {
            _renderGate.Release();
            _renderGate.Dispose();
        }
    }

    private Bitmap RenderPage(int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using PdfRenderer.Page page = _renderer.OpenPage(index);

        (int width, int height) = GetRenderSize(page.Width, page.Height);
        using AndroidBitmap nativeBitmap = AndroidBitmap.CreateBitmap(
            width,
            height,
            AndroidBitmap.Config.Argb8888!)
            ?? throw new OutOfMemoryException("The PDF page bitmap could not be allocated.");
        nativeBitmap.EraseColor(global::Android.Graphics.Color.White);
        page.Render(nativeBitmap, null, null, PdfRenderMode.ForDisplay);
        cancellationToken.ThrowIfCancellationRequested();

        int[] pixels = new int[checked(width * height)];
        nativeBitmap.GetPixels(pixels, 0, width, 0, 0, width, height);
        cancellationToken.ThrowIfCancellationRequested();

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Premul);
        try
        {
            using ILockedFramebuffer buffer = bitmap.Lock();
            CopyPixels(pixels, width, height, buffer);
            cancellationToken.ThrowIfCancellationRequested();
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private (int Width, int Height) GetRenderSize(int pageWidth, int pageHeight)
    {
        if (pageWidth <= 0 || pageHeight <= 0)
        {
            throw new InvalidDataException("The PDF contains an invalid page size.");
        }

        double scale = (double)_targetWidth / pageWidth;
        int width = Math.Max(1, (int)Math.Round(pageWidth * scale));
        int height = Math.Max(1, (int)Math.Round(pageHeight * scale));
        long pixels = checked((long)width * height);

        if (pixels > PreviewLimits.MaxPdfPagePixels)
        {
            scale *= Math.Sqrt((double)PreviewLimits.MaxPdfPagePixels / pixels);
            width = Math.Max(1, (int)Math.Floor(pageWidth * scale));
            height = Math.Max(1, (int)Math.Floor(pageHeight * scale));
        }

        return (width, height);
    }

    private static void CopyPixels(
        int[] pixels,
        int width,
        int height,
        ILockedFramebuffer destination)
    {
        int rowBytes = checked(width * sizeof(int));
        if (destination.RowBytes == rowBytes)
        {
            Marshal.Copy(pixels, 0, destination.Address, pixels.Length);
            return;
        }

        for (int row = 0; row < height; row++)
        {
            Marshal.Copy(
                pixels,
                row * width,
                IntPtr.Add(destination.Address, row * destination.RowBytes),
                width);
        }
    }
}
