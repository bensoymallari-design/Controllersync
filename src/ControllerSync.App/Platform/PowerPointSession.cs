using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ControllerSync.Core;

namespace ControllerSync.App.Platform;

[SupportedOSPlatform("windows")]
public sealed class PowerPointSession : ISlideBridge, IDisposable
{
    private readonly StaRunner _sta = new();

    public SlideEvent? TryRead()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        return _sta.Run(ReadCore, null, TimeSpan.FromSeconds(2));
    }

    public SlideApplyResult Apply(SlideEvent slide)
    {
        if (!OperatingSystem.IsWindows())
            return new SlideApplyResult(false, null);
        return _sta.Run(() => ApplyCore(slide), new SlideApplyResult(false, "PowerPoint is not running on this laptop."), TimeSpan.FromSeconds(3));
    }

    public bool TryStep(ShowAction action)
    {
        if (!OperatingSystem.IsWindows() || action == ShowAction.None)
            return false;
        return _sta.Run(() => StepCore(action), false, TimeSpan.FromSeconds(3));
    }

    public void Dispose() => _sta.Dispose();

    private static SlideEvent? ReadCore()
    {
        var app = TryAttach();
        if (app == null)
            return null;
        try
        {
            string name;
            int total;
            try
            {
                dynamic presentation = app.ActivePresentation;
                name = (string)presentation.FullName;
                total = (int)presentation.Slides.Count;
            }
            catch
            {
                return null;
            }

            dynamic? view = ViewOrNull(app);
            if (view == null)
                return new SlideEvent(name, 0, 0, total, false, ShowScreen.Done);

            int index = (int)view.CurrentShowPosition;
            int click = 0;
            try
            {
                click = (int)view.GetClickIndex();
            }
            catch
            {
                // Older PowerPoint builds do not expose the animation click.
            }

            int state = 1;
            try
            {
                state = (int)view.State;
            }
            catch
            {
                state = 1;
            }

            var screen = Enum.IsDefined(typeof(ShowScreen), state) ? (ShowScreen)state : ShowScreen.Unknown;
            return new SlideEvent(name, index, click, total, true, screen);
        }
        finally
        {
            Release(app);
        }
    }

    private static SlideApplyResult ApplyCore(SlideEvent slide)
    {
        var app = TryAttach();
        if (app == null)
            return new SlideApplyResult(false, "PowerPoint is not running on this laptop.");

        try
        {
            string? warning = DeckWarning(app, slide.DeckName);
            if (!slide.InShow)
            {
                dynamic? endView = ViewOrNull(app);
                if (endView != null)
                {
                    try
                    {
                        endView.Exit();
                    }
                    catch
                    {
                        return new SlideApplyResult(false, warning ?? "Could not leave the slideshow on this laptop.");
                    }
                }

                return new SlideApplyResult(true, warning);
            }

            dynamic? view = ViewOrNull(app);
            if (view == null)
            {
                try
                {
                    dynamic settings = app.ActivePresentation.SlideShowSettings;
                    settings.RangeType = 1;
                    settings.Run();
                }
                catch
                {
                    return new SlideApplyResult(false, warning ?? "Could not start the slideshow on this laptop.");
                }

                view = ViewOrNull(app);
                if (view == null)
                    return new SlideApplyResult(false, warning ?? "The slideshow did not start on this laptop.");
            }

            try
            {
                if (slide.SlideIndex > 0 && (int)view.CurrentShowPosition != slide.SlideIndex)
                    view.GotoSlide(slide.SlideIndex);
            }
            catch
            {
                return new SlideApplyResult(false, warning ?? "Could not change slides on this laptop.");
            }

            try
            {
                if (slide.ClickIndex > 0 && (int)view.GetClickIndex() != slide.ClickIndex)
                    view.GotoClick(slide.ClickIndex);
            }
            catch
            {
                // Animation steps are optional. The slide index is already aligned.
            }

            try
            {
                if (slide.Screen is ShowScreen.Running or ShowScreen.Paused or ShowScreen.Black or ShowScreen.White
                    && (int)view.State != (int)slide.Screen)
                    view.State = (int)slide.Screen;
            }
            catch
            {
                // Screen color is optional.
            }

            return new SlideApplyResult(true, warning);
        }
        finally
        {
            Release(app);
        }
    }

    private static bool StepCore(ShowAction action)
    {
        var app = TryAttach();
        if (app == null)
            return false;
        try
        {
            switch (action)
            {
                case ShowAction.StartFromFirst:
                    app.ActivePresentation.SlideShowSettings.RangeType = 1;
                    app.ActivePresentation.SlideShowSettings.Run();
                    return true;
                case ShowAction.StartFromCurrent:
                    int current = 1;
                    try
                    {
                        current = (int)app.ActiveWindow.View.Slide.SlideIndex;
                    }
                    catch
                    {
                        current = 1;
                    }

                    int count = (int)app.ActivePresentation.Slides.Count;
                    dynamic settings = app.ActivePresentation.SlideShowSettings;
                    settings.RangeType = 2;
                    settings.StartingSlide = current;
                    settings.EndingSlide = Math.Max(current, count);
                    settings.Run();
                    return true;
                case ShowAction.EndShow:
                    dynamic? endView = ViewOrNull(app);
                    if (endView == null)
                        return false;
                    endView.Exit();
                    return true;
                case ShowAction.BlackScreen:
                case ShowAction.WhiteScreen:
                    dynamic? colorView = ViewOrNull(app);
                    if (colorView == null)
                        return false;
                    colorView.State = action == ShowAction.BlackScreen ? 3 : 4;
                    return true;
            }

            dynamic? view = ViewOrNull(app);
            if (view == null)
            {
                if (action != ShowAction.Next)
                    return false;
                app.ActivePresentation.SlideShowSettings.RangeType = 1;
                app.ActivePresentation.SlideShowSettings.Run();
                return true;
            }

            switch (action)
            {
                case ShowAction.Next:
                    view.Next();
                    return true;
                case ShowAction.Previous:
                    view.Previous();
                    return true;
                case ShowAction.First:
                    view.GotoSlide(1);
                    return true;
                case ShowAction.Last:
                    view.GotoSlide((int)view.Presentation.Slides.Count);
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(app);
        }
    }

    private static string? DeckWarning(dynamic app, string remoteDeck)
    {
        var remoteName = Path.GetFileName(remoteDeck ?? "");
        if (remoteName.Length == 0)
            return null;
        try
        {
            string localName = Path.GetFileName((string)app.ActivePresentation.FullName);
            if (localName.Length > 0 && !localName.Equals(remoteName, StringComparison.OrdinalIgnoreCase))
                return "A different PowerPoint file is open on this laptop (" + localName + "). Slide numbers may not match.";
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static dynamic? ViewOrNull(dynamic app)
    {
        try
        {
            if ((int)app.SlideShowWindows.Count < 1)
                return null;
            dynamic view = app.SlideShowWindows[1].View;
            if ((int)view.State == 5)
                return null;
            return view;
        }
        catch
        {
            return null;
        }
    }

    private static dynamic? TryAttach()
    {
        var hr = NativeOle.CLSIDFromProgID("PowerPoint.Application", out var clsid);
        if (hr != 0)
            return null;
        hr = NativeOle.GetActiveObject(ref clsid, IntPtr.Zero, out var app);
        if (hr != 0)
            return null;
        return app;
    }

    private static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com))
        {
            try
            {
                Marshal.ReleaseComObject(com);
            }
            catch
            {
                // PowerPoint may already have gone away.
            }
        }
    }

    private static class NativeOle
    {
        [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
        public static extern int CLSIDFromProgID(string lpszProgId, out Guid pclsid);

        [DllImport("oleaut32.dll")]
        public static extern int GetActiveObject(ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);
    }

    private sealed class StaRunner : IDisposable
    {
        private readonly BlockingCollection<Action> _jobs = new();
        private readonly Thread _thread;

        public StaRunner()
        {
            _thread = new Thread(() =>
            {
                foreach (var job in _jobs.GetConsumingEnumerable())
                {
                    try
                    {
                        job();
                    }
                    catch
                    {
                        // Each job records its own failure.
                    }
                }
            })
            {
                IsBackground = true,
                Name = "ControllerSync.PowerPoint"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public T Run<T>(Func<T> func, T fallback, TimeSpan timeout)
        {
            if (_jobs.IsAddingCompleted)
                return fallback;

            var done = new ManualResetEventSlim(false);
            T result = fallback;
            try
            {
                _jobs.Add(() =>
                {
                    try
                    {
                        result = func();
                    }
                    catch
                    {
                        result = fallback;
                    }
                    finally
                    {
                        done.Set();
                    }
                });
            }
            catch (InvalidOperationException)
            {
                return fallback;
            }

            done.Wait(timeout);
            return result;
        }

        public void Dispose()
        {
            _jobs.CompleteAdding();
        }
    }
}
