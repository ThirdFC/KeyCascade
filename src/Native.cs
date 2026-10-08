using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeyCascade
{
    internal static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        public const int WM_HOTKEY = 0x312, WM_APP_SHOW = 0x8011;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] public struct Kbd { public uint vkCode, scanCode, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct Msg { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public Point point; public uint privateValue; }
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string module);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
        [DllImport("user32.dll")] public static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref Msg msg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref Msg msg);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool PostThreadMessage(uint id, uint msg, UIntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
        public static long GetStyle(IntPtr hwnd) { return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64() : GetWindowLong32(hwnd, GWL_EXSTYLE); }
        public static void SetStyle(IntPtr hwnd, long style) { if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(style)); else SetWindowLong32(hwnd, GWL_EXSTYLE, (int)style); }
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [StructLayout(LayoutKind.Sequential)] public struct RawDevice { public ushort usagePage, usage; public uint flags; public IntPtr target; }
        [StructLayout(LayoutKind.Sequential)] public struct RawHeader { public uint type, size; public IntPtr device, wp; }
        [StructLayout(LayoutKind.Sequential)] public struct RawKeyboard { public ushort scan, flags, reserved, vk; public uint message, extra; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint GetRegisteredRawInputDevices([Out] RawDevice[] devices, ref uint count, uint size);
        public static void EnableDpi() { try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { } }
    }

    public struct KeyEvent
    {
        public int Code; public bool Down; public double Time; public int Source;
        public KeyEvent(int code, bool down, double time, int source = 0) { Code = code; Down = down; Time = time; Source = source; }
    }

    public sealed class KeyboardHook : IDisposable
    {
        public readonly ConcurrentQueue<KeyEvent> Events = new ConcurrentQueue<KeyEvent>();
        public event Action InputAvailable;
        private readonly Thread thread;
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private Native.HookProc callback;
        private IntPtr handle;
        private IntPtr rawWindow;
        private uint threadId;
        private Exception failure;
        private bool disposed;
        public IntPtr RawWindow { get { return rawWindow; } }
        public static double Now { get { return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; } }
        private static readonly double tickEpoch = Now - Native.GetTickCount64() / 1000.0;
        public KeyboardHook()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "Global keyboard listener" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!ready.WaitOne(5000)) { Dispose(); throw new TimeoutException("键盘监听初始化超时。"); }
            if (failure != null) { Dispose(); throw failure; }
        }
        private void Run()
        {
            try
            {
                threadId = Native.GetCurrentThreadId(); Native.Msg message;
                Native.PeekMessage(out message, IntPtr.Zero, 0, 0, 0);
                rawWindow = Native.CreateWindowEx(0, "STATIC", "KeyCascade input sink", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
                if (rawWindow == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建键盘输入窗口。");
                var device = new Native.RawDevice { usagePage = 1, usage = 6, flags = 0x100, target = rawWindow };
                if (!Native.RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf(typeof(Native.RawDevice)))) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册后台键盘输入。");
                callback = OnKey;
                handle = Native.SetWindowsHookEx(13, callback, Native.GetModuleHandle(null), 0);
                if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动全局键盘监听。");
                Native.PostThreadMessage(threadId, 0x8019, UIntPtr.Zero, IntPtr.Zero);
                int result;
                while ((result = Native.GetMessage(out message, IntPtr.Zero, 0, 0)) > 0)
                {
                    if (message.message == 0x8019) { ready.Set(); continue; }
                    if (message.message == 0xFF && message.hwnd == rawWindow) ReadRawInput(message.lParam, message.time);
                    Native.TranslateMessage(ref message); Native.DispatchMessage(ref message);
                }
                if (result < 0) failure = new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch (Exception e) { failure = e; ready.Set(); }
            finally
            {
                if (handle != IntPtr.Zero) { Native.UnhookWindowsHookEx(handle); handle = IntPtr.Zero; }
                if (rawWindow != IntPtr.Zero)
                {
                    var remove = new Native.RawDevice { usagePage = 1, usage = 6, flags = 1, target = IntPtr.Zero };
                    Native.RegisterRawInputDevices(new[] { remove }, 1, (uint)Marshal.SizeOf(typeof(Native.RawDevice)));
                    Native.DestroyWindow(rawWindow); rawWindow = IntPtr.Zero;
                }
            }
        }
        private static double EventTime(uint stamp)
        {
            ulong tick = Native.GetTickCount64(); uint age = unchecked((uint)tick - stamp);
            return tickEpoch + (tick - age) / 1000.0;
        }
        private void ReadRawInput(IntPtr input, uint stamp)
        {
            uint size = 0; uint headerSize = (uint)Marshal.SizeOf(typeof(Native.RawHeader));
            if (Native.GetRawInputData(input, 0x10000003, IntPtr.Zero, ref size, headerSize) == uint.MaxValue || size < headerSize + Marshal.SizeOf(typeof(Native.RawKeyboard))) return;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Native.GetRawInputData(input, 0x10000003, buffer, ref size, headerSize) == uint.MaxValue) return;
                var header = (Native.RawHeader)Marshal.PtrToStructure(buffer, typeof(Native.RawHeader)); if (header.type != 1) return;
                var raw = (Native.RawKeyboard)Marshal.PtrToStructure(IntPtr.Add(buffer, (int)headerSize), typeof(Native.RawKeyboard));
                int vk = raw.vk; if (vk == 255 || vk == 0) return;
                if (vk == 16) vk = raw.scan == 54 ? 161 : 160;
                else if (vk == 17) vk = (raw.flags & 2) != 0 ? 163 : 162;
                else if (vk == 18) vk = (raw.flags & 2) != 0 ? 165 : 164;
                QueueEvent(new KeyEvent(vk, (raw.flags & 1) == 0, EventTime(stamp), 2));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        private IntPtr OnKey(int code, IntPtr wp, IntPtr lp)
        {
            if (code >= 0)
            {
                long message = wp.ToInt64();
                if (message == 0x100 || message == 0x104 || message == 0x101 || message == 0x105)
                {
                    var key = (Native.Kbd)Marshal.PtrToStructure(lp, typeof(Native.Kbd));
                    // Raw Input provides a second hardware path if Windows drops this hook.
                    // The engine deduplicates both paths and ignores stale events using OS timestamps.
                    QueueEvent(new KeyEvent((int)key.vkCode, message == 0x100 || message == 0x104, EventTime(key.time), 1));
                }
            }
            // Never consume an event: the game receives the original keystroke.
            return Native.CallNextHookEx(handle, code, wp, lp);
        }
        private void QueueEvent(KeyEvent e)
        {
            Events.Enqueue(e); var available = InputAvailable;
            if (available != null) { try { available(); } catch (InvalidOperationException) { } }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (threadId != 0) Native.PostThreadMessage(threadId, 0x12, UIntPtr.Zero, IntPtr.Zero);
            if (Thread.CurrentThread != thread && thread.IsAlive) thread.Join(2000);
            // A late-starting thread may still signal this event after a timeout.
        }
    }
}
