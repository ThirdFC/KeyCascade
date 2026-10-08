using System;
using System.Collections.Generic;
using System.Linq;

namespace KeyCascade
{
    public sealed class Trail
    {
        public string KeyId; public double Start; public double? End;
    }
    public sealed class InputEngine
    {
        private readonly HashSet<int> physicalDown = new HashSet<int>();
        private readonly Dictionary<int, double> lastEvent = new Dictionary<int, double>();
        private readonly Dictionary<int, Queue<KeyEvent>> pathHistory = new Dictionary<int, Queue<KeyEvent>>();
        private readonly Dictionary<string, Trail> active = new Dictionary<string, Trail>();
        private readonly Dictionary<string, long> counts = new Dictionary<string, long>();
        private readonly List<Trail> trails = new List<Trail>();
        private AppConfig config;
        public InputEngine(AppConfig config) { Configure(config); }
        public void Configure(AppConfig value)
        {
            config = value.Clone(); physicalDown.Clear(); lastEvent.Clear(); pathHistory.Clear(); active.Clear(); trails.Clear();
            var valid = new HashSet<string>(config.Keys.Select(k => k.Id));
            foreach (var id in counts.Keys.ToList()) if (!valid.Contains(id)) counts.Remove(id);
            foreach (var k in config.Keys) if (!counts.ContainsKey(k.Id)) counts[k.Id] = 0;
        }
        public void Process(KeyEvent e)
        {
            if (e.Source != 0)
            {
                Queue<KeyEvent> history;
                if (!pathHistory.TryGetValue(e.Code, out history)) { history = new Queue<KeyEvent>(); pathHistory[e.Code] = history; }
                if (history.Any(p => p.Source != e.Source && p.Time == e.Time && p.Down == e.Down)) return;
                history.Enqueue(e); while (history.Count > 8) history.Dequeue();
            }
            double previous;
            // A delayed duplicate must never release a subsequent press of the same physical key.
            if (lastEvent.TryGetValue(e.Code, out previous) && e.Time < previous - 0.0002) return;
            lastEvent[e.Code] = Math.Max(e.Time, previous);
            if (e.Down) { if (!physicalDown.Add(e.Code)) return; } else { if (!physicalDown.Remove(e.Code)) return; }
            foreach (var k in config.Keys)
            {
                if (!KeyNames.Matches(k.KeyCode, e.Code)) continue;
                if (e.Down)
                {
                    if (active.ContainsKey(k.Id)) continue;
                    var trail = new Trail { KeyId = k.Id, Start = e.Time };
                    active[k.Id] = trail; trails.Add(trail); counts[k.Id]++;
                }
                else if (!physicalDown.Any(v => KeyNames.Matches(k.KeyCode, v)))
                {
                    Trail trail; if (active.TryGetValue(k.Id, out trail)) { trail.End = Math.Max(trail.Start, e.Time); active.Remove(k.Id); }
                }
            }
        }
        public void Prune(double now)
        {
            double lifetime = config.TrailHeight / config.Speed + 0.1;
            trails.RemoveAll(t => t.End.HasValue && now - t.End.Value > lifetime);
        }
        public void Reconcile(double now, Func<int, bool> isDown)
        {
            // Recover a missing release after lock/sleep or a missed OS hook callback.
            foreach (int vk in physicalDown.ToList()) if (!isDown(vk)) Process(new KeyEvent(vk, false, now));
        }
        public IList<Trail> Trails { get { return trails; } }
        public bool IsDown(string id) { return active.ContainsKey(id); }
        public long Count(string id) { long n; return counts.TryGetValue(id, out n) ? n : 0; }
        public void ResetCounts() { foreach (var id in counts.Keys.ToList()) counts[id] = 0; }
        public void ReleaseAll(double now) { foreach (int vk in physicalDown.ToList()) Process(new KeyEvent(vk, false, now)); }
    }
}
