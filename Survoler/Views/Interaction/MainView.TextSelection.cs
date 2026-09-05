using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using OfficeIMO.Pdf;

namespace Survoler.Views;

public partial class MainView
{
    private static readonly ImmutableSolidColorBrush SelectionBrush = new(
        Color.FromArgb(96, 27, 135, 255));

    private IPointer? _selectionPointer;
    private int _selectionStartIndex = -1;
    private int _selectionEndIndex = -1;
    private string _selectedText = string.Empty;
    private bool _selecting;

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

    private void ClearSelection()
    {
        IPointer? pointer = _selectionPointer;
        _selectionPointer = null;
        if (pointer is not null && !_contacts.ContainsKey(pointer.Id))
        {
            pointer.Capture(null);
        }
        _selectionStartIndex = -1;
        _selectionEndIndex = -1;
        _selectedText = string.Empty;
        _selecting = false;
        SelectionOverlay.Children.Clear();
        App.TextSelectionMenu?.Hide();
        UpdateSwipeAvailability();
    }
}
