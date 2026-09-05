using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using OfficeIMO.Pdf;
using Survoler.ViewModels;
using Survoler.Resources;

namespace Survoler.Views;

public partial class MainView : UserControl
{
    private const double PagePadding = 12;
    private const double MaximumScale = 6;
    private const double PanStartDistance = 8;

    private static readonly SolidColorBrush SelectionBrush = new(
        Color.FromArgb(96, 27, 135, 255));

    private readonly Dictionary<int, IPointer> _contacts = new();
    private readonly Dictionary<int, Vector> _swipeTotals = new();
    private readonly MatrixTransform _pageTransform = new();
    private MainViewModel? _viewModel;
    private IPointer? _activePointer;
    private IPointer? _selectionPointer;
    private Point _pressPosition;
    private Point _lastPointerPosition;
    private double _pageWidth;
    private double _pageHeight;
    private double _fitScale = 1;
    private double _scale = 1;
    private double _translateX;
    private double _translateY;
    private double _pinchStartScale;
    private double _pinchStartTranslateX;
    private double _pinchStartTranslateY;
    private Point _pinchPageOrigin;
    private int _selectionStartIndex = -1;
    private int _selectionEndIndex = -1;
    private string _selectedText = string.Empty;
    private bool _pinching;
    private bool _panning;
    private bool _selecting;
    private bool _suppressViewModeUpdate;

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

    private void OnPinch(object? sender, PinchEventArgs args)
    {
        if (_pageWidth <= 0 || _pageHeight <= 0)
        {
            return;
        }

        if (!_pinching)
        {
            _pinching = true;
            CancelPan();
            ClearSelection();
            _pinchStartScale = _scale;
            _pinchStartTranslateX = _translateX;
            _pinchStartTranslateY = _translateY;
            _pinchPageOrigin = new Point(
                (args.ScaleOrigin.X - _pinchStartTranslateX) / _pinchStartScale,
                (args.ScaleOrigin.Y - _pinchStartTranslateY) / _pinchStartScale);
        }

        double minimumScale = Math.Max(0.1, Math.Min(_fitScale, 1) * 0.75);
        _scale = Math.Clamp(_pinchStartScale * args.Scale, minimumScale, MaximumScale);
        _translateX = args.ScaleOrigin.X - _pinchPageOrigin.X * _scale;
        _translateY = args.ScaleOrigin.Y - _pinchPageOrigin.Y * _scale;
        SetCustomViewMode();
        ClampTranslation();
        ApplyTransform();
        args.Handled = true;
    }

    private void OnPinchEnded(object? sender, PinchEndedEventArgs args)
    {
        if (!_pinching)
        {
            return;
        }

        _pinching = false;
        ClampTranslation();
        ApplyTransform();
        args.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        Point position = args.GetPosition(PreviewViewport);
        _contacts[args.Pointer.Id] = args.Pointer;
        if (_contacts.Count > 1)
        {
            CancelPan();
            ClearSelection();
            return;
        }

        _activePointer = args.Pointer;
        _pressPosition = position;
        _lastPointerPosition = position;
        if (!_selectedText.Equals(string.Empty, StringComparison.Ordinal))
        {
            ClearSelection();
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs args)
    {
        Point position = args.GetPosition(PreviewViewport);
        if (args.Pointer == _selectionPointer && _selecting)
        {
            UpdateSelection(position);
            args.PreventGestureRecognition();
            args.Handled = true;
            return;
        }

        if (args.Pointer != _activePointer || _pinching || _contacts.Count != 1)
        {
            return;
        }

        Vector fromPress = position - _pressPosition;
        if (!_panning && IsZoomedForPanning() &&
            Math.Sqrt(fromPress.X * fromPress.X + fromPress.Y * fromPress.Y) >= PanStartDistance)
        {
            _panning = true;
            args.Pointer.Capture(PreviewViewport);
        }

        if (_panning)
        {
            Vector delta = position - _lastPointerPosition;
            _translateX += delta.X;
            _translateY += delta.Y;
            ClampTranslation();
            ApplyTransform();
            args.PreventGestureRecognition();
            args.Handled = true;
        }

        _lastPointerPosition = position;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        _contacts.Remove(args.Pointer.Id);
        if (args.Pointer == _selectionPointer)
        {
            UpdateSelection(args.GetPosition(PreviewViewport));
            _selectionPointer = null;
            _selecting = false;
            args.Pointer.Capture(null);
            ShowSelectionMenu();
            UpdateSwipeAvailability();
            args.Handled = true;
        }
        else if (args.Pointer == _activePointer)
        {
            CancelPan();
            _activePointer = null;
        }
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs args)
    {
        _contacts.Remove(args.Pointer.Id);
        if (args.Pointer == _selectionPointer)
        {
            _selectionPointer = null;
            _selecting = false;
            ShowSelectionMenu();
        }
        if (args.Pointer == _activePointer)
        {
            _activePointer = null;
            _panning = false;
        }
        UpdateSwipeAvailability();
    }

    private void OnHolding(object? sender, HoldingRoutedEventArgs args)
    {
        if (args.HoldingState != HoldingState.Started || _pinching || _contacts.Count != 1 ||
            _viewModel?.PreviewInteractionMap?.TextRegions.Count is not > 0)
        {
            return;
        }

        IPointer pointer = _contacts.Values.First();
        PdfPageInteractionRegion? region = FindNearestTextRegion(_lastPointerPosition, false);
        if (region is null)
        {
            return;
        }

        CancelPan();
        _selectionPointer = pointer;
        _selectionPointer.Capture(PreviewViewport);
        _selectionStartIndex = region.TextIndex;
        _selectionEndIndex = region.TextIndex;
        _selecting = true;
        DrawSelection();
        UpdateSwipeAvailability();
        args.Handled = true;
    }

    private void UpdateSelection(Point viewportPoint)
    {
        PdfPageInteractionRegion? region = FindNearestTextRegion(viewportPoint, true);
        if (region is null || region.TextIndex == _selectionEndIndex)
        {
            return;
        }

        _selectionEndIndex = region.TextIndex;
        DrawSelection();
    }

    private PdfPageInteractionRegion? FindNearestTextRegion(
        Point viewportPoint,
        bool allowDistantMatch)
    {
        PdfPageInteractionMap? map = _viewModel?.PreviewInteractionMap;
        if (map is null || _pageWidth <= 0 || _pageHeight <= 0)
        {
            return null;
        }

        Point pagePoint = new(
            (viewportPoint.X - _translateX) / _scale,
            (viewportPoint.Y - _translateY) / _scale);
        double mapX = pagePoint.X * map.Width / _pageWidth;
        double mapY = pagePoint.Y * map.Height / _pageHeight;
        double tolerance = 12 / _scale * map.Width / _pageWidth;
        PdfPageInteractionRegion? hit = map.HitTest(mapX, mapY, tolerance)
            .FirstOrDefault(region => region.Kind == PdfInteractionKind.Text);
        if (hit is not null)
        {
            return hit;
        }

        PdfPageInteractionRegion? nearest = null;
        double nearestDistance = double.MaxValue;
        foreach (PdfPageInteractionRegion region in map.TextRegions)
        {
            double dx = mapX < region.Quad.Left
                ? region.Quad.Left - mapX
                : mapX > region.Quad.Right ? mapX - region.Quad.Right : 0;
            double dy = mapY < region.Quad.Top
                ? region.Quad.Top - mapY
                : mapY > region.Quad.Bottom ? mapY - region.Quad.Bottom : 0;
            double distance = dx * dx + dy * dy;
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = region;
            }
        }

        return allowDistantMatch || nearestDistance <= tolerance * tolerance * 4
            ? nearest
            : null;
    }

    private void DrawSelection()
    {
        SelectionOverlay.Children.Clear();
        PdfPageInteractionMap? map = _viewModel?.PreviewInteractionMap;
        if (map is null || _selectionStartIndex < 0 || _selectionEndIndex < 0)
        {
            _selectedText = string.Empty;
            return;
        }

        int first = Math.Min(_selectionStartIndex, _selectionEndIndex);
        int last = Math.Max(_selectionStartIndex, _selectionEndIndex);
        PdfPageInteractionRegion[] selected = map.TextRegions
            .Where(region => region.TextIndex >= first && region.TextIndex <= last)
            .OrderBy(region => region.TextIndex)
            .ToArray();
        _selectedText = string.Concat(selected.Select(region => region.Text));

        double scaleX = _pageWidth / map.Width;
        double scaleY = _pageHeight / map.Height;
        foreach (Rect rect in MergeSelectionRects(selected, scaleX, scaleY))
        {
            var highlight = new Border
            {
                Width = rect.Width,
                Height = rect.Height,
                Background = SelectionBrush,
                CornerRadius = new CornerRadius(1),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(highlight, rect.X);
            Canvas.SetTop(highlight, rect.Y);
            SelectionOverlay.Children.Add(highlight);
        }
    }

    private static IReadOnlyList<Rect> MergeSelectionRects(
        IReadOnlyList<PdfPageInteractionRegion> regions,
        double scaleX,
        double scaleY)
    {
        var merged = new List<Rect>();
        foreach (PdfPageInteractionRegion region in regions)
        {
            var current = new Rect(
                region.Quad.Left * scaleX,
                region.Quad.Top * scaleY,
                Math.Max(1, region.Quad.Width * scaleX),
                Math.Max(1, region.Quad.Height * scaleY));
            if (merged.Count == 0)
            {
                merged.Add(current);
                continue;
            }

            Rect previous = merged[^1];
            double verticalOverlap = Math.Min(previous.Bottom, current.Bottom) -
                Math.Max(previous.Top, current.Top);
            bool sameLine = verticalOverlap >= Math.Min(previous.Height, current.Height) * 0.55;
            bool adjacent = current.Left - previous.Right <= Math.Max(previous.Height, current.Height);
            if (sameLine && adjacent)
            {
                merged[^1] = previous.Union(current);
            }
            else
            {
                merged.Add(current);
            }
        }

        return merged;
    }

    private void ShowSelectionMenu()
    {
        if (string.IsNullOrWhiteSpace(_selectedText) || SelectionOverlay.Children.Count == 0)
        {
            return;
        }

        Rect bounds = default;
        foreach (Control highlight in SelectionOverlay.Children)
        {
            var rect = new Rect(Canvas.GetLeft(highlight), Canvas.GetTop(highlight),
                highlight.Width, highlight.Height);
            bounds = bounds == default ? rect : bounds.Union(rect);
        }
        App.TextSelectionMenu?.Show(_selectedText,
            new PixelRect(PageLayer.PointToScreen(bounds.TopLeft),
                PageLayer.PointToScreen(bounds.BottomRight)), ClearSelection);
    }

    private void OnSwipeGesture(object? sender, SwipeGestureEventArgs args)
    {
        if (_pinching || _panning || _selecting || IsZoomedForPanning())
        {
            return;
        }

        _swipeTotals.TryGetValue(args.Id, out Vector total);
        _swipeTotals[args.Id] = total + args.Delta;
        args.Handled = true;
    }

    private void OnSwipeGestureEnded(object? sender, SwipeGestureEndedEventArgs args)
    {
        if (!_swipeTotals.Remove(args.Id, out Vector total) ||
            _pinching || _panning || _selecting || IsZoomedForPanning())
        {
            return;
        }

        double threshold = Math.Max(48, PreviewViewport.Bounds.Height * 0.08);
        if (Math.Abs(total.Y) <= Math.Abs(total.X) || Math.Abs(total.Y) < threshold)
        {
            return;
        }

        ClearSelection();
        if (total.Y > 0)
        {
            ExecuteNavigation(_viewModel?.NavigateNextCommand);
        }
        else
        {
            ExecuteNavigation(_viewModel?.NavigatePreviousCommand);
        }
        args.Handled = true;
    }

    private static void ExecuteNavigation(System.Windows.Input.ICommand? command)
    {
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }

    private bool IsZoomedForPanning() => _scale > _fitScale * 1.02;

    private void CancelPan()
    {
        if (_panning)
        {
            _activePointer?.Capture(null);
        }
        _panning = false;
        UpdateSwipeAvailability();
    }

    private void UpdateSwipeAvailability()
    {
        PageSwipeRecognizer.IsEnabled = !_pinching && !_panning && !_selecting &&
            !IsZoomedForPanning();
    }

    private void ClearSelection()
    {
        IPointer? pointer = _selectionPointer;
        _selectionPointer = null;
        pointer?.Capture(null);
        _selectionStartIndex = -1;
        _selectionEndIndex = -1;
        _selectedText = string.Empty;
        _selecting = false;
        SelectionOverlay.Children.Clear();
        App.TextSelectionMenu?.Hide();
        UpdateSwipeAvailability();
    }
}
