using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;

namespace Survoler.Views;

public partial class MainView
{
    private const double MaximumScale = 6;
    private const double PanStartDistance = 8;

    private readonly Dictionary<int, IPointer> _contacts = new();
    private readonly Dictionary<int, Point> _contactPositions = new();
    private readonly Dictionary<int, Vector> _swipeTotals = new();
    private IPointer? _activePointer;
    private Point _pressPosition;
    private Point _lastPointerPosition;
    private double _pinchStartScale;
    private double _pinchStartDistance;
    private Point _pinchPageOrigin;
    private bool _pinching;
    private bool _panning;
    private bool _hadMultipleContacts;

    private void BeginPinch()
    {
        _pinching = true;
        _hadMultipleContacts = true;
        CancelPan();
        ClearSelection();
        _activePointer = null;
        _swipeTotals.Clear();
        Point[] pair = _contactPositions.Values.Take(2).ToArray();
        _pinchStartDistance = ((Vector)(pair[1] - pair[0])).Length;
        Point origin = new((pair[0].X + pair[1].X) / 2, (pair[0].Y + pair[1].Y) / 2);
        _pinchStartScale = _scale;
        _pinchPageOrigin = new Point(
            (origin.X - _translateX) / _scale,
            (origin.Y - _translateY) / _scale);
    }

    private void UpdatePinch()
    {
        if (_pinchStartDistance <= 0)
        {
            BeginPinch();
            return;
        }

        Point[] pair = _contactPositions.Values.Take(2).ToArray();
        Point origin = new((pair[0].X + pair[1].X) / 2, (pair[0].Y + pair[1].Y) / 2);
        double minimumScale = Math.Max(0.1, Math.Min(_fitScale, 1) * 0.75);
        _scale = Math.Clamp(_pinchStartScale * ((Vector)(pair[1] - pair[0])).Length / _pinchStartDistance,
            minimumScale, MaximumScale);
        _translateX = origin.X - _pinchPageOrigin.X * _scale;
        _translateY = origin.Y - _pinchPageOrigin.Y * _scale;
        SetCustomViewMode();
        ClampTranslation();
        ApplyTransform();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (_pageWidth <= 0 || _pageHeight <= 0 ||
            (args.Pointer.Type != PointerType.Touch &&
             !args.GetCurrentPoint(PreviewViewport).Properties.IsLeftButtonPressed))
        {
            return;
        }
        Point position = args.GetPosition(PreviewViewport);
        _contacts[args.Pointer.Id] = args.Pointer;
        _contactPositions[args.Pointer.Id] = position;
        args.Pointer.Capture(PreviewViewport);
        if (_contacts.Count > 1)
        {
            if (_contacts.Count == 2)
            {
                BeginPinch();
            }
            args.PreventGestureRecognition();
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
        if (!_contacts.ContainsKey(args.Pointer.Id))
        {
            return;
        }
        _contactPositions[args.Pointer.Id] = position;
        if (_hadMultipleContacts || IsZoomedForPanning())
        {
            args.PreventGestureRecognition();
        }
        if (_pinching)
        {
            UpdatePinch();
            args.PreventGestureRecognition();
            args.Handled = true;
            return;
        }
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
        if (args.Pointer == _selectionPointer)
        {
            UpdateSelection(args.GetPosition(PreviewViewport));
            _selectionPointer = null;
            _selecting = false;
            ShowSelectionMenu();
            UpdateSwipeAvailability();
            args.Handled = true;
        }
        RemoveContact(args.Pointer);
        args.Pointer.Capture(null);
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs args)
    {
        if (args.Pointer == _selectionPointer)
        {
            _selectionPointer = null;
            _selecting = false;
            ShowSelectionMenu();
        }
        RemoveContact(args.Pointer);
    }

    private void RemoveContact(IPointer pointer)
    {
        if (!_contacts.Remove(pointer.Id))
        {
            return;
        }
        _contactPositions.Remove(pointer.Id);
        _pinching = false;
        _panning = false;
        _activePointer = null;
        if (_contacts.Count >= 2)
        {
            BeginPinch();
        }
        else if (_contacts.Count == 1)
        {
            _activePointer = _contacts.Values.First();
            _pressPosition = _lastPointerPosition = _contactPositions[_activePointer.Id];
        }
        else
        {
            _hadMultipleContacts = false;
        }
        ClampTranslation();
        ApplyTransform();
    }

    private void CancelAllContacts()
    {
        IPointer[] pointers = _contacts.Values.ToArray();
        // Clear state before releasing capture, which synchronously raises CaptureLost.
        _contacts.Clear();
        _contactPositions.Clear();
        _swipeTotals.Clear();
        _activePointer = null;
        _pinching = false;
        _panning = false;
        _hadMultipleContacts = false;
        ClearSelection();
        foreach (IPointer pointer in pointers)
        {
            pointer.Capture(null);
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
        _panning = false;
        UpdateSwipeAvailability();
    }

    private void UpdateSwipeAvailability()
    {
        PageSwipeRecognizer.IsEnabled = !_hadMultipleContacts && !_pinching && !_panning && !_selecting &&
            !IsZoomedForPanning();
    }
}
