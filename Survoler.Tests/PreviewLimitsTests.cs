using OfficeIMO;
using Survoler.Documents;

namespace Survoler.Tests;

[TestClass]
public sealed class PreviewLimitsTests
{
    [TestMethod]
    public void CreatesRestrictivePackagePolicy()
    {
        OfficePackageSecurityOptions options = PreviewLimits.CreatePackageSecurity();

        Assert.AreEqual(PreviewLimits.MaxInputBytes, options.MaxPackageBytes);
        Assert.AreEqual(PreviewLimits.MaxPartBytes, options.MaxPartUncompressedBytes);
        Assert.AreEqual(PreviewLimits.MaxTotalUncompressedBytes, options.MaxTotalUncompressedBytes);
        Assert.AreEqual(OfficePackageContentPolicy.Reject, options.Macros);
        Assert.AreEqual(OfficePackageContentPolicy.Reject, options.EmbeddedPayloads);
        Assert.AreEqual(OfficePackageContentPolicy.Reject, options.ActiveX);
        Assert.AreEqual(OfficePackageContentPolicy.Allow, options.ExternalRelationships);
    }
}
