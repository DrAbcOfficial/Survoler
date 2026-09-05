using Avalonia.Platform.Storage;
using Survoler.Documents;

namespace Survoler.Tests;

[TestClass]
public sealed class DocumentOpenCoordinatorTests
{
    [TestMethod]
    [DataRow("report.wps", "sample.doc", OfficeFileKind.Doc)]
    [DataRow("report.wpt", "sample.doc", OfficeFileKind.Doc)]
    [DataRow("report.et", "sample.xls", OfficeFileKind.Xls)]
    [DataRow("report.ett", "sample.xls", OfficeFileKind.Xls)]
    [DataRow("report.dps", "sample.ppt", OfficeFileKind.Ppt)]
    [DataRow("report.dpt", "sample.ppt", OfficeFileKind.Ppt)]
    [DataRow("report.xlt", "sample.xls", OfficeFileKind.Xls)]
    [DataRow("report.xla", "sample.xls", OfficeFileKind.Xls)]
    [DataRow("report.dot", "sample.doc", OfficeFileKind.Doc)]
    [DataRow("report.xlsm", "sample.xlsx", OfficeFileKind.Xlsx)]
    [DataRow("report.xltm", "sample.xlsx", OfficeFileKind.Xlsx)]
    [DataRow("report.xlam", "sample.xlsx", OfficeFileKind.Xlsx)]
    [DataRow("report.dotx", "sample.docx", OfficeFileKind.Docx)]
    [DataRow("report.pptm", "sample.pptx", OfficeFileKind.Pptx)]
    public async Task OpensCompatibleContainerUnderAliasPreservingSourceNameAndKind(
        string sourceName,
        string fixtureName,
        OfficeFileKind expectedKind)
    {
        // These fixtures exercise container validation, not subtype-specific conversion.
        byte[] content = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "TestData", fixtureName));
        using IStorageFile file = FakeStorageFile.Create(sourceName, content);
        using var coordinator = new DocumentOpenCoordinator();

        DocumentSession? session = await coordinator.OpenAsync(file);

        Assert.IsNotNull(session);
        Assert.AreEqual(sourceName, session.SourceName);
        Assert.AreEqual(expectedKind, session.Kind);
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(session.LocalPath));
    }

    [TestMethod]
    [DataRow("report.wps")]
    [DataRow("report.wpt")]
    [DataRow("report.et")]
    [DataRow("report.ett")]
    [DataRow("report.dps")]
    [DataRow("report.dpt")]
    [DataRow("report.xlt")]
    [DataRow("report.xla")]
    [DataRow("report.dot")]
    public async Task RejectsContentWithoutCompleteOleSignatureUnderAlias(string sourceName)
    {
        var invalidContents = new Dictionary<string, byte[]>
        {
            ["non-OLE binary"] = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07],
            ["plain text"] = "This is not an OLE document."u8.ToArray(),
            ["ZIP Office fixture"] = await File.ReadAllBytesAsync(
                Path.Combine(AppContext.BaseDirectory, "TestData", "sample.docx")),
            ["empty ZIP signature"] = [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0],
            ["spanned ZIP signature"] = [0x50, 0x4B, 0x07, 0x08, 0, 0, 0, 0]
        };
        byte[] oleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        for (int length = 0; length < oleSignature.Length; length++)
        {
            invalidContents.Add($"OLE prefix of {length} bytes", oleSignature[..length]);
        }

        using var coordinator = new DocumentOpenCoordinator();
        foreach ((string description, byte[] content) in invalidContents)
        {
            using IStorageFile file = FakeStorageFile.Create(sourceName, content);
            DocumentOpenException exception = await Assert.ThrowsExactlyAsync<DocumentOpenException>(
                () => coordinator.OpenAsync(file), $"{sourceName}: {description}");

            Assert.AreEqual(
                "The file content does not match its extension.",
                exception.Message,
                $"{sourceName}: {description} must fail content validation, not extension recognition.");
        }
    }

    [TestMethod]
    [DataRow("report.xlsm")]
    [DataRow("report.xltm")]
    [DataRow("report.xlam")]
    [DataRow("report.dotx")]
    [DataRow("report.pptm")]
    public async Task RejectsNonZipContentUnderOpenXmlAlias(string sourceName)
    {
        byte[][] invalidContents =
        [
            [],
            [0x50, 0x4B, 0x03],
            "This is not an OpenXML package."u8.ToArray(),
            await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "sample.xls"))
        ];
        using var coordinator = new DocumentOpenCoordinator();
        foreach (byte[] content in invalidContents)
        {
            using IStorageFile file = FakeStorageFile.Create(sourceName, content);
            DocumentOpenException exception = await Assert.ThrowsExactlyAsync<DocumentOpenException>(
                () => coordinator.OpenAsync(file));
            Assert.AreEqual("The file content does not match its extension.", exception.Message);
        }
    }
}
