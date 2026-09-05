using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using ActionMode = Android.Views.ActionMode;

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
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
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
public class MainActivity : AvaloniaMainActivity, ITextSelectionMenu
{
    private ActionMode? _selectionMode;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        App.TextSelectionMenu = this;
    }

    public void Show(string text, PixelRect screenBounds, System.Action onDismissed)
    {
        Hide();
        _selectionMode = Window?.DecorView?.StartActionMode(
            new SelectionCallback(this, text, screenBounds, onDismissed), ActionModeType.Floating);
    }

    public void Hide()
    {
        ActionMode? mode = _selectionMode;
        _selectionMode = null;
        mode?.Finish();
    }

    protected override void OnPause()
    {
        Hide();
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        Hide();
        if (ReferenceEquals(App.TextSelectionMenu, this))
        {
            App.TextSelectionMenu = null;
        }
        base.OnDestroy();
    }

    private sealed class SelectionCallback(
        MainActivity activity, string text, PixelRect screenBounds, System.Action onDismissed)
        : ActionMode.Callback2
    {
        public override bool OnCreateActionMode(ActionMode? mode, IMenu? menu)
        {
            menu?.Add(0, global::Android.Resource.Id.Copy, 0, global::Android.Resource.String.Copy)
                ?.SetShowAsAction(ShowAsAction.Always);
            return true;
        }

        public override bool OnPrepareActionMode(ActionMode? mode, IMenu? menu) => false;

        public override bool OnActionItemClicked(ActionMode? mode, IMenuItem? item)
        {
            if (item?.ItemId != global::Android.Resource.Id.Copy)
            {
                return false;
            }
            if (activity.GetSystemService(ClipboardService) is ClipboardManager clipboard)
            {
                clipboard.PrimaryClip = ClipData.NewPlainText(null, text);
            }
            mode?.Finish();
            return true;
        }

        public override void OnDestroyActionMode(ActionMode? mode)
        {
            activity._selectionMode = null;
            onDismissed();
        }

        public override void OnGetContentRect(ActionMode? mode, View? view,
            global::Android.Graphics.Rect? outRect)
        {
            int[] location = new int[2];
            view?.GetLocationOnScreen(location);
            outRect?.Set(screenBounds.X - location[0], screenBounds.Y - location[1],
                screenBounds.Right - location[0], screenBounds.Bottom - location[1]);
        }
    }
}
