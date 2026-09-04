
using System;
using System.ComponentModel;
using Avalonia.Controls;
using Survoler.ViewModels;

namespace Survoler.Views;

public partial class MainView : UserControl
{
    private static readonly Uri PreviewOrigin = new("https://survoler.invalid/");

    private MainViewModel? _viewModel;
    private bool _adapterReady;
    private bool _allowPreviewNavigation;

    public MainView()
    {
        InitializeComponent();

        PreviewWebView.EnvironmentRequested += OnEnvironmentRequested;
        PreviewWebView.AdapterCreated += OnAdapterCreated;
        PreviewWebView.AdapterDestroyed += OnAdapterDestroyed;
        PreviewWebView.NavigationStarted += OnNavigationStarted;
        PreviewWebView.NewWindowRequested += OnNewWindowRequested;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs args)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            NavigateToPreview(_viewModel.PreviewHtml);
        }
    }

    private static void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs args)
    {
        args.EnableDevTools = false;
        App.WebViewPlatformPolicy?.ConfigureEnvironment(args);
    }

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs args)
    {
        App.WebViewPlatformPolicy?.ConfigureAdapter(args);
        _adapterReady = true;
        NavigateToPreview(_viewModel?.PreviewHtml);
    }

    private void OnAdapterDestroyed(object? sender, WebViewAdapterEventArgs args)
    {
        _adapterReady = false;
        _allowPreviewNavigation = false;
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs args)
    {
        bool allowed = _allowPreviewNavigation && args.Request == PreviewOrigin;
        _allowPreviewNavigation = false;
        args.Cancel = !allowed;
    }

    private static void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs args)
    {
        args.Handled = true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.PreviewHtml))
        {
            NavigateToPreview(_viewModel?.PreviewHtml);
        }
    }

    private void NavigateToPreview(string? html)
    {
        if (!_adapterReady || string.IsNullOrWhiteSpace(html))
        {
            return;
        }

        _allowPreviewNavigation = true;
        PreviewWebView.NavigateToString(html, PreviewOrigin);
    }
}
