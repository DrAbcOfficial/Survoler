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
    [DataRow("report.wps", OfficeFileKind.Doc, OfficeDocumentFamily.Word, true)]
    [DataRow("report.WPS", OfficeFileKind.Doc, OfficeDocumentFamily.Word, true)]
    [DataRow("report.wpt", OfficeFileKind.Doc, OfficeDocumentFamily.Word, true)]
    [DataRow("report.WpT", OfficeFileKind.Doc, OfficeDocumentFamily.Word, true)]
    [DataRow("report.et", OfficeFileKind.Xls, OfficeDocumentFamily.Spreadsheet, true)]
    [DataRow("report.ET", OfficeFileKind.Xls, OfficeDocumentFamily.Spreadsheet, true)]
    [DataRow("report.ett", OfficeFileKind.Xls, OfficeDocumentFamily.Spreadsheet, true)]
    [DataRow("report.EtT", OfficeFileKind.Xls, OfficeDocumentFamily.Spreadsheet, true)]
    [DataRow("report.dps", OfficeFileKind.Ppt, OfficeDocumentFamily.Presentation, true)]
    [DataRow("report.DPS", OfficeFileKind.Ppt, OfficeDocumentFamily.Presentation, true)]
    [DataRow("report.dpt", OfficeFileKind.Ppt, OfficeDocumentFamily.Presentation, true)]
    [DataRow("report.DpT", OfficeFileKind.Ppt, OfficeDocumentFamily.Presentation, true)]
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
    [DataRow("report.pdf")]
    [DataRow("report")]
    [DataRow("report.wpsx")]
    [DataRow("report.wps.exe")]
    public void RejectsUnrelatedExtensions(string fileName)
    {
        Assert.IsFalse(OfficeFileKinds.TryFromFileName(fileName, out _));
    }
}
