using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Survoler.Documents;

namespace Survoler.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly DocumentActivationService _activationService;
    private readonly DocumentOpenCoordinator _openCoordinator = new();
    private int _openVersion;

    public MainViewModel(DocumentActivationService activationService)
    {
        _activationService = activationService;
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

    public void Dispose()
    {
        _activationService.FileActivated -= OnFileActivated;
        _openCoordinator.Dispose();
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
        IsLoading = true;
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
            LoadProgress = 1;
            StatusText = session.IsLegacy
                ? "Compatibility mode. Some content may be omitted."
                : "Document is ready for rendering.";
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
        }
    }
}
