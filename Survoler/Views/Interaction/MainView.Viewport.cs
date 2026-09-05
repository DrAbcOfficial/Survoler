using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Survoler.Resources;

namespace Survoler.Views;

public partial class MainView
{
    private const double PagePadding = 12;

    private readonly MatrixTransform _pageTransform = new();
    private double _pageWidth;
    private double _pageHeight;
    private double _fitScale = 1;
    private double _scale = 1;
    private double _translateX;
    private double _translateY;
    private bool _suppressViewModeUpdate;

    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        if (_viewModel?.IsFitToView == true)
        {
            ApplyFitTransform();
        }
        else
        {
            ClampTranslation();
            ApplyTransform();
        }
    }

    private void UpdatePageGeometry()
    {
        ClearSelection();
        if (_viewModel?.PreviewImage is not { } image)
        {
            _pageWidth = 0;
            _pageHeight = 0;
            PageLayer.Width = 0;
            PageLayer.Height = 0;
            return;
        }

        _pageWidth = image.PixelSize.Width;
        _pageHeight = image.PixelSize.Height;
        PageLayer.Width = _pageWidth;
        PageLayer.Height = _pageHeight;
        PreviewPageImage.Width = _pageWidth;
        PreviewPageImage.Height = _pageHeight;
        SelectionOverlay.Width = _pageWidth;
        SelectionOverlay.Height = _pageHeight;
        ApplyViewMode();
    }

    private void ApplyViewMode()
    {
        if (_pageWidth <= 0 || _pageHeight <= 0 ||
            PreviewViewport.Bounds.Width <= 0 || PreviewViewport.Bounds.Height <= 0)
        {
            return;
        }

        if (_viewModel?.IsFitToView != false)
        {
            ApplyFitTransform();
            return;
        }

        _scale = 1;
        _translateX = (PreviewViewport.Bounds.Width - _pageWidth) / 2;
        _translateY = PagePadding;
        ClampTranslation();
        ApplyTransform();
    }

    private void ApplyFitTransform()
    {
        if (_pageWidth <= 0 || _pageHeight <= 0)
        {
            return;
        }

        double availableWidth = Math.Max(1, PreviewViewport.Bounds.Width - PagePadding * 2);
        double availableHeight = Math.Max(1, PreviewViewport.Bounds.Height - PagePadding * 2);
        _fitScale = Math.Min(availableWidth / _pageWidth, availableHeight / _pageHeight);
        _scale = _fitScale;
        _translateX = (PreviewViewport.Bounds.Width - _pageWidth * _scale) / 2;
        _translateY = (PreviewViewport.Bounds.Height - _pageHeight * _scale) / 2;
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        _pageTransform.Matrix = new Matrix(
            _scale,
            0,
            0,
            _scale,
            _translateX,
            _translateY);
        UpdateSwipeAvailability();
    }

    private void ClampTranslation()
    {
        double viewportWidth = PreviewViewport.Bounds.Width;
        double viewportHeight = PreviewViewport.Bounds.Height;
        double scaledWidth = _pageWidth * _scale;
        double scaledHeight = _pageHeight * _scale;

        _translateX = scaledWidth <= viewportWidth
            ? (viewportWidth - scaledWidth) / 2
            : Math.Clamp(_translateX, viewportWidth - scaledWidth - PagePadding, PagePadding);
        _translateY = scaledHeight <= viewportHeight
            ? (viewportHeight - scaledHeight) / 2
            : Math.Clamp(_translateY, viewportHeight - scaledHeight - PagePadding, PagePadding);
    }

    private void SetCustomViewMode()
    {
        if (_viewModel is null || !_viewModel.IsFitToView)
        {
            return;
        }

        _suppressViewModeUpdate = true;
        _viewModel.IsFitToView = false;
        _viewModel.FitButtonText = Strings.Fit;
        _suppressViewModeUpdate = false;
    }
}
