using Avalonia.Controls;

namespace Survoler.Rendering;

public interface IWebViewPlatformPolicy
{
    void ConfigureEnvironment(WebViewEnvironmentRequestedEventArgs args);

    void ConfigureAdapter(WebViewAdapterEventArgs args);
}
