using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OfficeIMO;
using OfficeIMO.Drawing;
using OfficeIMO.Excel;
using OfficeIMO.Excel.Html;
using Survoler.Documents;

namespace Survoler.Rendering;

public sealed class SpreadsheetPreviewRenderer : IDocumentPreviewRenderer
{
    public bool CanRender(OfficeFileKind kind) =>
        kind is OfficeFileKind.Xls or OfficeFileKind.Xlsx;

    public async Task<IDocumentPreview> CreateAsync(
        DocumentSession session,
        CancellationToken cancellationToken)
    {
        var loadOptions = new ExcelLoadOptions
        {
            AccessMode = DocumentAccessMode.ReadOnly,
            PersistenceMode = DocumentPersistenceMode.Explicit,
            MaxInputBytes = PreviewLimits.MaxInputBytes,
            PackageSecurity = PreviewLimits.CreatePackageSecurity()
        };

        ExcelDocument document = await ExcelDocument.LoadAsync(
            session.LocalPath,
            loadOptions,
            cancellationToken);

        try
        {
            ExcelSheet[] visibleSheets = document.Sheets
                .Where(sheet => !sheet.Hidden)
                .ToArray();

            if (visibleSheets.Length == 0)
            {
                throw new DocumentOpenException("This workbook has no visible worksheets.");
            }

            var preview = new SpreadsheetDocumentPreview(document, visibleSheets);
            await preview.InitializeAsync(cancellationToken);
            return preview;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }
}
