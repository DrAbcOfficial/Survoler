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
    [DataRow("report.xlsm", OfficeFileKind.Xlsx, OfficeDocumentFamily.Spreadsheet, false)]
    [DataRow("report.XLSM", OfficeFileKind.Xlsx, OfficeDocumentFamily.Spreadsheet, false)]
    [DataRow("report.xlt", OfficeFileKind.Xls, OfficeDocumentFamily.Spreadsheet, true)]
    [DataRow("report.XLT", OfficeFileKind.Xls, OfficeDocumentFamily.Spreadsheet, true)]
    [DataRow("report.xltm", OfficeFileKind.Xlsx, OfficeDocumentFamily.Spreadsheet, false)]
    [DataRow("report.XLTM", OfficeFileKind.Xlsx, OfficeDocumentFamily.Spreadsheet, false)]
    [DataRow("report.dot", OfficeFileKind.Doc, OfficeDocumentFamily.Word, true)]
    [DataRow("report.DOT", OfficeFileKind.Doc, OfficeDocumentFamily.Word, true)]
    [DataRow("report.dotx", OfficeFileKind.Docx, OfficeDocumentFamily.Word, false)]
    [DataRow("report.DOTX", OfficeFileKind.Docx, OfficeDocumentFamily.Word, false)]
    [DataRow("report.xla", OfficeFileKind.Xls, OfficeDocumentFamily.Spreadsheet, true)]
    [DataRow("report.XLA", OfficeFileKind.Xls, OfficeDocumentFamily.Spreadsheet, true)]
    [DataRow("report.xlam", OfficeFileKind.Xlsx, OfficeDocumentFamily.Spreadsheet, false)]
    [DataRow("report.XLAM", OfficeFileKind.Xlsx, OfficeDocumentFamily.Spreadsheet, false)]
    [DataRow("report.pptm", OfficeFileKind.Pptx, OfficeDocumentFamily.Presentation, false)]
    [DataRow("report.PPTM", OfficeFileKind.Pptx, OfficeDocumentFamily.Presentation, false)]
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
