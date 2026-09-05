using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;

namespace Survoler.Views;

public partial class MainView
{
    private const double MaximumScale = 6;
    private const double PanStartDistance = 8;

    private readonly Dictionary<int, IPointer> _contacts = new();
    private readonly Dictionary<int, Vector> _swipeTotals = new();
    private IPointer? _activePointer;
    private Point _pressPosition;
    private Point _lastPointerPosition;
    private double _pinchStartScale;
    private double _pinchStartTranslateX;
    private double _pinchStartTranslateY;
    private Point _pinchPageOrigin;
    private bool _pinching;
    private bool _panning;

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
}
