using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

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
            App.PdfPageRendererFactory = new AndroidPdfPageRendererFactory();
            App.OfficePdfRenderingResourcesProvider =
                new AndroidOfficePdfRenderingResourcesProvider();
            return base.CustomizeAppBuilder(builder);
        }
    }
}
