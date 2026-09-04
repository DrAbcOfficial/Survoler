using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Survoler.Documents;
using Survoler.Rendering;

namespace Survoler.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly DocumentActivationService _activationService;
    private readonly DocumentOpenCoordinator _openCoordinator = new();
    private readonly DocumentPreviewService _previewService;
    private int _openVersion;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _navigationCancellation;
    private IDocumentPreview? _preview;
    private bool _settingNavigationIndex;

    public MainViewModel(
        DocumentActivationService activationService,
        DocumentPreviewService previewService)
    {
        _activationService = activationService;
        _previewService = previewService;
        _activationService.FileActivated += OnFileActivated;

        if (_activationService.TryTakePending(out IStorageFile? pendingFile))
        {
            _ = OpenFileAsync(pendingFile);
        }
    }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Open an Office file to preview it.";

    [ObservableProperty]
    public partial string FileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double LoadProgress { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsLegacy { get; set; }

    [ObservableProperty]
    public partial DocumentSession? Session { get; set; }

    [ObservableProperty]
    public partial string? PreviewHtml { get; set; }

    [ObservableProperty]
    public partial bool HasPreview { get; set; }

    [ObservableProperty]
    public partial bool IsStatusVisible { get; set; } = true;

    [ObservableProperty]
    public partial IReadOnlyList<string> NavigationItems { get; set; } = Array.Empty<string>();

    [ObservableProperty]
    public partial int SelectedNavigationIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string NavigationPosition { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasNavigation { get; set; }

    [ObservableProperty]
    public partial bool CanNavigatePrevious { get; set; }

    [ObservableProperty]
    public partial bool CanNavigateNext { get; set; }

    public void Dispose()
    {
        _activationService.FileActivated -= OnFileActivated;
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _previewCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        CancellationTokenSource? navigationCancellation = Interlocked.Exchange(
            ref _navigationCancellation,
            null);
        navigationCancellation?.Cancel();
        IDocumentPreview? preview = Interlocked.Exchange(ref _preview, null);
        if (navigationCancellation is null)
        {
            preview?.Dispose();
        }
        _openCoordinator.Dispose();
    }

    partial void OnPreviewHtmlChanged(string? value)
    {
        HasPreview = !string.IsNullOrWhiteSpace(value);
        IsStatusVisible = !HasPreview;
    }

    partial void OnSelectedNavigationIndexChanged(int value)
    {
        if (!_settingNavigationIndex && _preview is not null && value >= 0)
        {
            _ = SelectNavigationItemAsync(value);
        }
    }

    [RelayCommand]
    private Task NavigatePreviousAsync() =>
        SelectNavigationItemAsync(SelectedNavigationIndex - 1);

    [RelayCommand]
    private Task NavigateNextAsync() =>
        SelectNavigationItemAsync(SelectedNavigationIndex + 1);

    private async void OnFileActivated(object? sender, IStorageFile file)
    {
        await OpenFileAsync(file);
    }

    private async Task OpenFileAsync(IStorageFile? file)
    {
        if (file is null)
        {
            return;
        }

        int openVersion = Interlocked.Increment(ref _openVersion);
        var previewCancellation = new CancellationTokenSource();
        CancellationTokenSource? previousCancellation = Interlocked.Exchange(
            ref _previewCancellation,
            previewCancellation);
        previousCancellation?.Cancel();
        CancellationTokenSource? previousNavigation = Interlocked.Exchange(
            ref _navigationCancellation,
            null);
        previousNavigation?.Cancel();
        IDocumentPreview? previousPreview = Interlocked.Exchange(ref _preview, null);
        if (previousNavigation is null)
        {
            previousPreview?.Dispose();
        }

        IsLoading = true;
        PreviewHtml = null;
        FileName = file.Name;
        StatusText = "Copying document...";
        LoadProgress = 0;
        SetNavigation(Array.Empty<string>(), -1);

        try
        {
            var progress = new Progress<DocumentLoadProgress>(value =>
            {
                if (openVersion == Volatile.Read(ref _openVersion))
                {
                    LoadProgress = value.Fraction ?? 0;
                }
            });

            DocumentSession? session = await _openCoordinator.OpenAsync(file, progress);
            if (session is null || openVersion != Volatile.Read(ref _openVersion))
            {
                return;
            }

            Session = session;
            IsLegacy = session.IsLegacy;
            StatusText = "Rendering document...";

            IDocumentPreview preview = await _previewService.CreateAsync(
                session,
                previewCancellation.Token);

            if (openVersion != Volatile.Read(ref _openVersion) ||
                !ReferenceEquals(_previewCancellation, previewCancellation))
            {
                preview.Dispose();
                return;
            }

            _preview = preview;
            PreviewHtml = preview.Html;
            SetNavigation(preview.NavigationItems, preview.SelectedIndex);
            LoadProgress = 1;
            StatusText = string.Empty;
        }
        catch (OperationCanceledException) when (previewCancellation.IsCancellationRequested)
        {
        }
        catch (DocumentOpenException exception)
        {
            if (openVersion != Volatile.Read(ref _openVersion))
            {
                return;
            }

            Session = null;
            StatusText = exception.Message;
        }
        catch (Exception)
        {
            if (openVersion != Volatile.Read(ref _openVersion))
            {
                return;
            }

            Session = null;
            StatusText = "The document could not be opened.";
        }
        finally
        {
            bool isCurrentRequest = ReferenceEquals(_previewCancellation, previewCancellation);
            if (isCurrentRequest)
            {
                Interlocked.CompareExchange(ref _previewCancellation, null, previewCancellation);
                IsLoading = false;
            }

            previewCancellation.Dispose();
        }
    }

    private async Task SelectNavigationItemAsync(int index)
    {
        IDocumentPreview? preview = _preview;
        if (preview is null || (uint)index >= (uint)preview.NavigationItems.Count)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previousCancellation = Interlocked.Exchange(
            ref _navigationCancellation,
            cancellation);
        previousCancellation?.Cancel();
        IDocumentPreview expectedPreview = preview;

        try
        {
            IsLoading = true;
            string html = await preview.SelectAsync(index, cancellation.Token);

            if (!ReferenceEquals(_preview, expectedPreview) ||
                !ReferenceEquals(_navigationCancellation, cancellation))
            {
                return;
            }

            PreviewHtml = html;
            SetNavigation(preview.NavigationItems, preview.SelectedIndex);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (ReferenceEquals(_preview, expectedPreview))
            {
                StatusText = "This section could not be rendered.";
            }
        }
        finally
        {
            bool isCurrentNavigation = ReferenceEquals(_navigationCancellation, cancellation);
            if (isCurrentNavigation)
            {
                Interlocked.CompareExchange(ref _navigationCancellation, null, cancellation);
                IsLoading = false;
            }

            cancellation.Dispose();

            if (!ReferenceEquals(_preview, expectedPreview))
            {
                expectedPreview.Dispose();
            }
        }
    }

    private void SetNavigation(IReadOnlyList<string> items, int selectedIndex)
    {
        NavigationItems = items;
        HasNavigation = items.Count > 1;

        _settingNavigationIndex = true;
        SelectedNavigationIndex = selectedIndex;
        _settingNavigationIndex = false;

        NavigationPosition = selectedIndex >= 0 && items.Count > 0
            ? $"{selectedIndex + 1} / {items.Count}"
            : string.Empty;
        CanNavigatePrevious = selectedIndex > 0;
        CanNavigateNext = selectedIndex >= 0 && selectedIndex < items.Count - 1;
    }
}
