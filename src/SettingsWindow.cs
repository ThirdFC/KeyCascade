using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace KeyCascade
{
    public sealed class SettingsController
    {
        public Window Window { get; private set; }
        private readonly AppHost host;
        private ObservableCollection<KeySpec> rows;
        private DataGrid grid;
        private bool exiting;
        private int draftVersion;
        private readonly System.Windows.Threading.DispatcherTimer frameStatusTimer;
        private long submittedFrames, previousFrames;
        private double previousFrameSample;
        public SettingsController(AppHost host)
        {
            this.host = host;
            using (var stream = typeof(SettingsController).Assembly.GetManifestResourceStream("Settings.xaml")) Window = (Window)XamlReader.Load(stream);
            Window.MaxHeight = SystemParameters.WorkArea.Height;
            Window.Height = Math.Min(Window.Height, SystemParameters.WorkArea.Height - 40);
            grid = Find<DataGrid>("KeysGrid");
            Load(host.Config);
            Button("ApplyButton", delegate { Apply(); });
            Button("HideSettingsButton", delegate { Window.Hide(); });
            Button("VisibilityButton", delegate { host.ToggleVisible(); UpdateState(); });
            Button("PreviewButton", delegate { if (Apply()) { host.Overlay.Show(); host.Overlay.Demo(); UpdateState(); } });
            Button("AddButton", Add);
            Button("BindButton", Bind);
            Button("ColorButton", PickColor);
            Button("DeleteButton", delegate { if (rows.Count > 1 && Selected != null) { int i = grid.SelectedIndex; rows.Remove(Selected); grid.SelectedIndex = Math.Min(i, rows.Count - 1); UpdateCount(); } else Message("至少保留一个按键。", true); });
            Button("UpButton", delegate { Move(-1); }); Button("DownButton", delegate { Move(1); });
            Button("FourKeyButton", delegate { Preset(4); }); Button("SevenKeyButton", delegate { Preset(7); }); Button("EightKeyButton", delegate { Preset(8); });
            Button("ResetButton", delegate { Load(AppConfig.Default()); Message("默认布局已载入，点击应用并保存生效。", false); });
            Button("PlaceButton", Place);
            Button("DragButton", delegate { if (Apply()) { host.SetClickThrough(false); host.Overlay.Show(); UpdateState(); Message("拖动悬浮窗任意位置；完成后按 Ctrl+Alt+F9 锁定。", false); } });
            Button("ResetCountsButton", delegate { host.ResetCounts(); Message("按键计数已清零。", false); });
            Button("ImportButton", Import); Button("ExportButton", Export);
            Button("ExitButton", delegate { host.Exit(); });
            Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!exiting) { e.Cancel = true; Window.Hide(); } };
            if (!string.IsNullOrEmpty(ConfigStore.LoadWarning)) Message(ConfigStore.LoadWarning, true);
            host.Overlay.Surface.FrameDrawn += OnFrameDrawn;
            previousFrameSample = KeyboardHook.Now;
            frameStatusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            frameStatusTimer.Tick += delegate
            {
                double now = KeyboardHook.Now; double fps = (submittedFrames - previousFrames) / Math.Max(0.001, now - previousFrameSample);
                previousFrames = submittedFrames; previousFrameSample = now;
                if (Window.IsVisible) Find<TextBlock>("FrameStatus").Text = host.Overlay.AnimationActive ? "动画刷新：跟随屏幕 · 绘制约 " + fps.ToString("0") + " FPS" : "动画刷新：跟随屏幕 · 待机";
            };
            frameStatusTimer.Start();
        }
        private void OnFrameDrawn(double time, double cost) { submittedFrames++; }
        private T Find<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }
        private void Button(string name, Action action) { Find<Button>(name).Click += delegate { try { action(); } catch (Exception e) { Message(e.Message, true); } }; }
        private KeySpec Selected { get { return grid.SelectedItem as KeySpec; } }
        private void Set(string name, double value) { Find<TextBox>(name).Text = value.ToString("0.##", CultureInfo.CurrentCulture); }
        private double Number(string name, string label)
        {
            double v; if (!double.TryParse(Find<TextBox>(name).Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v) || double.IsInfinity(v) || double.IsNaN(v)) throw new ArgumentException(label + "必须是有效数字。"); return v;
        }
        public void Load(AppConfig config)
        {
            draftVersion = config.Version;
            rows = new ObservableCollection<KeySpec>(config.Keys.Select(k => k.Clone())); grid.ItemsSource = rows; grid.SelectedIndex = 0;
            Set("LeftBox", config.Left); Set("TopBox", config.Top); Set("TrailBox", config.TrailHeight); Set("SpeedBox", config.Speed); Set("GapBox", config.Gap); Set("OpacityBox", config.Opacity * 100); Set("BackgroundBox", config.BackgroundOpacity * 100);
            Find<CheckBox>("CountCheck").IsChecked = config.ShowCounts; Find<CheckBox>("ThroughCheck").IsChecked = config.ClickThrough; Find<CheckBox>("FadeCheck").IsChecked = config.Fade;
            UpdateCount(); UpdateState();
        }
        private bool HasErrors(DependencyObject obj)
        {
            if (Validation.GetHasError(obj)) return true;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++) if (HasErrors(VisualTreeHelper.GetChild(obj, i))) return true;
            return false;
        }
        private AppConfig Read()
        {
            if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true) || HasErrors(grid)) throw new ArgumentException("请修正表格中的无效值；宽度、高度、偏移必须是数字。");
            var c = new AppConfig { Version = draftVersion, Keys = rows.Select(k => k.Clone()).ToList(), Left = Number("LeftBox", "X 坐标"), Top = Number("TopBox", "Y 坐标"), TrailHeight = Number("TrailBox", "瀑布高度"), Speed = Number("SpeedBox", "上升速度"), Gap = Number("GapBox", "间距"), Opacity = Number("OpacityBox", "显示透明度") / 100, BackgroundOpacity = Number("BackgroundBox", "背景透明度") / 100, ShowCounts = Find<CheckBox>("CountCheck").IsChecked == true, Fade = Find<CheckBox>("FadeCheck").IsChecked == true, ClickThrough = Find<CheckBox>("ThroughCheck").IsChecked == true };
            c.Validate(); return c;
        }
        public bool Apply()
        {
            try { var c = Read(); host.Apply(c); UpdateState(); Message("已应用并保存 · " + c.Keys.Count + " 键", false); return true; }
            catch (Exception e) { Message(e.Message, true); return false; }
        }
        public void Message(string message, bool error)
        {
            var text = Find<TextBlock>("MessageText"); text.Text = message; text.Foreground = new SolidColorBrush(error ? Color.FromRgb(255, 157, 162) : Color.FromRgb(138, 222, 199));
        }
        public void UpdatePosition(double x, double y) { Set("LeftBox", x); Set("TopBox", y); }
        public void UpdateState()
        {
            if (host.Overlay == null) return;
            Find<Button>("VisibilityButton").Content = host.Overlay.IsVisible ? "隐藏悬浮窗" : "显示悬浮窗";
            Find<Button>("DragButton").Content = host.Config.ClickThrough ? "启用拖动模式" : "拖动模式已开启";
            Find<TextBlock>("LiveStatus").Text = host.Overlay.IsVisible ? (host.Config.ClickThrough ? "●  全局监听已启动 · 置顶 + 鼠标穿透" : "●  全局监听已启动 · 置顶 + 拖动模式") : "●  全局监听已启动 · 悬浮窗已隐藏";
        }
        public void SyncThrough(bool value) { Find<CheckBox>("ThroughCheck").IsChecked = value; UpdateState(); }
        private void UpdateCount() { Find<TextBlock>("KeyCount").Text = rows.Count + " / 32 键"; }
        private void Add()
        {
            if (rows.Count >= 32) { Message("最多支持 32 个按键。", true); return; }
            int[] candidates = Enumerable.Range(65, 26).Concat(Enumerable.Range(48, 10)).Concat(new[] { 32, 37, 38, 39, 40 }).ToArray();
            int vk = candidates.FirstOrDefault(code => !rows.Any(k => KeyNames.Overlap(k.KeyCode, code)));
            if (vk == 0) vk = Enumerable.Range(112, 24).First(code => !rows.Any(k => KeyNames.Overlap(k.KeyCode, code)));
            var item = new KeySpec { KeyCode = vk, Label = KeyNames.Name(vk), Color = "#65E2CD" };
            rows.Add(item); grid.SelectedItem = item; grid.ScrollIntoView(item); UpdateCount();
        }
        private void Bind()
        {
            if (Selected == null) { Message("请先选中一行按键。", true); return; }
            var k = Selected; int? result = CaptureKey(Window);
            if (!result.HasValue) return;
            if (rows.Any(other => other != k && KeyNames.Overlap(other.KeyCode, result.Value))) { Message("该按键已绑定到其他列，请选择其他按键。", true); return; }
            if (k.Label == KeyNames.Name(k.KeyCode)) k.Label = KeyNames.Name(result.Value);
            k.KeyCode = result.Value; grid.Items.Refresh(); Message("已录入 " + k.Binding + "，点击应用并保存生效。", false);
        }
        public static int? CaptureKey(Window owner)
        {
            var dialog = new Window { Title = "录入按键", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 420, Height = 205, ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(21, 31, 43)), Foreground = Brushes.White, FontFamily = new FontFamily("Microsoft YaHei UI"), ShowInTaskbar = false };
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "请按下要绑定的键", FontSize = 21 });
            panel.Children.Add(new TextBlock { Text = "支持字母、空格、方向键、功能键及左右修饰键。", Margin = new Thickness(0, 14, 0, 18), Foreground = Brushes.LightSteelBlue, TextWrapping = TextWrapping.Wrap });
            var cancel = new Button { Content = "取消", Width = 80, Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Right }; cancel.Click += delegate { dialog.Close(); }; panel.Children.Add(cancel); dialog.Content = panel;
            int? value = null;
            dialog.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key; if (key == Key.ImeProcessed) key = e.ImeProcessedKey;
                int vk = KeyInterop.VirtualKeyFromKey(key); if (vk < 7 || vk > 254) return;
                value = vk; e.Handled = true; dialog.Close();
            };
            dialog.ShowDialog(); return value;
        }
        private void PickColor()
        {
            if (Selected == null) { Message("请先选中一行按键。", true); return; }
            using (var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true })
            {
                try { var c = AppConfig.ParseColor(Selected.Color); dialog.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B); } catch (ArgumentException) { }
                if (dialog.ShowDialog(new Win32Owner(new WindowInteropHelper(Window).Handle)) == System.Windows.Forms.DialogResult.OK) { Selected.Color = "#" + dialog.Color.R.ToString("X2") + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2"); grid.Items.Refresh(); }
            }
        }
        private void Move(int delta)
        {
            int i = grid.SelectedIndex; if (i < 0 || i + delta < 0 || i + delta >= rows.Count) return;
            rows.Move(i, i + delta); grid.SelectedIndex = i + delta;
        }
        private void Preset(int count)
        {
            var c = AppConfig.Default();
            if (count == 4) { c.Keys = c.Keys.Where(k => new[] { 68, 70, 74, 75 }.Contains(k.KeyCode)).ToList(); }
            if (count == 8)
            {
                int[] keys = { 65, 83, 68, 70, 74, 75, 76, 186 }; string[] colors = { "#51DCCA", "#F273BF", "#55C8F3", "#ECA5FF", "#ECA5FF", "#55C8F3", "#F273BF", "#F5CC68" };
                c.Keys.Clear(); for (int i = 0; i < keys.Length; i++) c.Keys.Add(new KeySpec { KeyCode = keys[i], Label = keys[i] == 186 ? ";" : KeyNames.Name(keys[i]), Color = colors[i] });
            }
            rows = new ObservableCollection<KeySpec>(c.Keys); grid.ItemsSource = rows; grid.SelectedIndex = 0; UpdateCount(); Message(count + " 键布局已载入，点击应用并保存生效。", false);
        }
        private void Place()
        {
            var c = Read(); var layout = Layout.Calculate(c);
            // Convert the monitor containing this editor from physical pixels to WPF DIP.
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(Window).Handle);
            var source = PresentationSource.FromVisual(Window); var transform = source.CompositionTarget.TransformFromDevice;
            var topLeft = transform.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
            var bottomRight = transform.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
            Set("LeftBox", Math.Round(topLeft.X + Math.Max(0, (bottomRight.X - topLeft.X - layout.Width) / 2)));
            Set("TopBox", Math.Round(Math.Max(topLeft.Y, bottomRight.Y - layout.Height - 28)));
            Message("已调整为当前屏幕底部居中，点击应用并保存生效。", false);
        }
        private void Import()
        {
            var dialog = new OpenFileDialog { Title = "导入 KeyCascade 配置", Filter = "JSON 配置 (*.json)|*.json" };
            if (dialog.ShowDialog(Window) == true) { Load(ConfigStore.Import(dialog.FileName)); Message("配置已载入，点击应用并保存生效。", false); }
        }
        private void Export()
        {
            var c = Read(); var dialog = new SaveFileDialog { Title = "导出 KeyCascade 配置", Filter = "JSON 配置 (*.json)|*.json", FileName = "KeyCascade-config.json" };
            if (dialog.ShowDialog(Window) == true) { ConfigStore.Save(c, dialog.FileName); Message("配置已导出。", false); }
        }
        public void Show() { UpdateState(); Window.Show(); Window.WindowState = WindowState.Normal; Window.Activate(); }
        public void Finish() { exiting = true; frameStatusTimer.Stop(); host.Overlay.Surface.FrameDrawn -= OnFrameDrawn; Window.Close(); }
        private sealed class Win32Owner : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            public Win32Owner(IntPtr handle) { Handle = handle; }
        }
    }
}
