using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfficeIMO.Pdf;
using Survoler.Documents;
using Survoler.Rendering;
using Survoler.Resources;

namespace Survoler.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly DocumentActivationService _activationService;
    private readonly DocumentOpenCoordinator _openCoordinator = new();
    private readonly DocumentPreviewService _previewService;
    private int _openVersion;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _navigationCancellation;
    private CancellationTokenSource? _interactionCancellation;
    private CancellationTokenSource? _warningCancellation;
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
    public partial string StatusText { get; set; } = Strings.Get("OpenPrompt");

    [ObservableProperty]
    public partial string FileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double LoadProgress { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial DocumentSession? Session { get; set; }

    [ObservableProperty]
    public partial Bitmap? PreviewImage { get; set; }

    [ObservableProperty]
    public partial bool HasPreview { get; set; }

    [ObservableProperty]
    public partial PdfPageInteractionMap? PreviewInteractionMap { get; set; }

    [ObservableProperty]
    public partial string? PreviewWarning { get; set; }

    [ObservableProperty]
    public partial bool HasPreviewWarning { get; set; }

    [ObservableProperty]
    public partial double WarningBannerMaxHeight { get; set; }

    [ObservableProperty]
    public partial double WarningBannerOpacity { get; set; }

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

    [ObservableProperty]
    public partial bool IsFitToView { get; set; } = true;

    [ObservableProperty]
    public partial string FitButtonText { get; set; } = Strings.ActualSize;

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
        CancellationTokenSource? interactionCancellation = Interlocked.Exchange(
            ref _interactionCancellation,
            null);
        interactionCancellation?.Cancel();
        interactionCancellation?.Dispose();
        CancellationTokenSource? warningCancellation = Interlocked.Exchange(
            ref _warningCancellation,
            null);
        warningCancellation?.Cancel();
        warningCancellation?.Dispose();
        IDocumentPreview? preview = Interlocked.Exchange(ref _preview, null);
        if (navigationCancellation is null)
        {
            preview?.Dispose();
        }
        _openCoordinator.Dispose();
    }

    partial void OnPreviewImageChanged(Bitmap? value)
    {
        HasPreview = value is not null;
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

    [RelayCommand]
    private void ToggleFit()
    {
        IsFitToView = !IsFitToView;
        FitButtonText = IsFitToView ? Strings.ActualSize : Strings.Fit;
    }

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
        CancellationTokenSource? previousInteraction = Interlocked.Exchange(
            ref _interactionCancellation,
            null);
        previousInteraction?.Cancel();
        IDocumentPreview? previousPreview = Interlocked.Exchange(ref _preview, null);
        PreviewImage = null;
        PreviewInteractionMap = null;
        ShowPreviewWarning(null);
        if (previousNavigation is null)
        {
            previousPreview?.Dispose();
        }

        IsLoading = true;
        FileName = file.Name;
        StatusText = Strings.Get("Loading");
        LoadProgress = 0;
        IsFitToView = true;
        FitButtonText = Strings.ActualSize;
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
            PreviewImage = preview.PageImage;
            ShowPreviewWarning(preview.Warning);
            SetNavigation(preview.NavigationItems, preview.SelectedIndex);
            BeginLoadInteractionMap(preview, preview.SelectedIndex);
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
            StatusText = Strings.Get("OpenFailed");
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
        CancellationTokenSource? previousInteraction = Interlocked.Exchange(
            ref _interactionCancellation,
            null);
        previousInteraction?.Cancel();
        PreviewInteractionMap = null;
        IDocumentPreview expectedPreview = preview;

        try
        {
            IsLoading = true;
            Bitmap image = await preview.SelectAsync(index, cancellation.Token);

            if (!ReferenceEquals(_preview, expectedPreview) ||
                !ReferenceEquals(_navigationCancellation, cancellation))
            {
                return;
            }

            PreviewImage = image;
            SetNavigation(preview.NavigationItems, preview.SelectedIndex);
            ShowPreviewWarning(preview.Warning);
            BeginLoadInteractionMap(preview, preview.SelectedIndex);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (ReferenceEquals(_preview, expectedPreview))
            {
                ShowPreviewWarning(Strings.Get("PageRenderFailed"));
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
            ? Strings.Format("NavigationPosition", selectedIndex + 1, items.Count)
            : string.Empty;
        CanNavigatePrevious = selectedIndex > 0;
        CanNavigateNext = selectedIndex >= 0 && selectedIndex < items.Count - 1;
    }

    private void BeginLoadInteractionMap(IDocumentPreview preview, int index)
    {
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _interactionCancellation,
            cancellation);
        previous?.Cancel();
        _ = LoadInteractionMapAsync(preview, index, cancellation);
    }

    private async Task LoadInteractionMapAsync(
        IDocumentPreview preview,
        int index,
        CancellationTokenSource cancellation)
    {
        try
        {
            PdfPageInteractionMap? map = await preview.GetInteractionMapAsync(
                index,
                cancellation.Token);
            if (ReferenceEquals(_preview, preview) &&
                ReferenceEquals(_interactionCancellation, cancellation) &&
                preview.SelectedIndex == index)
            {
                PreviewInteractionMap = map;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _interactionCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private void ShowPreviewWarning(string? message)
    {
        CancellationTokenSource? previous = Interlocked.Exchange(ref _warningCancellation, null);
        previous?.Cancel();

        string? documentWarning = _preview?.Warning;
        if (!string.IsNullOrWhiteSpace(documentWarning) && message != documentWarning)
        {
            message = string.IsNullOrWhiteSpace(message) ? documentWarning : documentWarning + "\n" + message;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            PreviewWarning = null;
            HasPreviewWarning = false;
            WarningBannerMaxHeight = 0;
            WarningBannerOpacity = 0;
            return;
        }

        PreviewWarning = message;
        HasPreviewWarning = true;
        WarningBannerMaxHeight = 160;
        WarningBannerOpacity = 1;

        // Incomplete-document notices must remain visible for the lifetime of the preview.
        if (!string.IsNullOrWhiteSpace(documentWarning)) return;

        var cancellation = new CancellationTokenSource();
        _warningCancellation = cancellation;
        _ = HidePreviewWarningAsync(cancellation);
    }

    private async Task HidePreviewWarningAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token);
            WarningBannerMaxHeight = 0;
            WarningBannerOpacity = 0;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation.Token);

            if (ReferenceEquals(_warningCancellation, cancellation))
            {
                PreviewWarning = null;
                HasPreviewWarning = false;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _warningCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }
}
