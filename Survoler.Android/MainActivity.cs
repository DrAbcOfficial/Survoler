using Android.App;
using Android.Content;
using Android.Content.PM;
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
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    })]
public class MainActivity : AvaloniaMainActivity
{
}
