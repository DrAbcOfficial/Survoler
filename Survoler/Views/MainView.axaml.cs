using System;
using System.ComponentModel;
using Avalonia.Controls;
using Survoler.ViewModels;

namespace Survoler.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _viewModel;

    public MainView()
    {
        InitializeComponent();
        PageLayer.RenderTransform = _pageTransform;
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => ClearSelection();
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
        }

        UpdatePageGeometry();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.PreviewImage))
        {
            UpdatePageGeometry();
        }
        else if (args.PropertyName == nameof(MainViewModel.PreviewInteractionMap))
        {
            ClearSelection();
        }
        else if (args.PropertyName == nameof(MainViewModel.IsFitToView) &&
                 !_suppressViewModeUpdate)
        {
            ApplyViewMode();
        }
    }
}
