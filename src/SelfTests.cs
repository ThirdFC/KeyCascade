using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KeyCascade
{
    public static class SelfTests
    {
        private static readonly List<string> results = new List<string>();
        private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); results.Add("PASS: " + message); }
        private static void Reject(Action action, string message)
        {
            bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, message);
        }
        public static int Run()
        {
            string report = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-results.txt");
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var c = AppConfig.Default(); c.Validate();
                var engine = new InputEngine(c); var key = c.Keys[0];
                engine.Process(new KeyEvent(key.KeyCode, true, 10));
                engine.Process(new KeyEvent(key.KeyCode, true, 10.01));
                engine.Process(new KeyEvent(key.KeyCode, true, 10.1));
                Check(engine.Count(key.Id) == 1 && engine.Trails.Count == 1 && engine.IsDown(key.Id), "Auto-repeat does not inflate press counts or split a held trail");
                engine.Prune(300); Check(engine.Trails.Count == 1, "A long hold survives trail pruning");
                engine.Process(new KeyEvent(key.KeyCode, false, 301));
                Check(!engine.IsDown(key.Id) && engine.Trails[0].End == 301, "Release finishes the same trail with the release timestamp");
                engine.Prune(302); Check(engine.Trails.Count == 1, "A released trail remains until it leaves the viewport");
                engine.Prune(303); Check(engine.Trails.Count == 0, "Expired released trails are removed");
                foreach (var k in c.Keys) engine.Process(new KeyEvent(k.KeyCode, true, 400));
                Check(c.Keys.All(k => engine.IsDown(k.Id)), "All seven keys can be held simultaneously");
                engine.Reconcile(401, vk => false); Check(c.Keys.All(k => !engine.IsDown(k.Id)), "Missed releases are recovered without a stuck key");
                engine.ResetCounts(); Check(c.Keys.All(k => engine.Count(k.Id) == 0), "Count reset works independently of trail lifetime");
                var dualInput = new InputEngine(c);
                dualInput.Process(new KeyEvent(key.KeyCode, true, 1)); dualInput.Process(new KeyEvent(key.KeyCode, true, 1.0001));
                dualInput.Process(new KeyEvent(key.KeyCode, false, 1.05)); dualInput.Process(new KeyEvent(key.KeyCode, true, 1.08));
                dualInput.Process(new KeyEvent(key.KeyCode, false, 1.0501));
                Check(dualInput.Count(key.Id) == 2 && dualInput.IsDown(key.Id), "Dual input paths deduplicate presses and ignore a stale release after a new press");
                var sameTick = new InputEngine(c);
                sameTick.Process(new KeyEvent(key.KeyCode, true, 1, 1)); sameTick.Process(new KeyEvent(key.KeyCode, false, 1, 1));
                sameTick.Process(new KeyEvent(key.KeyCode, true, 1, 2)); sameTick.Process(new KeyEvent(key.KeyCode, false, 1, 2));
                Check(sameTick.Count(key.Id) == 1 && !sameTick.IsDown(key.Id), "Same-timestamp down/up duplicates from the second input path do not create a second tap");

                var modifierConfig = AppConfig.Default(); modifierConfig.Keys = new List<KeySpec> { new KeySpec { KeyCode = 16, Label = "SHIFT" } };
                var modifiers = new InputEngine(modifierConfig); string modifierId = modifierConfig.Keys[0].Id;
                modifiers.Process(new KeyEvent(160, true, 1)); modifiers.Process(new KeyEvent(161, true, 1.1)); modifiers.Process(new KeyEvent(160, false, 1.2));
                Check(modifiers.IsDown(modifierId) && modifiers.Count(modifierId) == 1, "A generic modifier stays held while either physical side is down");
                modifiers.Process(new KeyEvent(161, false, 1.3)); Check(!modifiers.IsDown(modifierId), "Generic modifier releases when both sides release");
                Check(!KeyNames.Overlap(160, 161) && KeyNames.Overlap(16, 160), "Left/right modifier binding collision detection");

                var bad = c.Clone(); bad.Keys[1].KeyCode = bad.Keys[0].KeyCode; Reject(bad.Validate, "Duplicate physical bindings are rejected");
                bad = c.Clone(); bad.Speed = double.NaN; Reject(bad.Validate, "Non-finite configuration numbers are rejected");
                bad = c.Clone(); bad.Keys[0].Color = "#NOTHEX"; Reject(bad.Validate, "Invalid lane colors are rejected");
                bad = c.Clone(); bad.Keys.Clear(); Reject(bad.Validate, "An empty key layout is rejected");
                bad = c.Clone(); bad.Keys = Enumerable.Range(0, 33).Select(i => new KeySpec { KeyCode = 65, Label = "A" }).ToList(); Reject(bad.Validate, "More than 32 lanes are rejected");
                var offset = c.Clone(); offset.Keys[0].OffsetX = -180; offset.Keys[0].OffsetY = -130;
                var layout = Layout.Calculate(offset); Check(layout.Keys.All(r => r.Left >= 0 && r.Top >= offset.TrailHeight) && layout.Width >= layout.Keys.Max(r => r.Right), "Negative per-key offsets produce an unclipped window layout");
                var tempDir = Path.Combine(Path.GetTempPath(), "KeyCascade-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    string path = Path.Combine(tempDir, "settings.json"); c.Left = -1200; c.Top = -250; c.Keys[0].Label = "中文键";
                    ConfigStore.Save(c, path); var loaded = ConfigStore.Import(path);
                    Check(loaded.Left == -1200 && loaded.Keys[0].Label == "中文键" && loaded.Keys.Count == 7, "Configuration round-trips Chinese labels and negative monitor coordinates");
                    c.Speed = 480; ConfigStore.Save(c, path);
                    Check(ConfigStore.Import(path).Speed == 480 && File.Exists(path + ".bak") && !File.Exists(path + ".tmp"), "Saving replaces the original atomically and retains a backup");
                }
                finally { Directory.Delete(tempDir, true); }

                using (var hook = new KeyboardHook())
                {
                    uint deviceCount = 0; uint deviceSize = (uint)Marshal.SizeOf(typeof(Native.RawDevice));
                    Native.GetRegisteredRawInputDevices(null, ref deviceCount, deviceSize);
                    var devices = new Native.RawDevice[deviceCount]; Native.GetRegisteredRawInputDevices(devices, ref deviceCount, deviceSize);
                    Check(devices.Any(d => d.usagePage == 1 && d.usage == 6 && d.flags == 0x100 && d.target == hook.RawWindow), "Hardware keyboard Raw Input is registered as a background input sink");
                    var inputConfig = AppConfig.Default(); inputConfig.Keys = new List<KeySpec> { new KeySpec { KeyCode = 135, Label = "F24" } }; var input = new InputEngine(inputConfig);
                    try
                    {
                        SendF24(false); WaitEvent(hook, input, true);
                        Check(input.IsDown(inputConfig.Keys[0].Id) && input.Count(inputConfig.Keys[0].Id) == 1, "Real Win32 keyboard hook receives SendInput key-down");
                        Check((Native.GetAsyncKeyState(135) & 0x8000) != 0, "Keyboard hook passes input through to Windows without swallowing it");
                        SendF24(true); WaitEvent(hook, input, false);
                        Check(!input.IsDown(inputConfig.Keys[0].Id) && input.Trails[0].End.HasValue, "Real Win32 keyboard hook receives key-up");
                    }
                    finally { SendF24(true); }
                    var liveOverlay = new OverlayWindow(inputConfig, hook);
                    try
                    {
                        SendF24(false);
                        PumpUntil(() => liveOverlay.Engine.Count(inputConfig.Keys[0].Id) == 1, 2);
                        Check(liveOverlay.Engine.IsDown(inputConfig.Keys[0].Id), "Keyboard notification dispatches a key-down into the overlay engine without frame polling");
                        SendF24(true); PumpUntil(() => !liveOverlay.Engine.IsDown(inputConfig.Keys[0].Id), 2);
                        Check(!liveOverlay.Engine.IsDown(inputConfig.Keys[0].Id), "Keyboard notification dispatches the matching release into the overlay engine");
                    }
                    finally { SendF24(true); liveOverlay.Finish(); }
                }
                using (var host = new AppHost(true))
                {
                    var hwnd = new WindowInteropHelper(host.Overlay).EnsureHandle();
                    long style = Native.GetStyle(hwnd);
                    Check((style & Native.WS_EX_TRANSPARENT) != 0 && (style & Native.WS_EX_NOACTIVATE) != 0 && (style & 0x8) != 0, "Native overlay is topmost, click-through and non-activating");
                    host.Overlay.SetClickThrough(false); style = Native.GetStyle(hwnd);
                    Check((style & Native.WS_EX_TRANSPARENT) == 0 && (style & Native.WS_EX_NOACTIVATE) != 0 && (style & 0x8) != 0, "Edit mode accepts the mouse and retains topmost/non-activating styles");
                    host.Overlay.SetClickThrough(true); Check((Native.GetStyle(hwnd) & Native.WS_EX_TRANSPARENT) != 0, "Click-through can be restored at runtime");
                    host.Settings.Load(c); Check(host.Settings.Window.Content != null, "Chinese WPF settings editor and embedded XAML load successfully");
                    var surface = host.Overlay.Surface;
                    long labelsBefore = surface.LabelLayoutCount, countersBefore = surface.CounterLayoutCount;
                    host.Overlay.Engine.Process(new KeyEvent(host.Config.Keys[0].KeyCode, true, 10));
                    surface.UpdateFrame(10, false);
                    long afterPress = surface.CounterLayoutCount;
                    for (int frame = 1; frame <= 240; frame++) surface.UpdateFrame(10 + frame / 240.0, false);
                    Check(surface.LabelLayoutCount == labelsBefore && surface.CounterLayoutCount == afterPress && afterPress == countersBefore + 1, "240 animation updates retain labels and rebuild only the changed counter once");
                    host.Overlay.Engine.ResetCounts(); surface.UpdateFrame(11, true);
                    Check(surface.CounterLayoutCount == afterPress + 1, "Count reset refreshes cached counter text");
                    host.Overlay.Show(); host.Overlay.RequestAnimationFrame(); Check(host.Overlay.AnimationActive, "A visible active trail starts the composition rendering clock");
                    host.Overlay.Hide(); Check(!host.Overlay.AnimationActive, "Hiding the overlay detaches the animation clock");
                    host.Overlay.Engine.ReleaseAll(11); host.Overlay.Engine.Prune(20); host.Overlay.Show();
                    Check(!host.Overlay.AnimationActive, "An idle overlay does not run a continuous animation loop");
                    host.Overlay.Demo(); Check(host.Overlay.AnimationActive, "Demo playback wakes the animation clock");
                    host.Overlay.Hide();
                }
                File.WriteAllLines(report, results.Concat(new[] { "ALL CHECKS PASSED" })); return 0;
            }
            catch (Exception e) { results.Add(e.ToString()); File.WriteAllLines(report, results); return 1; }
        }
        private static void WaitEvent(KeyboardHook hook, InputEngine engine, bool down)
        {
            double deadline = KeyboardHook.Now + 2;
            var received = new List<string>();
            while (KeyboardHook.Now < deadline)
            {
                KeyEvent e;
                while (hook.Events.TryDequeue(out e)) { received.Add(e.Code + ":" + e.Down); engine.Process(e); if (e.Code == 135 && e.Down == down) return; }
                Native.Msg message;
                while (Native.PeekMessage(out message, IntPtr.Zero, 0, 0, 1)) { Native.TranslateMessage(ref message); Native.DispatchMessage(ref message); }
                Thread.Sleep(10);
            }
            throw new TimeoutException("Global hook did not receive F24 " + (down ? "down" : "up") + "; received=" + string.Join(",", received) + "; INPUT size=" + Marshal.SizeOf(typeof(Input)) + "; union offset=" + Marshal.OffsetOf(typeof(Input), "data"));
        }
        private static void PumpUntil(Func<bool> predicate, double seconds)
        {
            double deadline = KeyboardHook.Now + seconds;
            while (!predicate())
            {
                if (KeyboardHook.Now > deadline) throw new TimeoutException("UI input notification did not arrive.");
                Native.Msg message;
                while (Native.PeekMessage(out message, IntPtr.Zero, 0, 0, 1)) { Native.TranslateMessage(ref message); Native.DispatchMessage(ref message); }
                Thread.Sleep(2);
            }
        }
        [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Explicit, Size = 32)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput keyboard; }
        [StructLayout(LayoutKind.Sequential)] private struct Input { public uint type; public InputUnion data; }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        private static void SendF24(bool release)
        {
            var input = new Input { type = 1, data = new InputUnion { keyboard = new KeyboardInput { vk = 135, flags = release ? 2u : 0u } } };
            if (SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Input))) != 1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public static int RenderPreview(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory); var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                using (var host = new AppHost(true))
                {
                    host.Settings.Window.MaxHeight = double.PositiveInfinity;
                    Render((FrameworkElement)host.Settings.Window.Content, 1140, 820, Path.Combine(directory, "settings.png"));
                    var surface = host.Overlay.Surface; surface.RenderTime = 100; surface.DemoStart = 98.9; surface.DemoUntil = 102.9;
                    foreach (var k in host.Config.Keys) for (int i = 0; i < 16; i++) { host.Overlay.Engine.Process(new KeyEvent(k.KeyCode, true, 90 + i * 0.1)); host.Overlay.Engine.Process(new KeyEvent(k.KeyCode, false, 90.05 + i * 0.1)); }
                    host.Overlay.Engine.Prune(100); host.Overlay.Engine.Process(new KeyEvent(host.Config.Keys[3].KeyCode, true, 99.6));
                    surface.UpdateFrame(100, true);
                    Render(surface, surface.Width, surface.Height, Path.Combine(directory, "overlay.png"));
                }
                return 0;
            }
            catch (Exception e) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "render-error.txt"), e.ToString()); return 1; }
        }
        public static void CaptureLive(AppHost host, string directory)
        {
            Directory.CreateDirectory(directory);
            var content = (FrameworkElement)host.Settings.Window.Content;
            Render(content, content.ActualWidth > 0 ? content.ActualWidth : 1140, content.ActualHeight > 0 ? content.ActualHeight : 820, Path.Combine(directory, "settings.png"));
            Render(host.Overlay.Surface, host.Overlay.Surface.ActualWidth, host.Overlay.Surface.ActualHeight, Path.Combine(directory, "overlay.png"));
            var hwnd = new WindowInteropHelper(host.Overlay).Handle;
            var style = Native.GetStyle(hwnd);
            File.WriteAllLines(Path.Combine(directory, "startup-check.txt"), new[] {
                "Overlay visible: " + host.Overlay.IsVisible,
                "Settings visible: " + host.Settings.Window.IsVisible,
                "Topmost: " + ((style & 0x8) != 0),
                "Click-through: " + ((style & Native.WS_EX_TRANSPARENT) != 0),
                "No activate: " + ((style & Native.WS_EX_NOACTIVATE) != 0),
                "Unavailable hotkeys: " + host.Overlay.FailedHotkeys.Count,
                "Settings size (DIP): " + content.ActualWidth + " x " + content.ActualHeight });
        }
        private static void Render(FrameworkElement element, double width, double height, string path)
        {
            element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(path)) png.Save(stream);
        }
    }
}
