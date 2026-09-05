using System.Globalization;
using System.Resources;

namespace Survoler.Resources;

internal static class OfdStrings
{
    internal const string DiagnosticMarker = "Survoler.OfdDiagnostic";
    private static readonly ResourceManager Manager = new("Survoler.Resources.OfdStrings", typeof(OfdStrings).Assembly);

    internal static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException($"Missing OFD resource: {key}");

    internal static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(key), args);
}
