using OfficeIMO;
using Survoler.Documents;

namespace Survoler.Tests;

[TestClass]
public sealed class PreviewLimitsTests
{
    [TestMethod]
    public void CreatesBoundedPackagePolicy()
    {
        OfficePackageSecurityOptions options = PreviewLimits.CreatePackageSecurity();

        Assert.AreEqual(PreviewLimits.MaxInputBytes, options.MaxPackageBytes);
        Assert.AreEqual(PreviewLimits.MaxPartBytes, options.MaxPartUncompressedBytes);
        Assert.AreEqual(PreviewLimits.MaxTotalUncompressedBytes, options.MaxTotalUncompressedBytes);
        Assert.AreEqual(OfficePackageContentPolicy.Allow, options.Macros);
        Assert.AreEqual(OfficePackageContentPolicy.Allow, options.EmbeddedPayloads);
        Assert.AreEqual(OfficePackageContentPolicy.Allow, options.ActiveX);
        Assert.AreEqual(OfficePackageContentPolicy.Allow, options.ExternalRelationships);
    }
}
