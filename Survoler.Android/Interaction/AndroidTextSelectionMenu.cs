using Android.App;
using Android.Content;
using Android.Views;
using Avalonia;
using ActionMode = Android.Views.ActionMode;

namespace Survoler.Android;

internal sealed class AndroidTextSelectionMenu(Activity activity) : ITextSelectionMenu
{
    private ActionMode? _selectionMode;

    public void Show(string text, PixelRect screenBounds, System.Action onDismissed)
    {
        Hide();
        _selectionMode = activity.Window?.DecorView?.StartActionMode(
            new SelectionCallback(this, activity, text, screenBounds, onDismissed), ActionModeType.Floating);
    }

    public void Hide()
    {
        ActionMode? mode = _selectionMode;
        _selectionMode = null;
        mode?.Finish();
    }

    private sealed class SelectionCallback(
        AndroidTextSelectionMenu owner, Activity activity, string text, PixelRect screenBounds,
        System.Action onDismissed)
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
            if (activity.GetSystemService(Context.ClipboardService) is ClipboardManager clipboard)
            {
                clipboard.PrimaryClip = ClipData.NewPlainText(null, text);
            }
            mode?.Finish();
            return true;
        }

        public override void OnDestroyActionMode(ActionMode? mode)
        {
            owner._selectionMode = null;
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
