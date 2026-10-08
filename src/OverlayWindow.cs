using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace KeyCascade
{
    public sealed class OverlaySurface : FrameworkElement
    {
        public AppConfig Config;
        public Layout Layout;
        public InputEngine Engine;
        public bool Editing;
        public double DemoUntil, DemoStart;
        public double RenderTime = -1;
        public event Action<double, double> FrameDrawn;
        public long LabelLayoutCount { get; private set; }
        public long CounterLayoutCount { get; private set; }

        private sealed class LaneAssets
        {
            public KeySpec Key;
            public Rect Rect;
            public SolidColorBrush Color;
            public RectangleGeometry Clip;
            public DrawingGroup IdleKey, HeldKey;
            public DrawingVisual Visual = new DrawingVisual();
            public Geometry CountGeometry;
            public long LastCount = -1;
            public bool LastDown;
            public List<Trail> Trails = new List<Trail>();
        }
        private readonly VisualCollection children;
        private readonly DrawingVisual backgroundVisual = new DrawingVisual();
        private readonly DrawingVisual trailsVisual = new DrawingVisual();
        private readonly DrawingVisual statusVisual = new DrawingVisual();
        private readonly List<LaneAssets> lanes = new List<LaneAssets>();
        private readonly Dictionary<string, LaneAssets> laneById = new Dictionary<string, LaneAssets>();
        private readonly Typeface font = new Typeface("Segoe UI");
        private static readonly SolidColorBrush idleFill = Brush(Color.FromArgb(120, 8, 17, 27));
        private static readonly SolidColorBrush heldFill = Brush(Color.FromArgb(225, 8, 17, 27));
        private static readonly SolidColorBrush countBrush = Brush(Color.FromRgb(206, 217, 232));
        private static readonly SolidColorBrush statusBrush = Brush(Color.FromRgb(206, 224, 237));
        private SolidColorBrush backgroundBrush, editBackgroundBrush;
        private Geometry editText, demoText;
        private bool lastEditing, lastDemo, backgroundReady;

        public OverlaySurface() { children = new VisualCollection(this); }
        protected override int VisualChildrenCount { get { return children.Count; } }
        protected override Visual GetVisualChild(int index) { return children[index]; }
        private static SolidColorBrush Brush(Color color) { var b = new SolidColorBrush(color); b.Freeze(); return b; }
        private static Pen Outline(Brush brush, double width) { var p = new Pen(brush, width); p.Freeze(); return p; }
        private Geometry TextGeometry(string text, double size, double x, double y, double maxWidth)
        {
            var f = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, font, size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            f.MaxTextWidth = Math.Max(1, maxWidth); f.MaxLineCount = 1; f.Trimming = TextTrimming.CharacterEllipsis;
            var geometry = f.BuildGeometry(new Point(x + Math.Max(0, (maxWidth - f.Width) / 2), y)); geometry.Freeze(); return geometry;
        }
        public void Configure(AppConfig c)
        {
            Config = c; Layout = KeyCascade.Layout.Calculate(c); Width = Layout.Width; Height = Layout.Height;
            lanes.Clear(); laneById.Clear(); children.Clear();
            children.Add(backgroundVisual); children.Add(trailsVisual);
            backgroundBrush = Brush(Color.FromArgb((byte)(c.BackgroundOpacity * 255), 9, 15, 24));
            editBackgroundBrush = Brush(Color.FromArgb((byte)(Math.Max(c.BackgroundOpacity, 0.10) * 255), 9, 15, 24));
            for (int i = 0; i < c.Keys.Count; i++)
            {
                var k = c.Keys[i]; var rect = Layout.Keys[i];
                var lane = new LaneAssets { Key = k, Rect = rect, Color = Brush(AppConfig.ParseColor(k.Color)) };
                lane.Clip = new RectangleGeometry(new Rect(rect.Left, Math.Max(0, rect.Top - c.TrailHeight), rect.Width, Math.Min(c.TrailHeight, rect.Top))); lane.Clip.Freeze();
                double labelSize = Math.Min(14, Math.Max(9, rect.Height * 0.29));
                double labelY = c.ShowCounts && rect.Height >= 36 ? rect.Y + rect.Height * 0.12 : rect.Y + (rect.Height - labelSize * 1.3) / 2;
                var label = TextGeometry(k.Label, labelSize, rect.X + 4, labelY, rect.Width - 8); LabelLayoutCount++;
                lane.IdleKey = BuildKey(lane, label, false); lane.HeldKey = BuildKey(lane, label, true);
                lanes.Add(lane); laneById.Add(k.Id, lane); children.Add(lane.Visual);
            }
            children.Add(statusVisual);
            editText = TextGeometry("拖动调整位置  ·  双击打开设置  ·  Ctrl+Alt+F9 锁定", 11, 4, 8, Width - 8);
            demoText = TextGeometry("演示中 · 真实按键仍会显示", 11, 4, 8, Width - 8);
            backgroundReady = false;
            UpdateFrame(RenderTime >= 0 ? RenderTime : KeyboardHook.Now, true);
        }
        private DrawingGroup BuildKey(LaneAssets lane, Geometry label, bool down)
        {
            var group = new DrawingGroup(); var r = lane.Rect;
            using (var dc = group.Open())
            {
                dc.DrawRoundedRectangle(down ? heldFill : idleFill, Outline(lane.Color, down ? 2 : 1.2), r, 6, 6);
                if (down) { dc.PushOpacity(0.28); dc.DrawRoundedRectangle(lane.Color, null, r, 6, 6); dc.Pop(); }
                dc.PushOpacity(down ? 1 : 0.72);
                dc.DrawRoundedRectangle(lane.Color, null, new Rect(r.X + 9, r.Y + 1, Math.Max(2, r.Width - 18), 2), 1, 1); dc.Pop();
                dc.DrawGeometry(lane.Color, null, label);
            }
            group.Freeze(); return group;
        }
        protected override void OnRender(DrawingContext dc)
        {
            // Only settings, count resets and previews invalidate the parent.
            // Animation retains unchanged key visuals and updates the trail visual.
            UpdateFrame(RenderTime >= 0 ? RenderTime : KeyboardHook.Now, true);
        }
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi); if (Config != null) Configure(Config);
        }
        public void UpdateFrame(double now, bool force)
        {
            if (Config == null || Engine == null) return;
            double drawStart = KeyboardHook.Now; bool demo = now < DemoUntil;
            if (force || !backgroundReady || lastEditing != Editing || lastDemo != demo)
            {
                using (var dc = backgroundVisual.RenderOpen()) dc.DrawRoundedRectangle(Editing ? editBackgroundBrush : backgroundBrush, null, new Rect(0, 0, Width, Height), 12, 12);
                using (var dc = statusVisual.RenderOpen())
                {
                    if (Editing) dc.DrawGeometry(statusBrush, null, editText);
                    else if (demo) dc.DrawGeometry(statusBrush, null, demoText);
                }
                lastEditing = Editing; lastDemo = demo; backgroundReady = true;
            }
            foreach (var lane in lanes) lane.Trails.Clear();
            foreach (var trail in Engine.Trails) { LaneAssets lane; if (laneById.TryGetValue(trail.KeyId, out lane)) lane.Trails.Add(trail); }
            using (var dc = trailsVisual.RenderOpen())
            {
                for (int i = 0; i < lanes.Count; i++)
                {
                    var lane = lanes[i]; if (lane.Trails.Count == 0 && !demo) continue;
                    dc.PushClip(lane.Clip);
                    foreach (var trail in lane.Trails) DrawTrail(dc, trail.Start, trail.End, now, lane);
                    if (demo)
                    {
                        for (int n = 0; n < 10; n++)
                        {
                            double start = DemoStart + n * 0.28 + (i % 4) * 0.09;
                            if (start > now || now - start > Config.TrailHeight / Config.Speed + 1) continue;
                            double end = start + ((n + i) % 5 == 0 ? 0.45 : 0.085);
                            DrawTrail(dc, start, end < now ? (double?)end : null, now, lane);
                        }
                    }
                    dc.Pop();
                }
            }
            foreach (var lane in lanes)
            {
                bool down = Engine.IsDown(lane.Key.Id); long count = Engine.Count(lane.Key.Id);
                if (!force && lane.LastCount >= 0 && lane.LastDown == down && (!Config.ShowCounts || lane.LastCount == count)) continue;
                var r = lane.Rect;
                if (Config.ShowCounts && r.Height >= 36 && (lane.CountGeometry == null || lane.LastCount != count))
                {
                    lane.CountGeometry = TextGeometry(count.ToString("N0"), Math.Min(11, r.Height * 0.22), r.X + 4, r.Y + r.Height * 0.57, r.Width - 8); CounterLayoutCount++;
                }
                using (var dc = lane.Visual.RenderOpen())
                {
                    dc.DrawDrawing(down ? lane.HeldKey : lane.IdleKey);
                    if (Config.ShowCounts && r.Height >= 36) dc.DrawGeometry(countBrush, null, lane.CountGeometry);
                }
                lane.LastDown = down; lane.LastCount = count;
            }
            if (FrameDrawn != null) FrameDrawn(drawStart, (KeyboardHook.Now - drawStart) * 1000);
        }
        private void DrawTrail(DrawingContext dc, double start, double? end, double now, LaneAssets lane)
        {
            var key = lane.Rect; var brush = lane.Color;
            double ended = end ?? now;
            double bottom = key.Top - 7 - Math.Max(0, now - ended) * Config.Speed;
            double top = key.Top - 7 - Math.Max(0, now - start) * Config.Speed;
            double height = Math.Max(5, bottom - top);
            if (bottom < key.Top - Config.TrailHeight || top > key.Top) return;
            var r = new Rect(key.Left + 5, bottom - height, Math.Max(2, key.Width - 10), height);
            double age = Math.Max(0, now - ended) * Config.Speed / Config.TrailHeight;
            dc.PushOpacity(Config.Fade ? Math.Max(0.16, 1 - age * 0.75) : 1);
            dc.PushOpacity(0.18); dc.DrawRoundedRectangle(brush, null, new Rect(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4), 4, 4); dc.Pop();
            dc.DrawRoundedRectangle(brush, null, r, 2, 2);
            dc.PushOpacity(0.42); dc.DrawRectangle(Brushes.White, null, new Rect(r.X + 2, r.Y + 1, Math.Max(1, r.Width - 4), Math.Min(2, r.Height))); dc.Pop(); dc.Pop();
        }
    }

    public sealed class OverlayWindow : Window
    {
        public const string WindowTitle = "KeyCascade Overlay";
        public readonly OverlaySurface Surface;
        public readonly InputEngine Engine;
        private readonly KeyboardHook keyboard;
        private readonly DispatcherTimer maintenance;
        private readonly List<KeyEvent> inputBatch = new List<KeyEvent>();
        private readonly Action dispatchInput;
        private int inputDispatchPending;
        private IntPtr handle;
        private AppConfig config;
        private double lastTopmost, lastRecovery;
        private bool closing, rendering;
        private TimeSpan lastRenderingTime = TimeSpan.MinValue;
        public bool AnimationActive { get { return rendering; } }
        public event Action<int> Command;
        public event Action<double, double> Moved;
        public event Action<string> Notice;
        public readonly List<int> FailedHotkeys = new List<int>();
        public OverlayWindow(AppConfig c, KeyboardHook hook)
        {
            config = c; keyboard = hook; Engine = new InputEngine(c);
            Title = WindowTitle; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
            Surface = new OverlaySurface { Engine = Engine }; Content = Surface;
            Apply(c);
            SourceInitialized += OnSource;
            MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (config.ClickThrough) return;
                if (e.ClickCount == 2) { if (Command != null) Command(1); return; }
                try { DragMove(); } catch (InvalidOperationException) { }
                config.Left = Left; config.Top = Top; if (Moved != null) Moved(Left, Top);
            };
            MouseRightButtonUp += delegate { if (!config.ClickThrough && Command != null) Command(1); };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!closing) { e.Cancel = true; Hide(); } };
            IsVisibleChanged += delegate { if (IsVisible) { Surface.UpdateFrame(KeyboardHook.Now, true); RequestAnimationFrame(); } else StopRendering(); };
            dispatchInput = ProcessPendingInput;
            if (keyboard != null) { keyboard.InputAvailable += InputArrived; ProcessPendingInput(); }
            maintenance = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            maintenance.Tick += Maintenance; maintenance.Start();
        }
        public void Apply(AppConfig c)
        {
            c.Validate(); config = c; Engine.Configure(c); Surface.Editing = !c.ClickThrough; Surface.Configure(c);
            Width = Surface.Width; Height = Surface.Height; Left = c.Left; Top = c.Top; Opacity = c.Opacity;
            if (handle != IntPtr.Zero) ApplyStyles();
        }
        private void OnSource(object sender, EventArgs e)
        {
            handle = new WindowInteropHelper(this).Handle; HwndSource.FromHwnd(handle).AddHook(WindowProc); ApplyStyles();
            for (int i = 1; i <= 4; i++) if (!Native.RegisterHotKey(handle, i, 0x4003, (uint)(118 + i))) FailedHotkeys.Add(i);
        }
        private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wp, IntPtr lp, ref bool handled)
        {
            if (message == Native.WM_HOTKEY) { if (Command != null) Command(wp.ToInt32()); handled = true; }
            else if (message == Native.WM_APP_SHOW) { if (Command != null) Command(1); handled = true; }
            else if (message == 0x8013) { if (Command != null) Command(6); handled = true; }
            else if (message == 0x21) { handled = true; return new IntPtr(3); }
            return IntPtr.Zero;
        }
        private void ApplyStyles()
        {
            long style = Native.GetStyle(handle) | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            style = config.ClickThrough ? style | Native.WS_EX_TRANSPARENT : style & ~Native.WS_EX_TRANSPARENT;
            Native.SetStyle(handle, style); Native.SetWindowPos(handle, Native.HWND_TOPMOST, 0, 0, 0, 0, 0x33);
        }
        public void SetClickThrough(bool enabled)
        {
            config.ClickThrough = enabled; Surface.Editing = !enabled; ApplyStyles(); Surface.UpdateFrame(KeyboardHook.Now, true);
        }
        public void Demo() { Surface.DemoStart = KeyboardHook.Now; Surface.DemoUntil = Surface.DemoStart + 4; RequestAnimationFrame(); }
        private void InputArrived()
        {
            // Coalesce notifications on the keyboard thread; never wait for the UI thread.
            if (Interlocked.CompareExchange(ref inputDispatchPending, 1, 0) != 0) return;
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, dispatchInput);
        }
        private void ProcessPendingInput()
        {
            Interlocked.Exchange(ref inputDispatchPending, 0); if (closing || keyboard == null) return;
            inputBatch.Clear(); KeyEvent key;
            while (keyboard.Events.TryDequeue(out key)) inputBatch.Add(key);
            if (inputBatch.Count == 0) return;
            foreach (var item in inputBatch.OrderBy(entry => entry.Time)) Engine.Process(item);
            RequestAnimationFrame();
        }
        public void RequestAnimationFrame()
        {
            if (closing || !IsVisible || rendering) return;
            if (Engine.Trails.Count == 0 && KeyboardHook.Now >= Surface.DemoUntil) return;
            rendering = true; lastRenderingTime = TimeSpan.MinValue; CompositionTarget.Rendering += RenderFrame;
        }
        private void StopRendering()
        {
            if (!rendering) return; CompositionTarget.Rendering -= RenderFrame; rendering = false;
        }
        private void RenderFrame(object sender, EventArgs e)
        {
            var args = (RenderingEventArgs)e;
            if (args.RenderingTime == lastRenderingTime) return;
            lastRenderingTime = args.RenderingTime;
            ProcessPendingInput(); double now = KeyboardHook.Now; Engine.Prune(now); Surface.UpdateFrame(now, false);
            if (Engine.Trails.Count == 0 && now >= Surface.DemoUntil) StopRendering();
        }
        private void Maintenance(object sender, EventArgs e)
        {
            double now = KeyboardHook.Now; ProcessPendingInput(); Engine.Prune(now);
            if (now - lastRecovery >= 0.5) { lastRecovery = now; Engine.Reconcile(now, vk => (Native.GetAsyncKeyState(vk) & 0x8000) != 0); }
            RequestAnimationFrame();
            if (IsVisible && handle != IntPtr.Zero && now - lastTopmost >= 0.75)
            {
                lastTopmost = now;
                if (!Native.SetWindowPos(handle, Native.HWND_TOPMOST, 0, 0, 0, 0, 0x13) && Notice != null) Notice("置顶刷新失败，请尝试重新显示悬浮窗。");
            }
        }
        public void Finish()
        {
            closing = true; maintenance.Stop(); StopRendering();
            if (keyboard != null) keyboard.InputAvailable -= InputArrived;
            for (int i = 1; i <= 4; i++) Native.UnregisterHotKey(handle, i); Close();
        }
    }
}
