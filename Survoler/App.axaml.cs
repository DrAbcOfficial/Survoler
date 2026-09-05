using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Survoler.Documents;
using Survoler.Rendering;
using Survoler.ViewModels;
using Survoler.Views;

namespace Survoler;

public partial class App : Application
{
    public static DocumentActivationService Activations { get; } = new();

    public static DocumentPreviewService Previews { get; } = new(
        new WordPreviewRenderer(),
        new SpreadsheetPreviewRenderer(),
        new PresentationPreviewRenderer());

    public static IWebViewPlatformPolicy? WebViewPlatformPolicy { get; set; }

    public static IPdfPageRendererFactory? PdfPageRendererFactory { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            activityLifetime.MainViewFactory = () => new MainView
            {
                DataContext = new MainViewModel(Activations, Previews)
            };
        }

        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatableLifetime)
        {
            activatableLifetime.Activated += OnActivated;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnActivated(object? sender, ActivatedEventArgs args)
    {
        if (args is not FileActivatedEventArgs fileArgs)
        {
            return;
        }

        foreach (IStorageItem item in fileArgs.Files)
        {
            if (item is IStorageFile file)
            {
                Activations.Publish(file);
                return;
            }
        }
    }
}
