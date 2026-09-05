using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using OfficeIMO.Pdf;

namespace Survoler.Rendering;

public interface IDocumentPreview : IDisposable
{
    Bitmap PageImage { get; }

    IReadOnlyList<string> NavigationItems { get; }

    int SelectedIndex { get; }

    string? Warning { get; }

    Task<Bitmap> SelectAsync(int index, CancellationToken cancellationToken);

    Task<PdfPageInteractionMap?> GetInteractionMapAsync(
        int index,
        CancellationToken cancellationToken);
}
