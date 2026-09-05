using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using System.Globalization;
using Avalonia.Android;

namespace Survoler.Android;

[Activity(
    Label = "Survoler",
    Theme = "@style/MyTheme.NoActionBar",
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ExcludeFromRecents = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault },
    DataSchemes = new[] { "content" },
    DataMimeTypes = new[]
    {
        "application/msword",
        "application/pdf",
        "application/ofd",
        "application/vnd.ofd",
        "text/csv",
        "text/comma-separated-values",
        "application/csv",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.template",
        "application/vnd.ms-excel.sheet.macroenabled.12",
        "application/vnd.ms-excel.template.macroenabled.12",
        "application/vnd.ms-excel.addin.macroenabled.12",
        "application/vnd.ms-powerpoint.presentation.macroenabled.12",
        "application/vnd.ms-excel.sheet.macroEnabled.12",
        "application/vnd.ms-excel.template.macroEnabled.12",
        "application/vnd.ms-excel.addin.macroEnabled.12",
        "application/vnd.ms-powerpoint.presentation.macroEnabled.12",
        "application/wps-office.wps",
        "application/wps-office.wpt",
        "application/wps-office.et",
        "application/wps-office.ett",
        "application/wps-office.dps",
        "application/wps-office.dpt",
        "application/vnd.ms-works",
        "application/x-wps",
        "application/x-wpt",
        "application/x-et",
        "application/x-ett",
        "application/x-dps",
        "application/x-dpt"
    })]
public class MainActivity : AvaloniaMainActivity
{
    private AndroidTextSelectionMenu? _textSelectionMenu;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        CultureInfo language;
        try
        {
            language = CultureInfo.GetCultureInfo(Java.Util.Locale.Default?.ToLanguageTag() ?? "en");
        }
        catch (CultureNotFoundException)
        {
            language = CultureInfo.GetCultureInfo("en");
        }
        // Set only the UI language; document number/date parsing must not change.
        CultureInfo.DefaultThreadCurrentUICulture = language;
        CultureInfo.CurrentUICulture = language;
        base.OnCreate(savedInstanceState);
        _textSelectionMenu = new AndroidTextSelectionMenu(this);
        App.TextSelectionMenu = _textSelectionMenu;
    }

    protected override void OnPause()
    {
        _textSelectionMenu?.Hide();
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        _textSelectionMenu?.Hide();
        if (ReferenceEquals(App.TextSelectionMenu, _textSelectionMenu))
        {
            App.TextSelectionMenu = null;
        }
        base.OnDestroy();
    }
}
