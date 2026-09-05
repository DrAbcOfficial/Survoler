using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using OfficeIMO.Pdf;
using Survoler.Documents;
using Survoler.Rendering;
using Survoler.ViewModels;
using Survoler.Views;

namespace Survoler.Tests.Interaction;

[TestClass]
[DoNotParallelize]
public sealed class MainViewGestureTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _) =>
        _session = HeadlessUnitTestSession.StartNew(typeof(MainViewGestureTests));

    [ClassCleanup]
    public static async Task Cleanup() => await _session.DisposeAsync();

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());

    private static async Task Run(Action<Fixture> test)
    {
        await _session.Dispatch(() =>
        {
            using var fixture = new Fixture();
            test(fixture);
        }, CancellationToken.None);
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    public Task PinchSurvivorPansOnFirstMoveAndSupportsRepeatedPinches(int removed, bool cancel) => Run(f =>
    {
        Matrix fit = f.Matrix;
        f.Zoom();
        Assert.AreEqual(fit.M11 * 2, f.Matrix.M11, 0.0001);
        int survivor = 3 - removed;
        Point position = survivor == 1 ? new(100, 250) : new(500, 250);
        f.Touch(removed, cancel ? RawPointerEventType.TouchCancel : RawPointerEventType.TouchEnd,
            removed == 1 ? new(100, 250) : new(500, 250));
        Matrix before = f.Matrix;
        f.Touch(survivor, RawPointerEventType.TouchUpdate, position + new Vector(20, 25));
        AssertTranslation(before, f.Matrix, 20, 25);
        f.Touch(survivor, RawPointerEventType.TouchEnd, position + new Vector(20, 25));

        f.Drag(3, new(300, 250), new(280, 230));
        AssertTranslation(before, f.Matrix, 0, 5);
        before = f.Matrix;
        f.Touch(4, RawPointerEventType.TouchBegin, new(200, 250));
        f.Touch(5, RawPointerEventType.TouchBegin, new(400, 250));
        f.Touch(5, RawPointerEventType.TouchUpdate, new(440, 250));
        Assert.AreEqual(before.M11 * 1.2, f.Matrix.M11, 0.0001);
        f.Touch(5, RawPointerEventType.TouchEnd, new(440, 250));
        before = f.Matrix;
        f.Touch(4, RawPointerEventType.TouchUpdate, new(210, 265));
        AssertTranslation(before, f.Matrix, 10, 15);
        f.Touch(4, RawPointerEventType.TouchEnd, new(210, 265));
        Assert.AreEqual(0, f.Preview.NavigationCalls);
    });

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public Task MotionlessPairRebasesSurvivorAndDoesNotNavigate(int removed) => Run(f =>
    {
        Matrix fit = f.Matrix;
        f.Touch(1, RawPointerEventType.TouchBegin, new(200, 250));
        f.Touch(2, RawPointerEventType.TouchBegin, new(400, 250));
        Assert.IsFalse(f.Swipe.IsEnabled);
        f.Touch(removed, RawPointerEventType.TouchEnd, new(removed == 1 ? 200 : 400, 250));
        f.DragRemaining(3 - removed, new(removed == 1 ? 400 : 200, 250), new(0, -150));
        Assert.AreEqual(fit, f.Matrix);
        Assert.AreEqual(0, f.Preview.NavigationCalls);
        Assert.IsTrue(f.Swipe.IsEnabled);
        f.Zoom();
        f.Touch(1, RawPointerEventType.TouchEnd, new(100, 250));
        f.Touch(2, RawPointerEventType.TouchEnd, new(500, 250));
        Matrix zoom = f.Matrix;
        f.Drag(3, new(300, 250), new(320, 270));
        AssertTranslation(zoom, f.Matrix, 20, 20);
    });

    [TestMethod]
    public Task FreshZoomedDragCannotSwipeButFitSwipeStillNavigates() => Run(f =>
    {
        f.Zoom();
        f.Touch(1, RawPointerEventType.TouchEnd, new(100, 250));
        f.Touch(2, RawPointerEventType.TouchEnd, new(500, 250));
        Matrix before = f.Matrix;
        f.Drag(3, new(300, 350), new(300, 150));
        AssertTranslation(before, f.Matrix, 0, -200);
        Assert.AreEqual(0, f.Preview.NavigationCalls);
        f.Model.IsFitToView = true;
        f.Drag(4, new(300, 350), new(300, 150));
        Assert.AreEqual(1, f.Preview.NavigationCalls);
        Assert.AreEqual(1, f.Model.SelectedNavigationIndex);
    });

    [TestMethod]
    public Task SecondFingerDuringPanKeepsBothCaptures() => Run(f =>
    {
        f.Zoom();
        f.Touch(1, RawPointerEventType.TouchEnd, new(100, 250));
        f.Touch(2, RawPointerEventType.TouchEnd, new(500, 250));
        f.Touch(3, RawPointerEventType.TouchBegin, new(200, 250));
        f.Touch(3, RawPointerEventType.TouchUpdate, new(220, 270));
        Matrix before = f.Matrix;
        f.Touch(4, RawPointerEventType.TouchBegin, new(420, 270));
        f.Touch(4, RawPointerEventType.TouchUpdate, new(460, 270));
        Assert.AreEqual(before.M11 * 1.2, f.Matrix.M11, 0.0001);
        f.Touch(3, RawPointerEventType.TouchCancel, new(220, 270));
        before = f.Matrix;
        f.DragRemaining(4, new(460, 270), new(-20, -20));
        AssertTranslation(before, f.Matrix, -20, -20);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task GeometryReplacementOrDetachClearsLiveContacts(bool detach) => Run(f =>
    {
        f.Zoom();
        if (detach)
        {
            f.Window.Content = null;
            f.Window.Content = f.View;
            f.Flush();
        }
        else
        {
            f.Model.PreviewImage = f.Preview.OtherImage;
        }
        Matrix before = f.Matrix;
        f.Touch(1, RawPointerEventType.TouchUpdate, new(150, 200));
        f.Touch(2, RawPointerEventType.TouchUpdate, new(550, 200));
        Assert.AreEqual(before, f.Matrix);
        f.Touch(1, RawPointerEventType.TouchEnd, new(150, 200));
        f.Touch(2, RawPointerEventType.TouchEnd, new(550, 200));
        f.Model.IsFitToView = true;
        f.Zoom();
        f.Touch(1, RawPointerEventType.TouchEnd, new(100, 250));
        before = f.Matrix;
        f.DragRemaining(2, new(500, 250), new(-15, -20));
        AssertTranslation(before, f.Matrix, -15, -20);
        Assert.AreEqual(0, f.Preview.NavigationCalls);
    });

    [TestMethod]
    public Task MouseLeftPanUsesCaptureOutsideViewportAndRightButtonDoesNotPan() => Run(f =>
    {
        f.Zoom();
        f.Touch(1, RawPointerEventType.TouchEnd, new(100, 250));
        f.Touch(2, RawPointerEventType.TouchEnd, new(500, 250));
        Matrix before = f.Matrix;
        f.Window.MouseDown(f.Root(new(300, 250)), MouseButton.Right);
        f.Window.MouseMove(f.Root(new(320, 270)), RawInputModifiers.RightMouseButton);
        f.Window.MouseUp(f.Root(new(320, 270)), MouseButton.Right);
        Assert.AreEqual(before, f.Matrix);
        f.Window.MouseDown(f.Root(new(300, 20)), MouseButton.Left);
        f.Window.MouseMove(f.Root(new(320, -10)), RawInputModifiers.LeftMouseButton);
        f.Window.MouseUp(f.Root(new(320, -10)), MouseButton.Left);
        AssertTranslation(before, f.Matrix, 20, -30);
        before = f.Matrix;
        f.Window.MouseMove(f.Root(new(340, 40)));
        Assert.AreEqual(before, f.Matrix);
    });

    private static void AssertTranslation(Matrix before, Matrix after, double x, double y)
    {
        Assert.AreEqual(before.M11, after.M11, 0.0001);
        Assert.AreEqual(before.M31 + x, after.M31, 0.0001, "PageLayer X translation");
        Assert.AreEqual(before.M32 + y, after.M32, 0.0001, "PageLayer Y translation");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LongPressSelectsAndDragsThenReleasesOrTransitionsToPinch(bool pinch)
    {
        await _session.Dispatch(async () =>
        {
            using var f = new Fixture();
            using var bytes = new MemoryStream();
            PdfDocument.Create(pdf => pdf.Content(content => content
                .Paragraph(p => p.Text("Selection starts here and extends across this line.")))).Save(bytes);
            PdfPageInteractionMap map = PdfPageInteractionMap.Create(bytes.ToArray(), 1);
            f.Model.PreviewInteractionMap = map;
            Assert.IsGreaterThan(1, map.TextRegions.Count);
            Point Position(PdfPageInteractionRegion region) => new(
                (region.Quad.Left + region.Quad.Width / 2) / map.Width * 1000 * f.Matrix.M11 + f.Matrix.M31,
                (region.Quad.Top + region.Quad.Height / 2) / map.Height * 1200 * f.Matrix.M22 + f.Matrix.M32);
            Point start = Position(map.TextRegions[0]);
            Point end = Position(map.TextRegions[^1]);
            Canvas overlay = f.View.FindControl<Canvas>("SelectionOverlay")!;
            f.Touch(1, RawPointerEventType.TouchBegin, start);
            // Wait for Avalonia's actual holding timer, not a synthetic Holding event.
            for (int i = 0; i < 40 && overlay.Children.Count == 0; i++)
            {
                await Task.Delay(50);
            }
            Assert.IsGreaterThan(0, overlay.Children.Count, "Raw touch must produce a long-press selection.");
            double width = overlay.Children.Sum(c => c.Width);
            Matrix before = f.Matrix;
            f.Touch(1, RawPointerEventType.TouchUpdate, end);
            Assert.IsGreaterThan(width, overlay.Children.Sum(c => c.Width));
            Assert.AreEqual(before, f.Matrix, "Selection drag must not pan the page.");
            if (pinch)
            {
                Point second = end + new Vector(100, 100);
                f.Touch(2, RawPointerEventType.TouchBegin, second);
                Assert.AreEqual(0, overlay.Children.Count);
                f.Touch(2, RawPointerEventType.TouchUpdate, end + new Vector(200, 200));
                Assert.AreEqual(before.M11 * 2, f.Matrix.M11, 0.0001);
                f.Touch(1, RawPointerEventType.TouchCancel, end);
                before = f.Matrix;
                f.DragRemaining(2, end + new Vector(200, 200), new(-15, -20));
                AssertTranslation(before, f.Matrix, -15, -20);
            }
            else
            {
                f.Touch(1, RawPointerEventType.TouchEnd, end);
                Assert.IsGreaterThan(0, overlay.Children.Count);
                f.Touch(2, RawPointerEventType.TouchBegin, new(300, 250));
                Assert.AreEqual(0, overlay.Children.Count);
                f.Touch(2, RawPointerEventType.TouchEnd, new(300, 250));
            }
            Assert.AreEqual(0, f.Preview.NavigationCalls);
            return 0;
        }, CancellationToken.None);
    }

    [TestMethod]
    public Task PinchTracksMidpointAndPanRetainsExistingClamps() => Run(f =>
    {
        f.Zoom();
        Matrix before = f.Matrix;
        f.Touch(1, RawPointerEventType.TouchUpdate, new(120, 280));
        f.Touch(2, RawPointerEventType.TouchUpdate, new(520, 280));
        AssertTranslation(before, f.Matrix, 20, 30);
        f.Touch(1, RawPointerEventType.TouchEnd, new(120, 280));
        f.DragRemaining(2, new(520, 280), new(2000, 2000));
        Assert.AreEqual(12, f.Matrix.M31, 0.0001);
        Assert.AreEqual(12, f.Matrix.M32, 0.0001);
        f.Drag(3, new(300, 250), new(-2000, -2000));
        Assert.AreEqual(f.Viewport.Bounds.Width - 1000 * f.Matrix.M11 - 12, f.Matrix.M31, 0.0001);
        Assert.AreEqual(f.Viewport.Bounds.Height - 1200 * f.Matrix.M22 - 12, f.Matrix.M32, 0.0001);
        Assert.AreEqual(0, f.Preview.NavigationCalls);
    });

    private sealed class Fixture : IDisposable
    {
        public readonly FakePreview Preview = new();
        public readonly MainViewModel Model;
        public readonly MainView View;
        public readonly Window Window;
        public readonly Grid Viewport;
        public readonly SwipeGestureRecognizer Swipe;
        private readonly IInputDevice _touch = (IInputDevice)Activator.CreateInstance(typeof(TouchDevice))!;
        private ulong _timestamp = 1000;
        public Matrix Matrix => View.FindControl<Grid>("PageLayer")!.RenderTransform!.Value;

        public Fixture()
        {
            Application.Current!.Styles.Add(new FluentTheme());
            Model = new MainViewModel(new DocumentActivationService(), new DocumentPreviewService())
            {
                PreviewImage = Preview.PageImage,
                NavigationItems = Preview.NavigationItems,
                SelectedNavigationIndex = 0,
                CanNavigateNext = true
            };
            // Inject only the preview fixture; all gestures use the real platform input route.
            typeof(MainViewModel).GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Model, Preview);
            View = new MainView { DataContext = Model };
            Window = new Window { Width = 600, Height = 800, Content = View };
            Window.Show();
            Flush();
            Viewport = View.FindControl<Grid>("PreviewViewport")!;
            Swipe = Viewport.GestureRecognizers.OfType<SwipeGestureRecognizer>().Single();
            Assert.IsGreaterThan(400, Viewport.Bounds.Height);
            Assert.IsGreaterThan(0, Matrix.M11);
        }

        public void Flush() => Window.MouseMove(new Point(1, 1));
        public Point Root(Point point) => Viewport.TranslatePoint(point, Window)!.Value;

        public void Touch(int id, RawPointerEventType type, Point position)
        {
            Dispatcher.UIThread.RunJobs();
            // Avalonia.Headless 12.1.2 has mouse helpers but no touch helpers. Pin this
            // private input-root lookup to that version; do not put it in application code.
            var root = (IInputRoot)typeof(TopLevel)
                .GetProperty("InputRoot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
            var input = (Action<RawInputEventArgs>)typeof(Avalonia.Platform.ITopLevelImpl)
                .GetProperty("Input")!.GetValue(Window.PlatformImpl)!;
            var args = (RawInputEventArgs)Activator.CreateInstance(typeof(RawTouchEventArgs),
                _touch, _timestamp += 20, root, type, Root(position), RawInputModifiers.None, (long)id)!;
            input(args);
            Dispatcher.UIThread.RunJobs();
        }

        public void Zoom()
        {
            Touch(1, RawPointerEventType.TouchBegin, new(200, 250));
            Touch(2, RawPointerEventType.TouchBegin, new(400, 250));
            Assert.IsFalse(Swipe.IsEnabled);
            Touch(1, RawPointerEventType.TouchUpdate, new(100, 250));
            Touch(2, RawPointerEventType.TouchUpdate, new(500, 250));
        }

        public void Drag(int id, Point start, Point end)
        {
            Touch(id, RawPointerEventType.TouchBegin, start);
            DragRemaining(id, start, end - start);
        }

        public void DragRemaining(int id, Point start, Vector delta)
        {
            Touch(id, RawPointerEventType.TouchUpdate, start + delta);
            Touch(id, RawPointerEventType.TouchEnd, start + delta);
        }

        public void Dispose()
        {
            Window.Content = null;
            ((IDisposable)_touch).Dispose();
            Window.Close();
            Model.Dispose();
        }
    }

    private sealed class FakePreview : IDocumentPreview
    {
        public Bitmap PageImage { get; } = CreateImage();
        public Bitmap OtherImage { get; } = CreateImage();
        public IReadOnlyList<string> NavigationItems { get; } = ["1", "2", "3"];
        public int SelectedIndex { get; private set; }
        public int NavigationCalls { get; private set; }
        public string? Warning => null;
        public Task<Bitmap> SelectAsync(int index, CancellationToken cancellationToken)
        {
            NavigationCalls++;
            SelectedIndex = index;
            return Task.FromResult(OtherImage);
        }
        public Task<PdfPageInteractionMap?> GetInteractionMapAsync(int index, CancellationToken cancellationToken) =>
            Task.FromResult<PdfPageInteractionMap?>(null);
        public void Dispose()
        {
            PageImage.Dispose();
            OtherImage.Dispose();
        }
        private static Bitmap CreateImage() => new WriteableBitmap(new PixelSize(1000, 1200), new Vector(96, 96));
    }
}
