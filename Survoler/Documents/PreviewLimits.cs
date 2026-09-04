using OfficeIMO;

namespace Survoler.Documents;

public static class PreviewLimits
{
    public const long MaxInputBytes = 64L * 1024 * 1024;
    public const int MaxPackageParts = 10_000;
    public const long MaxPartBytes = 32L * 1024 * 1024;
    public const long MaxTotalUncompressedBytes = 256L * 1024 * 1024;
    public const double MaxCompressionRatio = 200D;
    public const int MaxImageBytes = 8 * 1024 * 1024;
    public const long MaxTotalImageBytes = 32L * 1024 * 1024;
    public const int MaxWordHtmlCharacters = 32_000_000;
    public const int MaxSpreadsheetRows = 5_000;
    public const int MaxSpreadsheetColumns = 256;
    public const int MaxSpreadsheetCells = 250_000;
    public const int MaxSpreadsheetMergedRanges = 5_000;
    public const int MaxSlideWidth = 1_920;
    public const int MaxSlideHeight = 1_080;

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
