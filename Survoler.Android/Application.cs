using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Survoler.Rendering;

namespace Survoler.Android
{
    [Application]
    public class Application : AvaloniaAndroidApplication<App>
    {
        protected Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            App.WebViewPlatformPolicy = new AndroidWebViewPlatformPolicy();
            App.PdfPageRendererFactory = new AndroidPdfPageRendererFactory();
            return base.CustomizeAppBuilder(builder);
        }
    }
}
