using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OfficeIMO.Drawing;
using Survoler.Rendering;

namespace Survoler.Android;

public sealed class AndroidOfficePdfRenderingResourcesProvider : IOfficePdfRenderingResourcesProvider
{
    private const string SansFamily = "Android Sans";
    private const string SerifFamily = "Android Serif";
    private const string MonoFamily = "Android Mono";
    private const string CjkFamily = "Android CJK";

    private static readonly string[] FontDirectories =
    {
        "/system/fonts",
        "/product/fonts",
        "/system/product/fonts",
        "/vendor/fonts"
    };

    private readonly Lazy<OfficePdfRenderingResources> _resources = new(CreateResources);

    public OfficePdfRenderingResources GetResources() => _resources.Value;

    private static OfficePdfRenderingResources CreateResources()
    {
        var fonts = new OfficeFontFaceCollection();
        var substitutions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (TryAddFamily(
                fonts,
                SansFamily,
                (OfficeFontStyle.Regular, new[] { "Roboto-Regular.ttf", "NotoSans-Regular.ttf" }),
                (OfficeFontStyle.Bold, new[] { "Roboto-Bold.ttf", "NotoSans-Bold.ttf" }),
                (OfficeFontStyle.Italic, new[] { "Roboto-Italic.ttf", "NotoSans-Italic.ttf" }),
                (OfficeFontStyle.Bold | OfficeFontStyle.Italic,
                    new[] { "Roboto-BoldItalic.ttf", "NotoSans-BoldItalic.ttf" })))
        {
            fonts.AddFallbackFamily(SansFamily);
            AddSubstitutions(substitutions, SansFamily,
                "Aptos", "Arial", "Calibri", "Helvetica", "Segoe UI", "Tahoma",
                "Trebuchet MS", "Verdana");
        }

        if (TryAddFamily(
                fonts,
                CjkFamily,
                (OfficeFontStyle.Regular,
                    new[]
                    {
                        "SysSans-Hans-Regular.ttf", "OSans-RC-Regular.ttf",
                        "NotoSansCJK-Regular.ttc", "NotoSansCJK-VF.ttf",
                        "NotoSansSC-Regular.otf", "DroidSansFallback.ttf"
                    })))
        {
            fonts.AddFallbackFamily(CjkFamily);
            AddSubstitutions(substitutions, CjkFamily,
                "Microsoft YaHei", "SimHei", "SimSun", "DengXian", "FangSong", "KaiTi",
                "\u5fae\u8f6f\u96c5\u9ed1", "\u9ed1\u4f53", "\u5b8b\u4f53", "\u7b49\u7ebf",
                "\u4eff\u5b8b", "\u6977\u4f53");
        }

        if (TryAddFamily(
                fonts,
                SerifFamily,
                (OfficeFontStyle.Regular, new[] { "NotoSerif-Regular.ttf", "DroidSerif-Regular.ttf" }),
                (OfficeFontStyle.Bold, new[] { "NotoSerif-Bold.ttf", "DroidSerif-Bold.ttf" }),
                (OfficeFontStyle.Italic, new[] { "NotoSerif-Italic.ttf", "DroidSerif-Italic.ttf" }),
                (OfficeFontStyle.Bold | OfficeFontStyle.Italic,
                    new[] { "NotoSerif-BoldItalic.ttf", "DroidSerif-BoldItalic.ttf" })))
        {
            fonts.AddFallbackFamily(SerifFamily);
            AddSubstitutions(substitutions, SerifFamily,
                "Cambria", "Georgia", "Times New Roman");
        }

        if (TryAddFamily(
                fonts,
                MonoFamily,
                (OfficeFontStyle.Regular, new[] { "RobotoMono-Regular.ttf", "DroidSansMono.ttf" }),
                (OfficeFontStyle.Bold, new[] { "RobotoMono-Bold.ttf" })))
        {
            fonts.AddFallbackFamily(MonoFamily);
            AddSubstitutions(substitutions, MonoFamily, "Consolas", "Courier New");
        }

        var profile = new OfficeRenderingProfile(
            "survoler-android-system",
            fonts,
            textShapingProvider: OfficeManagedTextShapingProvider.Instance);
        return new OfficePdfRenderingResources(profile, substitutions, SansFamily);
    }

    private static bool TryAddFamily(
        OfficeFontFaceCollection fonts,
        string familyName,
        params (OfficeFontStyle Style, string[] FileNames)[] faces)
    {
        bool addedRegular = false;
        foreach ((OfficeFontStyle style, string[] fileNames) in faces)
        {
            foreach (string path in EnumerateFonts(fileNames))
            {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (!fonts.TryAdd(familyName, data, style) &&
                    (!AndroidTrueTypeCollectionExtractor.TryExtractPreferredFace(
                        path,
                        out byte[]? extracted) ||
                     !fonts.TryAdd(familyName, extracted, style)))
                {
                    continue;
                }

                    if (style == OfficeFontStyle.Regular)
                    {
                        addedRegular = true;
                    }
                    break;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return addedRegular;
    }

    private static IEnumerable<string> EnumerateFonts(IReadOnlyList<string> fileNames)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in FontDirectories)
        {
            foreach (string fileName in fileNames)
            {
                string path = Path.Combine(directory, fileName);
                if (File.Exists(path) && yielded.Add(path))
                {
                    yield return path;
                }
            }
        }

        string[] prefixes = fileNames
            .Select(fileName => Path.GetFileNameWithoutExtension(fileName))
            .ToArray();
        foreach (string directory in FontDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            string[] matches;
            try
            {
                matches = Directory.EnumerateFiles(directory)
                    .Where(path => prefixes.Any(prefix =>
                        Path.GetFileNameWithoutExtension(path)
                            .StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string match in matches)
            {
                if (yielded.Add(match))
                {
                    yield return match;
                }
            }
        }

    }

    private static void AddSubstitutions(
        IDictionary<string, string> substitutions,
        string targetFamily,
        params string[] sourceFamilies)
    {
        foreach (string sourceFamily in sourceFamilies)
        {
            substitutions[sourceFamily] = targetFamily;
        }
    }
}
