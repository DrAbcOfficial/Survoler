using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using OfficeIMO.Pdf;
using Survoler.Documents;
using Survoler.Rendering;
using Survoler.Resources;
using Survoler.ViewModels;

namespace Survoler.Tests;

[TestClass]
public sealed class LocalizationTests
{
    private static readonly ResourceManager Ui = new("Survoler.Resources.Strings", typeof(Strings).Assembly);
    private static readonly ResourceManager Ofd = new("Survoler.Resources.OfdStrings", typeof(Strings).Assembly);

    [TestMethod]
    [DataRow("Survoler.Resources.Strings")]
    [DataRow("Survoler.Resources.OfdStrings")]
    public void ResourceSetsHaveIdenticalNonemptyKeysAndFormatIndices(string baseName)
    {
        var manager = new ResourceManager(baseName, typeof(Strings).Assembly);
        try
        {
            ResourceSet? neutral = manager.GetResourceSet(CultureInfo.InvariantCulture, true, tryParents: false);
            ResourceSet? chinese = manager.GetResourceSet(CultureInfo.GetCultureInfo("zh-Hans"), true, tryParents: false);
            Assert.IsNotNull(neutral);
            Assert.IsNotNull(chinese);
            var english = neutral.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
            var translated = chinese.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
            Assert.IsNotEmpty(english);
            CollectionAssert.AreEquivalent(english.Keys.ToArray(), translated.Keys.ToArray());
            foreach ((string key, string value) in english)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(key), baseName);
                Assert.IsFalse(string.IsNullOrWhiteSpace(value), $"{baseName}: {key} (en)");
                Assert.IsFalse(string.IsNullOrWhiteSpace(translated[key]), $"{baseName}: {key} (zh-Hans)");
                // Ignore escaped braces; compare indices including repetitions, independent of order.
                const string pattern = @"(?<!\{)\{(\d+)(?:\s*,\s*-?\d+)?(?:\:[^{}]*)?\}(?!\})";
                string[] Indices(string text) => Regex.Matches(text, pattern)
                    .Select(m => m.Groups[1].Value).ToArray();
                CollectionAssert.AreEquivalent(Indices(value), Indices(translated[key]), $"{baseName}: {key}");
                _ = CompositeFormat.Parse(value);
                _ = CompositeFormat.Parse(translated[key]);
            }
        }
        finally
        {
            manager.ReleaseAllResources();
        }
    }

    [TestMethod]
    [DataRow("en-US", "")]
    [DataRow("zh-CN", "zh-Hans")]
    [DataRow("zh-SG", "zh-Hans")]
    [DataRow("zh-Hans", "zh-Hans")]
    [DataRow("fr-FR", "")]
    [DataRow("zh-TW", "")]
    public Task CultureFallbackMatchesEveryResource(string culture, string expectedCulture) => WithCulture(culture, () =>
    {
        Assert.AreEqual("en", typeof(Strings).Assembly.GetCustomAttribute<NeutralResourcesLanguageAttribute>()?.CultureName);
        foreach (ResourceManager manager in new[] { Ui, Ofd })
        {
            ResourceSet? expected = manager.GetResourceSet(CultureInfo.GetCultureInfo(expectedCulture), true, false);
            Assert.IsNotNull(expected);
            foreach (DictionaryEntry entry in expected)
            {
                string key = (string)entry.Key;
                Assert.AreEqual(entry.Value, manager.GetString(key, CultureInfo.CurrentUICulture), $"{culture}: {manager.BaseName}.{key}");
                if (manager == Ui) Assert.AreEqual(entry.Value, Strings.Get(key), $"{culture}: Strings.Get({key})");
            }
        }
        return Task.CompletedTask;
    });

    [TestMethod]
    public async Task LookupsFollowScopedCultureAndRestoreExistingContext()
    {
        CultureInfo originalUi = CultureInfo.CurrentUICulture;
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        string previous = Strings.Previous;
        await WithCulture("en-US", async () =>
        {
            Assert.AreEqual(Expected(Ui, "Previous", ""), Strings.Previous);
            await WithCulture("zh-CN", () =>
            {
                Assert.AreEqual(Expected(Ui, "Previous", "zh-Hans"), Strings.Previous);
                Assert.AreNotEqual(Expected(Ui, "Previous", ""), Strings.Previous);
                Assert.AreEqual(Expected(Ui, "Next", "zh-Hans"), Strings.Next);
                return Task.CompletedTask;
            });
            Assert.AreEqual("en-US", CultureInfo.CurrentUICulture.Name);
            Assert.AreEqual(Expected(Ui, "Previous", ""), Strings.Previous);
        });
        Assert.AreSame(originalUi, CultureInfo.CurrentUICulture);
        Assert.AreSame(originalCulture, CultureInfo.CurrentCulture);
        Assert.AreEqual(previous, Strings.Previous);
    }

    [TestMethod]
    public Task TaskRunFlowsUiCultureIndependentlyOfFormattingCulture() => WithCulture("zh-CN", async () =>
    {
        await Task.Run(async () =>
        {
            await Task.Yield();
            Assert.AreEqual("zh-CN", CultureInfo.CurrentUICulture.Name);
            Assert.AreEqual("fr-FR", CultureInfo.CurrentCulture.Name);
            Assert.AreEqual(Expected(Ui, "Previous", "zh-Hans"), Strings.Previous);
            Assert.AreEqual(string.Format(CultureInfo.GetCultureInfo("fr-FR"),
                Expected(Ui, "Page", "zh-Hans"), 1234.5m), Strings.Format("Page", 1234.5m));
            StringAssert.Contains(Strings.Format("Page", 1234.5m), "1234,5");
        });
    });

    [TestMethod]
    public async Task ParallelAsyncCultureContextsDoNotCollide()
    {
        CultureInfo originalUi = CultureInfo.CurrentUICulture;
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string[] cultures = ["en-US", "zh-CN", "fr-FR", "zh-SG", "zh-TW", "zh-Hans"];
        int remaining = cultures.Length;
        await Task.WhenAll(cultures.Select(culture => Task.Run(() => WithCulture(culture, async () =>
        {
            if (Interlocked.Decrement(ref remaining) == 0) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            string expectedCulture = culture is "zh-CN" or "zh-SG" or "zh-Hans" ? "zh-Hans" : "";
            for (int i = 0; i < 20; i++)
            {
                await Task.Yield();
                Assert.AreEqual(culture, CultureInfo.CurrentUICulture.Name);
                Assert.AreEqual("fr-FR", CultureInfo.CurrentCulture.Name);
                Assert.AreEqual(Expected(Ui, "Previous", expectedCulture), Strings.Previous);
                using var file = FakeStorageFile.Create("unsupported.xyz", []);
                using var coordinator = new DocumentOpenCoordinator();
                DocumentOpenException error = await Assert.ThrowsExactlyAsync<DocumentOpenException>(() => coordinator.OpenAsync(file));
                Assert.AreEqual(Expected(Ui, "UnsupportedFileType", expectedCulture), error.Message);
            }
        }))));
        Assert.AreSame(originalUi, CultureInfo.CurrentUICulture);
        Assert.AreSame(originalCulture, CultureInfo.CurrentCulture);
    }

    [TestMethod]
    public Task ViewModelInitialStatusAndFitUseCurrentUiCulture() => WithCulture("en-US", async () =>
    {
        using var english = new MainViewModel(new DocumentActivationService(), new DocumentPreviewService());
        Assert.AreEqual(Expected(Ui, "OpenPrompt", ""), english.StatusText);
        Assert.AreEqual(Expected(Ui, "ActualSize", ""), english.FitButtonText);
        await WithCulture("zh-CN", () =>
        {
            using var chinese = new MainViewModel(new DocumentActivationService(), new DocumentPreviewService());
            Assert.AreEqual(Expected(Ui, "OpenPrompt", "zh-Hans"), chinese.StatusText);
            Assert.AreNotEqual(english.StatusText, chinese.StatusText);
            Assert.AreEqual(Expected(Ui, "ActualSize", "zh-Hans"), chinese.FitButtonText);
            Assert.IsNull(chinese.Session);
            Assert.IsFalse(chinese.IsLoading);
            english.ToggleFitCommand.Execute(null);
            Assert.IsFalse(english.IsFitToView);
            Assert.AreEqual(Expected(Ui, "Fit", "zh-Hans"), english.FitButtonText);
            english.ToggleFitCommand.Execute(null);
            Assert.AreEqual(Expected(Ui, "ActualSize", "zh-Hans"), english.FitButtonText);
            Assert.AreEqual(Expected(Ui, "OpenPrompt", ""), english.StatusText);
            return Task.CompletedTask;
        });
        english.ToggleFitCommand.Execute(null);
        Assert.AreEqual(Expected(Ui, "Fit", ""), english.FitButtonText);
    });

    [TestMethod]
    [DataRow("en-US", "")]
    [DataRow("zh-CN", "zh-Hans")]
    public Task InvalidCsvAndEmptyOfdZipReturnLocalizedErrors(string culture, string expectedCulture) => WithCulture(culture, async () =>
    {
        using var csvFile = FakeStorageFile.Create("Original.CSV", [0xC3, 0x28]);
        using var coordinator = new DocumentOpenCoordinator();
        DocumentSession? csv = await coordinator.OpenAsync(csvFile);
        Assert.IsNotNull(csv);
        Assert.AreEqual("Original.CSV", csv.SourceName);
        DocumentOpenException csvError = await Assert.ThrowsExactlyAsync<DocumentOpenException>(
            () => new OfficePdfConverter().ConvertAsync(csv, CancellationToken.None));
        Assert.AreEqual(Expected(Ui, "CsvEncoding", expectedCulture), csvError.Message);
        CollectionAssert.AreEqual(new byte[] { 0xC3, 0x28 }, await File.ReadAllBytesAsync(csv.LocalPath));

        // A real, valid empty ZIP reaches the bounded OFD package reader, not ZIP corruption handling.
        byte[] zip = CreateZip([]);
        using var ofdFile = FakeStorageFile.Create("Original.OFD", zip);
        DocumentSession? ofd = await coordinator.OpenAsync(ofdFile);
        Assert.IsNotNull(ofd);
        Assert.AreEqual("Original.OFD", ofd.SourceName);
        DocumentOpenException ofdError = await Assert.ThrowsExactlyAsync<DocumentOpenException>(
            () => new OfficePdfConverter().ConvertAsync(ofd, CancellationToken.None));
        Assert.AreEqual(string.Format(CultureInfo.GetCultureInfo(culture),
            Expected(Ofd, "InvalidPrefix", expectedCulture),
            Expected(Ofd, "MissingOfdEntry", expectedCulture)), ofdError.Message);
        CollectionAssert.AreEqual(zip, await File.ReadAllBytesAsync(ofd.LocalPath));
    });

    [TestMethod]
    [DataRow("SkippedSignatures")]
    [DataRow("SkippedAnnotations")]
    [DataRow("SkippedSignaturesAndAnnotations")]
    public void EverySkippedWarningRetainsCompletenessAndSignatureDisclaimer(string key)
    {
        string english = Expected(Ofd, key, "");
        StringAssert.Contains(english, "Partial OFD preview");
        StringAssert.Contains(english, "were skipped");
        StringAssert.Contains(english, "does not verify digital signatures");
        string chinese = Expected(Ofd, key, "zh-Hans");
        StringAssert.Contains(chinese, "\u9884\u89c8\u4e0d\u5b8c\u6574");
        StringAssert.Contains(chinese, "\u5df2\u8df3\u8fc7");
        StringAssert.Contains(chinese, "\u4e0d\u4f1a\u9a8c\u8bc1\u6570\u5b57\u7b7e\u540d");
        if (key != "SkippedAnnotations")
        {
            StringAssert.Contains(english, "seals");
            StringAssert.Contains(english, "signatures");
            StringAssert.Contains(chinese, "\u5370\u7ae0");
        }
        if (key != "SkippedSignatures")
        {
            StringAssert.Contains(english, "annotations");
            StringAssert.Contains(chinese, "\u6279\u6ce8");
        }
    }

    [TestMethod]
    public Task RealOfdConversionReturnsChineseWarningWithoutLocalizingNamesOrBody() => WithCulture("zh-CN", async () =>
    {
        const string ns = "http://www.ofdspec.org/2016";
        byte[] original = CreateZip([
            ("OFD.xml", $"<OFD xmlns='{ns}' Version='1.0' DocType='OFD'><DocBody><DocRoot>Doc/Document.xml</DocRoot><Signatures>MissingSigns.xml</Signatures></DocBody></OFD>"),
            ("Doc/Document.xml", $"<Document xmlns='{ns}'><CommonData><PageArea><PhysicalBox>0 0 100 120</PhysicalBox></PageArea><PublicRes>Res.xml</PublicRes></CommonData><Pages><Page ID='5' BaseLoc='Page.xml'/></Pages><Annotations>MissingAnnotations.xml</Annotations></Document>"),
            ("Doc/Res.xml", $"<Res xmlns='{ns}'><Fonts><Font ID='1' FontName='Missing-Test-Font'/></Fonts></Res>"),
            ("Doc/Page.xml", $"<Page xmlns='{ns}'><Content><Layer ID='6'><TextObject ID='10' Boundary='10 20 80 30' Font='1' Size='4'><TextCode X='2' Y='8'>OriginalBody</TextCode></TextObject></Layer></Content></Page>")
        ]);
        using var file = FakeStorageFile.Create("Original.OFD", original);
        using var coordinator = new DocumentOpenCoordinator();
        DocumentSession? session = await coordinator.OpenAsync(file);
        Assert.IsNotNull(session);
        string sourcePath = session.LocalPath;
        using ConvertedPdfDocument converted = await new OfficePdfConverter().ConvertAsync(session, CancellationToken.None);
        Assert.AreEqual(Expected(Ofd, "SkippedSignaturesAndAnnotations", "zh-Hans"), converted.Warning);
        Assert.AreEqual("Original.OFD", session.SourceName);
        Assert.AreEqual(sourcePath, session.LocalPath);
        Assert.AreNotEqual(sourcePath, converted.Path);
        Assert.AreEqual(".pdf", Path.GetExtension(converted.Path));
        Assert.IsTrue(Guid.TryParseExact(Path.GetFileNameWithoutExtension(converted.Path), "N", out _));
        PdfReadDocument pdf = PdfReadDocument.Open(converted.Path);
        Assert.AreEqual(1, pdf.Pages.Count);
        Assert.AreEqual("OriginalBody", pdf.ExtractText().Trim());
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(sourcePath));
        string outputPath = converted.Path;
        converted.Dispose();
        Assert.IsFalse(File.Exists(outputPath));
        Assert.IsTrue(File.Exists(sourcePath));
    });

    private static string Expected(ResourceManager manager, string key, string culture)
    {
        string? value = manager.GetString(key, CultureInfo.GetCultureInfo(culture));
        Assert.IsNotNull(value, $"{manager.BaseName}.{key}: {culture}");
        return value;
    }

    private static async Task WithCulture(string uiCulture, Func<Task> action)
    {
        CultureInfo previousUi = CultureInfo.CurrentUICulture;
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(uiCulture);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            await action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static byte[] CreateZip((string Name, string Xml)[] parts)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string xml) in parts)
            {
                using Stream stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.ASCII.GetBytes(xml));
            }
        }
        return output.ToArray();
    }
}
