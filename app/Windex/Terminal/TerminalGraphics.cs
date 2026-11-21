// The GPU plumbing all TerminalViews on a UI thread share: Win2D's shared CanvasDevice, one
// CompositionGraphicsDevice per compositor for their drawing surfaces, recovery when the device
// is lost, and frame pacing (each invalidated view draws once, on the next UI frame).
//
// Why one shared graphics device rather than a CanvasControl or CanvasImageSource per terminal:
// with each of those, and with a CompositionGraphicsDevice per terminal, every terminal that came
// and went left its whole surface allocated on the GPU (about 5 MB for a 1300×800 pane) for the
// life of the process, released or not (Windows App SDK 2.7, Win2D 1.4). Drawing surfaces made by
// one long-lived graphics device are freed when disposed. TerminalLab --leak-check measures this.
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace Windex.Terminal;

internal static class TerminalGraphics
{
    [ThreadStatic] private static ThreadState? t_state;

    private static ThreadState State => t_state ??= new ThreadState();

    /// <summary>The device terminals on this thread draw with (replaced after a device loss).</summary>
    public static CanvasDevice Device => State.Device;

    /// <summary>The graphics device that makes drawing surfaces for this compositor.</summary>
    public static CompositionGraphicsDevice GraphicsDevice(Compositor compositor) => State.GraphicsDevice(compositor);

    /// <summary>A loaded view: told when the device is replaced (it must rebuild its GPU resources).</summary>
    public static void Attach(TerminalView view) => State.Live.Add(view);

    public static void Detach(TerminalView view)
    {
        State.Live.Remove(view);
        State.Pending.Remove(view);
    }

    /// <summary>Draws the view on the next UI frame (once, however often it is asked).</summary>
    public static void RequestRender(TerminalView view) => State.RequestRender(view);

    /// <summary>What a device loss does: moves this thread's terminals to the current shared device,
    /// rebuilding their GPU resources and redrawing them. (Win2D raises DeviceLost only for a device
    /// that is really gone, so TerminalLab calls this to exercise the recovery.)</summary>
    public static void Recover() => State.Recover();

    private sealed class ThreadState
    {
        public readonly HashSet<TerminalView> Live = new();
        public readonly HashSet<TerminalView> Pending = new();
        private readonly List<TerminalView> _drawing = new();
        private readonly Dictionary<Compositor, CompositionGraphicsDevice> _graphics = new();
        private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread();
        private bool _hooked;

        public ThreadState()
        {
            Device = CanvasDevice.GetSharedDevice();
            Device.DeviceLost += OnDeviceLost;
        }

        public CanvasDevice Device { get; private set; }

        public CompositionGraphicsDevice GraphicsDevice(Compositor compositor)
        {
            if (!_graphics.TryGetValue(compositor, out var graphics))
            {
                graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, Device);
                graphics.RenderingDeviceReplaced += OnRenderingDeviceReplaced;
                _graphics.Add(compositor, graphics);
            }
            return graphics;
        }

        public void RequestRender(TerminalView view)
        {
            Pending.Add(view);
            if (_hooked) return;
            _hooked = true;
            CompositionTarget.Rendering += OnRendering;
        }

        private void OnRendering(object? sender, object args)
        {
            _drawing.AddRange(Pending);
            Pending.Clear();
            try
            {
                foreach (var view in _drawing)
                {
                    try
                    {
                        view.Render();
                    }
                    catch (Exception e) when (Device.IsDeviceLost(e.HResult))
                    {
                        // Raises DeviceLost, which moves every terminal to a new device.
                        Device.RaiseDeviceLost();
                        break;
                    }
                }
            }
            finally
            {
                _drawing.Clear();
                // Unhooked while idle: a Rendering handler keeps XAML producing frames.
                if (Pending.Count == 0)
                {
                    CompositionTarget.Rendering -= OnRendering;
                    _hooked = false;
                }
            }
        }

        private void OnDeviceLost(CanvasDevice sender, object args)
        {
            // The shared device is process-wide: another thread may be the one that found it lost.
            if (!_queue.HasThreadAccess)
            {
                _queue.TryEnqueue(() => OnDeviceLost(sender, args));
                return;
            }
            if (sender == Device) Recover();
        }

        public void Recover()
        {
            // Win2D hands out a new shared device once the old one is really gone.
            Device.DeviceLost -= OnDeviceLost;
            Device = CanvasDevice.GetSharedDevice();
            Device.DeviceLost += OnDeviceLost;
            foreach (var graphics in _graphics.Values) CanvasComposition.SetCanvasDevice(graphics, Device);
            ResetViews();
        }

        /// <summary>The compositor moved the drawing surfaces to another device: their contents are gone.</summary>
        private void OnRenderingDeviceReplaced(CompositionGraphicsDevice sender, RenderingDeviceReplacedEventArgs args) => ResetViews();

        /// <summary>Every loaded view rebuilds its GPU resources on the current device and redraws.</summary>
        private void ResetViews()
        {
            foreach (var view in Live.ToArray()) view.OnDeviceReplaced();
        }
    }
}
