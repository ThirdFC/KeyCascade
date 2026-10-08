using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace KeyCascade
{
    public static class PerformanceBench
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Native.EnableDpi();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            string report = args.Length > 0 ? args[0] : "benchmark.txt";
            var times = new List<double>(); var costs = new List<double>();
            using (var host = new AppHost(true))
            {
                host.Overlay.Surface.FrameDrawn += delegate(double time, double cost) { times.Add(time); costs.Add(cost); };
                var area = SystemParameters.WorkArea;
                host.Overlay.Left = area.Left + Math.Max(0, (area.Width - host.Overlay.Width) / 2);
                host.Overlay.Top = Math.Max(area.Top, area.Bottom - host.Overlay.Height - 28);
                host.Overlay.Show();
                var source = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
                int note = 0;
                source.Tick += delegate {
                    double now = KeyboardHook.Now;
                    for (int n = 0; n < 2; n++) {
                        var key = host.Config.Keys[(note++) % host.Config.Keys.Count];
                        host.Overlay.Engine.Process(new KeyEvent(key.KeyCode, true, now - 0.07));
                        host.Overlay.Engine.Process(new KeyEvent(key.KeyCode, false, now - 0.02));
                    }
                    host.Overlay.Surface.DemoStart = now - 1.1; host.Overlay.Surface.DemoUntil = now + 0.2;
                    host.Overlay.RequestAnimationFrame();
                };
                double began = KeyboardHook.Now; double cpuBefore = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
                var end = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                end.Tick += delegate {
                    source.Stop(); end.Stop();
                    double cpu = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds - cpuBefore;
                    var sample = times.Select((t, i) => new { Time = t, Cost = costs[i] }).Where(v => v.Time >= began + 1 && v.Time < began + 5.8).ToList();
                    var intervals = sample.Zip(sample.Skip(1), (a, b) => (b.Time - a.Time) * 1000).OrderBy(v => v).ToArray();
                    var drawCosts = sample.Select(v => v.Cost).OrderBy(v => v).ToArray();
                    double elapsed = sample.Count > 1 ? sample.Last().Time - sample.First().Time : 1;
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report)));
                    File.WriteAllLines(report, new[] {
                        "WPF UI drawing submissions; these are not GPU presentation/monitor frame counters.",
                        "Scenario: seven lanes, 40 completed notes/second plus dense demo trails; first second excluded.",
                        "Frames: " + sample.Count,
                        "Measured seconds: " + elapsed.ToString("F3", CultureInfo.InvariantCulture),
                        "Draw FPS: " + ((sample.Count - 1) / elapsed).ToString("F2", CultureInfo.InvariantCulture),
                        "Frame interval median ms: " + Percentile(intervals, 0.5).ToString("F3", CultureInfo.InvariantCulture),
                        "Frame interval p95 ms: " + Percentile(intervals, 0.95).ToString("F3", CultureInfo.InvariantCulture),
                        "UI drawing median ms: " + Percentile(drawCosts, 0.5).ToString("F3", CultureInfo.InvariantCulture),
                        "UI drawing p95 ms: " + Percentile(drawCosts, 0.95).ToString("F3", CultureInfo.InvariantCulture),
                        "Total process CPU ms over six seconds: " + cpu.ToString("F1", CultureInfo.InvariantCulture),
                        "WPF render tier: " + (RenderCapability.Tier >> 16) });
                    host.Dispose(); app.Shutdown();
                };
                source.Start(); end.Start(); app.Run();
            }
            return 0;
        }
        private static double Percentile(double[] data, double fraction) { return data.Length == 0 ? 0 : data[Math.Min(data.Length - 1, (int)Math.Floor((data.Length - 1) * fraction))]; }
    }
}
