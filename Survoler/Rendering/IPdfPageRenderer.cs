using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Survoler.Rendering;

public interface IPdfPageRendererFactory
{
    Task<IPdfPageRenderer> OpenAsync(string pdfPath, CancellationToken cancellationToken);
}

public interface IPdfPageRenderer : IDisposable
{
    int PageCount { get; }

    Task<Bitmap> RenderPageAsync(int index, CancellationToken cancellationToken);
}
