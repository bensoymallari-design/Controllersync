using System.Runtime.InteropServices;
using ControllerSync.Core;

namespace ControllerSync.App.Platform;

public sealed class WindowsInputSource : ILocalInputSource
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const uint WmQuit = 0x0012;
    private const uint LlkhfInjected = 0x10;
    private const uint LlmhfInjected = 0x01;

    private IntPtr _keyboard;
    private IntPtr _mouse;
    private uint _threadId;
    private Thread? _thread;
    private HookProc? _keyboardProc;
    private HookProc? _mouseProc;
    private long _lastMoveMs;
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;

    public event Action<KeyEvent>? Key;
    public event Action<MouseEvent>? Mouse;

    public void Start()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (_thread != null)
            return;

        var ready = new ManualResetEventSlim(false);
        Exception? error = null;
        _keyboardProc = KeyboardHook;
        _mouseProc = MouseHook;
        _thread = new Thread(() =>
        {
            try
            {
                _threadId = GetCurrentThreadId();
                PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
                _keyboard = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, IntPtr.Zero, 0);
                _mouse = SetWindowsHookEx(WhMouseLl, _mouseProc, IntPtr.Zero, 0);
                if (_keyboard == IntPtr.Zero)
                    error = new InvalidOperationException("Windows did not allow keyboard capture on this laptop.");
            }
            catch (Exception ex)
            {
                error = ex;
            }

            ready.Set();
            if (error != null)
                return;

            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            if (_keyboard != IntPtr.Zero)
                UnhookWindowsHookEx(_keyboard);
            if (_mouse != IntPtr.Zero)
                UnhookWindowsHookEx(_mouse);
        })
        {
            IsBackground = true,
            Name = "ControllerSync.Input"
        };
        _thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException("Keyboard capture did not start.");
        if (error != null)
        {
            Stop();
            throw error;
        }
    }

    public void Stop()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread = null;
        _threadId = 0;
    }

    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<KbdLlHook>(lParam);
                var injected = (info.flags & LlkhfInjected) != 0 || info.dwExtraInfo == InjectMarker.Value;
                var message = (uint)wParam;
                if (!injected
                    && !IsOwnProcessForeground()
                    && message is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
                {
                    var key = new KeyEvent(
                        (int)info.vkCode,
                        (int)info.scanCode,
                        message is WmKeyDown or WmSysKeyDown ? KeyPhase.Down : KeyPhase.Up,
                        Down(0x12),
                        Down(0x11),
                        Down(0x10));
                    if (key.VirtualKey != 0)
                        ThreadPool.QueueUserWorkItem(_ => Key?.Invoke(key));
                }
            }
            catch
            {
                // A hook that throws is removed by Windows.
            }
        }

        return CallNextHookEx(_keyboard, nCode, wParam, lParam);
    }

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<MsLlHook>(lParam);
                var injected = (info.flags & LlmhfInjected) != 0 || info.dwExtraInfo == InjectMarker.Value;
                if (!injected && !IsOwnProcessForeground())
                    PublishMouse((uint)wParam, info);
            }
            catch
            {
                // A hook that throws is removed by Windows.
            }
        }

        return CallNextHookEx(_mouse, nCode, wParam, lParam);
    }

    private void PublishMouse(uint message, MsLlHook info)
    {
        var phase = message switch
        {
            WmMouseMove => MousePhase.Move,
            WmLButtonDown or WmRButtonDown or WmMButtonDown => MousePhase.Down,
            WmLButtonUp or WmRButtonUp or WmMButtonUp => MousePhase.Up,
            WmMouseWheel => MousePhase.Wheel,
            _ => (MousePhase?)null
        };
        if (phase == null)
            return;

        if (phase == MousePhase.Move)
        {
            var now = Environment.TickCount64;
            if (now - _lastMoveMs < 33 && Math.Abs(info.pt.X - _lastX) < 2 && Math.Abs(info.pt.Y - _lastY) < 2)
                return;
            _lastMoveMs = now;
            _lastX = info.pt.X;
            _lastY = info.pt.Y;
        }

        var button = message switch
        {
            WmRButtonDown or WmRButtonUp => MouseButton.Right,
            WmMButtonDown or WmMButtonUp => MouseButton.Middle,
            WmLButtonDown or WmLButtonUp => MouseButton.Left,
            _ => MouseButton.None
        };
        var (x, y) = Normalize(info.pt.X, info.pt.Y);
        var delta = phase == MousePhase.Wheel ? (short)((info.mouseData >> 16) & 0xffff) : 0;
        var mouse = new MouseEvent(phase.Value, button, x, y, delta);
        ThreadPool.QueueUserWorkItem(_ => Mouse?.Invoke(mouse));
    }

    private static (double X, double Y) Normalize(int x, int y)
    {
        int originX = GetSystemMetrics(76);
        int originY = GetSystemMetrics(77);
        int width = Math.Max(1, GetSystemMetrics(78));
        int height = Math.Max(1, GetSystemMetrics(79));
        var nx = Math.Clamp((x - originX) / (double)width, 0, 1);
        var ny = Math.Clamp((y - originY) / (double)height, 0, 1);
        return (nx, ny);
    }

    private static bool Down(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool IsOwnProcessForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHook
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHook
    {
        public Point pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public Point pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
