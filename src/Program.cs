using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace KeyCascade
{
    public sealed class AppHost : IDisposable
    {
        public AppConfig Config { get; private set; }
        public OverlayWindow Overlay { get; private set; }
        public SettingsController Settings { get; private set; }
        private KeyboardHook keyboard;
        private System.Windows.Forms.NotifyIcon tray;
        private bool disposed;
        public AppHost(bool testMode, bool showSettings = true, bool showOverlay = true)
        {
            Config = testMode ? AppConfig.Default() : ConfigStore.Load();
            if (!testMode && !File.Exists(ConfigStore.FilePath))
            {
                var layout = Layout.Calculate(Config); var area = SystemParameters.WorkArea;
                Config.Left = Math.Max(area.Left, area.Left + (area.Width - layout.Width) / 2);
                Config.Top = Math.Max(area.Top, area.Bottom - layout.Height - 28);
            }
            if (!testMode) keyboard = new KeyboardHook();
            Overlay = new OverlayWindow(Config, keyboard);
            Overlay.Command += HandleCommand;
            Overlay.Moved += delegate(double x, double y)
            {
                Config.Left = x; Config.Top = y;
                try { ConfigStore.Save(Config, ConfigStore.FilePath); if (Settings != null) { Settings.UpdatePosition(x, y); Settings.Message("位置已自动保存。", false); } }
                catch (Exception e) { if (Settings != null) Settings.Message("位置保存失败：" + e.Message, true); }
            };
            Overlay.Notice += delegate(string s) { if (Settings != null) Settings.Message(s, true); };
            if (!testMode && showOverlay) Overlay.Show();
            Settings = new SettingsController(this);
            if (!testMode)
            {
                CreateTray(); if (showSettings) Settings.Show();
                if (Overlay.FailedHotkeys.Count > 0) Settings.Message("部分快捷键被其他软件占用。请通过托盘图标操作：" + string.Join("、", Overlay.FailedHotkeys.Select(i => "Ctrl+Alt+F" + (7 + i))), true);
            }
        }
        private void CreateTray()
        {
            tray = new System.Windows.Forms.NotifyIcon { Text = "KeyCascade", Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location), Visible = true };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("打开设置", null, delegate { Settings.Show(); });
            menu.Items.Add("显示 / 隐藏悬浮窗", null, delegate { ToggleVisible(); Settings.UpdateState(); });
            menu.Items.Add("鼠标穿透 / 拖动模式", null, delegate { SetClickThrough(!Config.ClickThrough); });
            menu.Items.Add("清零计数", null, delegate { ResetCounts(); });
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator()); menu.Items.Add("退出 KeyCascade", null, delegate { Exit(); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { Settings.Show(); };
        }
        public void Apply(AppConfig c)
        {
            ConfigStore.Save(c, ConfigStore.FilePath); Config = c; Overlay.Apply(c);
        }
        public void ToggleVisible() { if (Overlay.IsVisible) Overlay.Hide(); else Overlay.Show(); }
        public void SetClickThrough(bool enabled)
        {
            Overlay.SetClickThrough(enabled); Config.ClickThrough = enabled;
            if (Settings != null) Settings.SyncThrough(enabled);
            try { ConfigStore.Save(Config, ConfigStore.FilePath); }
            catch (Exception e) { if (Settings != null) Settings.Message("穿透已切换，但保存失败：" + e.Message, true); }
        }
        public void ResetCounts() { Overlay.Engine.ResetCounts(); Overlay.Surface.InvalidateVisual(); }
        private void HandleCommand(int id)
        {
            if (Settings == null) return;
            switch (id)
            {
                case 1: Settings.Show(); break;
                case 2: SetClickThrough(!Config.ClickThrough); break;
                case 3: ToggleVisible(); Settings.UpdateState(); break;
                case 4: ResetCounts(); Settings.Message("按键计数已清零。", false); break;
                case 6: Exit(); break;
            }
        }
        public void Exit() { Dispose(); Application.Current.Shutdown(); }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (tray != null) { tray.Visible = false; var icon = tray.Icon; var menu = tray.ContextMenuStrip; tray.Dispose(); if (icon != null) icon.Dispose(); if (menu != null) menu.Dispose(); }
            if (Settings != null) Settings.Finish(); if (Overlay != null) Overlay.Finish(); if (keyboard != null) keyboard.Dispose();
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Native.EnableDpi();
            if (args.Contains("--self-test")) return SelfTests.Run();
            if (args.Contains("--render-preview")) return SelfTests.RenderPreview(args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview"));
            if (args.Contains("--quit"))
            {
                var current = FindRunningOverlay();
                return current == IntPtr.Zero || Native.PostMessage(current, 0x8013, IntPtr.Zero, IntPtr.Zero) ? 0 : 1;
            }
            bool created;
            using (var mutex = new Mutex(true, "Local\\KeyCascade.KeyboardWaterfall.v1", out created))
            {
                if (!created)
                {
                    var hwnd = FindRunningOverlay();
                    if (hwnd != IntPtr.Zero) Native.PostMessage(hwnd, Native.WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                    {
                        Log(e.Exception); MessageBox.Show("运行出现错误：" + e.Exception.Message + "\n详情已写入 " + Path.Combine(ConfigStore.Folder, "error.log"), "KeyCascade", MessageBoxButton.OK, MessageBoxImage.Error);
                        e.Handled = true; app.Shutdown(1);
                    };
                    using (var host = new AppHost(false, !args.Contains("--quiet"), !args.Contains("--overlay-hidden")))
                    {
                        if (args.Contains("--capture-preview"))
                        {
                            var captureTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                            captureTimer.Tick += delegate { captureTimer.Stop(); SelfTests.CaptureLive(host, Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "preview-live"))); };
                            captureTimer.Start();
                        }
                        app.Run();
                    }
                    return 0;
                }
                catch (Exception e) { Log(e); MessageBox.Show("启动失败：" + e.Message, "KeyCascade", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
                finally { mutex.ReleaseMutex(); }
            }
        }
        private static IntPtr FindRunningOverlay()
        {
            var hwnd = Native.FindWindow(null, OverlayWindow.WindowTitle);
            if (hwnd == IntPtr.Zero) hwnd = Native.FindWindow(null, "KeyCascade · 按键瀑布流 Overlay");
            return hwnd;
        }
        public static void Log(Exception e) { try { Directory.CreateDirectory(ConfigStore.Folder); File.AppendAllText(Path.Combine(ConfigStore.Folder, "error.log"), DateTime.Now.ToString("s") + " " + e + Environment.NewLine); } catch { } }
    }
}
