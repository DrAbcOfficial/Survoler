using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Survoler.Rendering;

public interface IDocumentPreview : IDisposable
{
    Bitmap PageImage { get; }

    IReadOnlyList<string> NavigationItems { get; }

    int SelectedIndex { get; }

    Task<Bitmap> SelectAsync(int index, CancellationToken cancellationToken);
}
