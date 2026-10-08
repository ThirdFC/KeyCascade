using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Media;

namespace KeyCascade
{
    [DataContract]
    public sealed class KeySpec
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Label { get; set; }
        [DataMember] public int KeyCode { get; set; }
        [DataMember] public string Color { get; set; }
        [DataMember] public double Width { get; set; }
        [DataMember] public double Height { get; set; }
        [DataMember] public double OffsetX { get; set; }
        [DataMember] public double OffsetY { get; set; }
        public string Binding { get { return KeyNames.Name(KeyCode); } }
        public KeySpec() { Id = Guid.NewGuid().ToString("N"); Width = 64; Height = 48; Color = "#52D8ED"; }
        public KeySpec Clone() { return (KeySpec)MemberwiseClone(); }
    }

    [DataContract]
    public sealed class AppConfig
    {
        [DataMember] public int Version { get; set; }
        [DataMember] public List<KeySpec> Keys { get; set; }
        [DataMember] public double Left { get; set; }
        [DataMember] public double Top { get; set; }
        [DataMember] public double TrailHeight { get; set; }
        [DataMember] public double Speed { get; set; }
        [DataMember] public double Gap { get; set; }
        [DataMember] public double Opacity { get; set; }
        [DataMember] public double BackgroundOpacity { get; set; }
        [DataMember] public bool ShowCounts { get; set; }
        [DataMember] public bool Fade { get; set; }
        [DataMember] public bool ClickThrough { get; set; }
        public AppConfig()
        {
            Version = 1; Keys = new List<KeySpec>(); Left = 40; Top = 300;
            TrailHeight = 280; Speed = 230; Gap = 8; Opacity = 0.95;
            BackgroundOpacity = 0; ShowCounts = true; Fade = true; ClickThrough = true;
        }
        public static AppConfig Default()
        {
            var c = new AppConfig();
            int[] codes = { 83, 68, 70, 32, 74, 75, 76 };
            string[] labels = { "S", "D", "F", "SPACE", "J", "K", "L" };
            string[] colors = { "#51DCCA", "#F273BF", "#55C8F3", "#ECA5FF", "#55C8F3", "#F273BF", "#F5CC68" };
            for (int i = 0; i < codes.Length; i++) c.Keys.Add(new KeySpec { KeyCode = codes[i], Label = labels[i], Color = colors[i], Width = i == 3 ? 88 : 64 });
            return c;
        }
        public AppConfig Clone()
        {
            var c = (AppConfig)MemberwiseClone(); c.Keys = Keys.Select(k => k.Clone()).ToList(); return c;
        }
        private static bool Range(double v, double min, double max) { return !double.IsNaN(v) && !double.IsInfinity(v) && v >= min && v <= max; }
        public void Validate()
        {
            if (Version != 1) throw new ArgumentException("此配置版本不受支持。");
            if (Keys == null || Keys.Count < 1 || Keys.Count > 32) throw new ArgumentException("按键数量必须为 1–32 个。");
            if (!Range(Left, -100000, 100000) || !Range(Top, -100000, 100000)) throw new ArgumentException("窗口坐标无效。");
            if (!Range(TrailHeight, 40, 1800) || !Range(Speed, 20, 2000) || !Range(Gap, 0, 100)) throw new ArgumentException("瀑布高度应为 40–1800，速度为 20–2000，间距为 0–100。");
            if (!Range(Opacity, 0.15, 1) || !Range(BackgroundOpacity, 0, 1)) throw new ArgumentException("显示透明度应为 15–100%，背景透明度为 0–100%。");
            var ids = new HashSet<string>();
            foreach (var k in Keys)
            {
                if (k == null || string.IsNullOrEmpty(k.Id) || !ids.Add(k.Id)) throw new ArgumentException("按键标识无效或重复。");
                if (k.KeyCode < 1 || k.KeyCode > 254 || k.KeyCode <= 6) throw new ArgumentException("请选择有效的键盘按键。");
                if (string.IsNullOrWhiteSpace(k.Label) || k.Label.Length > 16) throw new ArgumentException("按键标签长度应为 1–16 个字符。");
                if (!Range(k.Width, 20, 300) || !Range(k.Height, 20, 200) || !Range(k.OffsetX, -2000, 2000) || !Range(k.OffsetY, -1000, 1000)) throw new ArgumentException("按键宽度应为 20–300，高度为 20–200，X 偏移为 ±2000，Y 偏移为 ±1000。");
                ParseColor(k.Color);
            }
            for (int i = 0; i < Keys.Count; i++) for (int j = i + 1; j < Keys.Count; j++)
                if (KeyNames.Overlap(Keys[i].KeyCode, Keys[j].KeyCode)) throw new ArgumentException("绑定重复：" + Keys[i].Binding + " 与 " + Keys[j].Binding + "。每个物理键只能绑定一列。");
            if (Layout.Calculate(this).Width > 14000 || Layout.Calculate(this).Height > 6000) throw new ArgumentException("悬浮窗口尺寸过大，请减少按键宽度或偏移。");
        }
        public static Color ParseColor(string value)
        {
            if (value == null || value.Length != 7 || value[0] != '#' || !value.Skip(1).All(Uri.IsHexDigit)) throw new ArgumentException("颜色请使用 #RRGGBB，例如 #52D8ED。");
            return (Color)ColorConverter.ConvertFromString(value);
        }
    }

    public static class ConfigStore
    {
        public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyCascade");
        public static readonly string FilePath = Path.Combine(Folder, "settings.json");
        public static string LoadWarning;
        public static AppConfig Load()
        {
            if (!File.Exists(FilePath)) return AppConfig.Default();
            try { using (var stream = File.OpenRead(FilePath)) { var c = (AppConfig)new DataContractJsonSerializer(typeof(AppConfig)).ReadObject(stream); c.Validate(); return c; } }
            catch (Exception e) { LoadWarning = "配置读取失败，已使用默认配置。原文件尚未更改：" + e.Message; return AppConfig.Default(); }
        }
        public static void Save(AppConfig c, string path)
        {
            c.Validate(); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { new DataContractJsonSerializer(typeof(AppConfig)).WriteObject(stream, c); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static AppConfig Import(string path)
        {
            using (var stream = File.OpenRead(path)) { var c = (AppConfig)new DataContractJsonSerializer(typeof(AppConfig)).ReadObject(stream); c.Validate(); return c; }
        }
    }

    public static class KeyNames
    {
        public static bool Matches(int binding, int vk)
        {
            return binding == vk || (binding == 16 && (vk == 160 || vk == 161)) || (binding == 17 && (vk == 162 || vk == 163)) || (binding == 18 && (vk == 164 || vk == 165));
        }
        public static bool Overlap(int a, int b) { return Enumerable.Range(1, 254).Any(v => Matches(a, v) && Matches(b, v)); }
        public static string Name(int vk)
        {
            if (vk == 32) return "SPACE";
            if (vk >= 65 && vk <= 90 || vk >= 48 && vk <= 57) return ((char)vk).ToString();
            if (vk >= 112 && vk <= 135) return "F" + (vk - 111);
            switch (vk)
            {
                case 16: return "SHIFT"; case 17: return "CTRL"; case 18: return "ALT";
                case 160: return "L SHIFT"; case 161: return "R SHIFT"; case 162: return "L CTRL"; case 163: return "R CTRL"; case 164: return "L ALT"; case 165: return "R ALT";
                case 37: return "←"; case 38: return "↑"; case 39: return "→"; case 40: return "↓";
                case 13: return "ENTER"; case 27: return "ESC"; case 9: return "TAB"; case 8: return "BACK";
            }
            return System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk).ToString().ToUpperInvariant();
        }
    }

    public sealed class Layout
    {
        public List<System.Windows.Rect> Keys = new List<System.Windows.Rect>();
        public double Width, Height;
        public static Layout Calculate(AppConfig c)
        {
            var l = new Layout(); double x = 12;
            foreach (var k in c.Keys) { l.Keys.Add(new System.Windows.Rect(x + k.OffsetX, c.TrailHeight + 12 + k.OffsetY, k.Width, k.Height)); x += k.Width + c.Gap; }
            double minX = Math.Min(0, l.Keys.Min(r => r.Left) - 12);
            double minY = Math.Min(0, l.Keys.Min(r => r.Top) - c.TrailHeight - 12);
            l.Keys = l.Keys.Select(r => new System.Windows.Rect(r.X - minX, r.Y - minY, r.Width, r.Height)).ToList();
            l.Width = l.Keys.Max(r => r.Right) + 12;
            l.Height = l.Keys.Max(r => r.Bottom) + 12;
            return l;
        }
    }
}
