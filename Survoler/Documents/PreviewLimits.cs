using OfficeIMO;

namespace Survoler.Documents;

public static class PreviewLimits
{
    public const long MaxInputBytes = 64L * 1024 * 1024;
    public const int MaxPackageParts = 10_000;
    public const long MaxPartBytes = 32L * 1024 * 1024;
    public const long MaxTotalUncompressedBytes = 256L * 1024 * 1024;
    public const double MaxCompressionRatio = 200D;
    public const int MaxPdfPages = 2_000;
    public const int MaxPdfPageWidth = 2_048;
    public const long MaxPdfPagePixels = 5_000_000;

    public static OfficePackageSecurityOptions CreatePackageSecurity() => new()
    {
        MaxPackageBytes = MaxInputBytes,
        MaxPartCount = MaxPackageParts,
        MaxPartUncompressedBytes = MaxPartBytes,
        MaxXmlCharactersInPart = MaxPartBytes,
        MaxTotalUncompressedBytes = MaxTotalUncompressedBytes,
        MaxCompressionRatio = MaxCompressionRatio,
        // OfficeIMO inventories active content but never executes it during preview.
        Macros = OfficePackageContentPolicy.Allow,
        EmbeddedPayloads = OfficePackageContentPolicy.Allow,
        ActiveX = OfficePackageContentPolicy.Allow,
        ExternalRelationships = OfficePackageContentPolicy.Allow
    };
}
