using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
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
    private IDocumentPreview? _preview;

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

    public void Dispose()
    {
        _activationService.FileActivated -= OnFileActivated;
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _previewCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        Interlocked.Exchange(ref _preview, null)?.Dispose();
        _openCoordinator.Dispose();
    }

    partial void OnPreviewHtmlChanged(string? value)
    {
        HasPreview = !string.IsNullOrWhiteSpace(value);
        IsStatusVisible = !HasPreview;
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
        Interlocked.Exchange(ref _preview, null)?.Dispose();

        IsLoading = true;
        PreviewHtml = null;
        FileName = file.Name;
        StatusText = "Copying document...";
        LoadProgress = 0;

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
            if (openVersion == Volatile.Read(ref _openVersion))
            {
                IsLoading = false;
            }

            if (!ReferenceEquals(_previewCancellation, previewCancellation))
            {
                previewCancellation.Dispose();
            }
        }
    }
}
