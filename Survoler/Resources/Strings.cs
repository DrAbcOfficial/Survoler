using System.Globalization;
using System.Resources;

namespace Survoler.Resources;

public static class Strings
{
    private static readonly ResourceManager Manager = new("Survoler.Resources.Strings", typeof(Strings).Assembly);

    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException($"Missing UI resource: {key}");

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static string Previous => Get(nameof(Previous));
    public static string Next => Get(nameof(Next));
    public static string Fit => Get(nameof(Fit));
    public static string ActualSize => Get(nameof(ActualSize));
}
