using Survoler.Documents;

namespace Survoler.Tests;

[TestClass]
public sealed class OfficeFileKindsTests
{
    [TestMethod]
    [DataRow("report.doc", OfficeFileKind.Doc, OfficeDocumentFamily.Word, true)]
    [DataRow("report.DOCX", OfficeFileKind.Docx, OfficeDocumentFamily.Word, false)]
    [DataRow("report.xls", OfficeFileKind.Xls, OfficeDocumentFamily.Spreadsheet, true)]
    [DataRow("report.xlsx", OfficeFileKind.Xlsx, OfficeDocumentFamily.Spreadsheet, false)]
    [DataRow("report.ppt", OfficeFileKind.Ppt, OfficeDocumentFamily.Presentation, true)]
    [DataRow("report.pptx", OfficeFileKind.Pptx, OfficeDocumentFamily.Presentation, false)]
    public void RecognizesSupportedExtensions(
        string fileName,
        OfficeFileKind expectedKind,
        OfficeDocumentFamily expectedFamily,
        bool expectedLegacy)
    {
        bool recognized = OfficeFileKinds.TryFromFileName(fileName, out OfficeFileKind kind);

        Assert.IsTrue(recognized);
        Assert.AreEqual(expectedKind, kind);
        Assert.AreEqual(expectedFamily, kind.GetFamily());
        Assert.AreEqual(expectedLegacy, kind.IsLegacy());
    }

    [TestMethod]
    public void RejectsUnrelatedExtensions()
    {
        Assert.IsFalse(OfficeFileKinds.TryFromFileName("report.pdf", out _));
    }
}
