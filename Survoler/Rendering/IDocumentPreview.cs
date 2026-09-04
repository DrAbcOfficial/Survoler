using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Survoler.Rendering;

public interface IDocumentPreview : IDisposable
{
    string Html { get; }

    IReadOnlyList<string> NavigationItems { get; }

    int SelectedIndex { get; }

    Task<string> SelectAsync(int index, CancellationToken cancellationToken);
}
