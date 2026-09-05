using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OfficeIMO.Drawing;

namespace Survoler.Rendering;

public interface IOfficePdfRenderingResourcesProvider
{
    OfficePdfRenderingResources GetResources();
}

public sealed class OfficePdfRenderingResources
{
    public OfficePdfRenderingResources(
        OfficeRenderingProfile profile,
        IReadOnlyDictionary<string, string>? fontSubstitutions = null,
        string? defaultFontFamily = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        FontSubstitutions = new ReadOnlyDictionary<string, string>(
            fontSubstitutions is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(fontSubstitutions, StringComparer.OrdinalIgnoreCase));
        DefaultFontFamily = string.IsNullOrWhiteSpace(defaultFontFamily)
            ? null
            : defaultFontFamily.Trim();
    }

    public OfficeRenderingProfile Profile { get; }

    public IReadOnlyDictionary<string, string> FontSubstitutions { get; }

    public string? DefaultFontFamily { get; }
}
