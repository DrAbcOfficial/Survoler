using System;
using System.IO;

namespace Survoler.Documents;

public enum OfficeFileKind
{
    Doc,
    Docx,
    Xls,
    Xlsx,
    Ppt,
    Pptx
}

public enum OfficeDocumentFamily
{
    Word,
    Spreadsheet,
    Presentation
}

public static class OfficeFileKinds
{
    public static bool TryFromFileName(string fileName, out OfficeFileKind kind)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        kind = extension switch
        {
            ".doc" => OfficeFileKind.Doc,
            ".docx" => OfficeFileKind.Docx,
            ".xls" => OfficeFileKind.Xls,
            ".xlsx" => OfficeFileKind.Xlsx,
            ".ppt" => OfficeFileKind.Ppt,
            ".pptx" => OfficeFileKind.Pptx,
            _ => default
        };

        return extension is
            ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx";
    }

    public static OfficeDocumentFamily GetFamily(this OfficeFileKind kind) => kind switch
    {
        OfficeFileKind.Doc or OfficeFileKind.Docx => OfficeDocumentFamily.Word,
        OfficeFileKind.Xls or OfficeFileKind.Xlsx => OfficeDocumentFamily.Spreadsheet,
        OfficeFileKind.Ppt or OfficeFileKind.Pptx => OfficeDocumentFamily.Presentation,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static bool IsLegacy(this OfficeFileKind kind) => kind is
        OfficeFileKind.Doc or OfficeFileKind.Xls or OfficeFileKind.Ppt;
}
