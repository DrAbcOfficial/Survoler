using System;
using Avalonia;

namespace Survoler;

public interface ITextSelectionMenu
{
    void Show(string text, PixelRect screenBounds, Action onDismissed);
    void Hide();
}
