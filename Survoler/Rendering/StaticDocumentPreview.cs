using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Survoler.Rendering;

public sealed class StaticDocumentPreview : IDocumentPreview
{
    private static readonly IReadOnlyList<string> NoNavigationItems = Array.Empty<string>();

    public StaticDocumentPreview(string html)
    {
        Html = html;
    }

    public string Html { get; }

    public IReadOnlyList<string> NavigationItems => NoNavigationItems;

    public int SelectedIndex => 0;

    public Task<string> SelectAsync(int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (index != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return Task.FromResult(Html);
    }

    public void Dispose()
    {
    }
}
