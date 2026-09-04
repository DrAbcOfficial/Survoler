using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Survoler.Documents;

namespace Survoler.Rendering;

public sealed class DocumentPreviewService
{
    private readonly IReadOnlyList<IDocumentPreviewRenderer> _renderers;

    public DocumentPreviewService(params IDocumentPreviewRenderer[] renderers)
    {
        _renderers = renderers;
    }

    public Task<IDocumentPreview> CreateAsync(
        DocumentSession session,
        CancellationToken cancellationToken)
    {
        IDocumentPreviewRenderer? renderer = _renderers.FirstOrDefault(
            candidate => candidate.CanRender(session.Kind));

        if (renderer is null)
        {
            throw new NotSupportedException("No preview renderer is available for this file.");
        }

        return renderer.CreateAsync(session, cancellationToken);
    }
}
