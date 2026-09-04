using System.Threading;
using System.Threading.Tasks;
using Survoler.Documents;

namespace Survoler.Rendering;

public interface IDocumentPreviewRenderer
{
    bool CanRender(OfficeFileKind kind);

    Task<IDocumentPreview> CreateAsync(
        DocumentSession session,
        CancellationToken cancellationToken);
}
