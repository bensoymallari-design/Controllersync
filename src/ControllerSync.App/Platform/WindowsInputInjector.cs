using System.Runtime.InteropServices;
using ControllerSync.Core;

namespace ControllerSync.App.Platform;

public sealed class WindowsInputInjector : ILocalInputInjector
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventExtended = 0x0001;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventWheel = 0x0800;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint MouseEventVirtualDesk = 0x4000;

    private readonly object _gate = new();

    public void InjectKey(KeyEvent key)
    {
        if (!OperatingSystem.IsWindows() || key.VirtualKey <= 0)
            return;

        var inputs = new List<Input>(4);
        if (key.Phase == KeyPhase.Down)
        {
            AddModifier(inputs, key.Shift, 0x10, keyUp: false, key.VirtualKey);
            AddModifier(inputs, key.Ctrl, 0x11, keyUp: false, key.VirtualKey);
            AddModifier(inputs, key.Alt, 0x12, keyUp: false, key.VirtualKey);
            inputs.Add(KeyInput(key.VirtualKey, keyUp: false));
        }
        else
        {
            inputs.Add(KeyInput(key.VirtualKey, keyUp: true));
            AddModifier(inputs, key.Alt, 0x12, keyUp: true, key.VirtualKey);
            AddModifier(inputs, key.Ctrl, 0x11, keyUp: true, key.VirtualKey);
            AddModifier(inputs, key.Shift, 0x10, keyUp: true, key.VirtualKey);
        }

        lock (_gate)
            Send(inputs);
    }

    public void InjectMouse(MouseEvent mouse)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var x = (int)Math.Round(Math.Clamp(mouse.X, 0, 1) * 65535);
        var y = (int)Math.Round(Math.Clamp(mouse.Y, 0, 1) * 65535);
        var inputs = new List<Input>(2)
        {
            MakeMouse(x, y, MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk, 0)
        };

        uint flags = mouse.Phase switch
        {
            MousePhase.Down when mouse.Button == MouseButton.Right => MouseEventRightDown,
            MousePhase.Down when mouse.Button == MouseButton.Middle => MouseEventMiddleDown,
            MousePhase.Down => MouseEventLeftDown,
            MousePhase.Up when mouse.Button == MouseButton.Right => MouseEventRightUp,
            MousePhase.Up when mouse.Button == MouseButton.Middle => MouseEventMiddleUp,
            MousePhase.Up => MouseEventLeftUp,
            MousePhase.Wheel => MouseEventWheel,
            _ => 0
        };
        if (flags != 0)
        {
            var data = flags == MouseEventWheel ? (uint)mouse.WheelDelta : 0;
            inputs.Add(MakeMouse(x, y, flags | (flags == MouseEventWheel ? 0 : MouseEventAbsolute | MouseEventVirtualDesk), data));
        }

        lock (_gate)
            Send(inputs);
    }

    private static void AddModifier(List<Input> inputs, bool pressed, int virtualKey, bool keyUp, int actualKey)
    {
        if (!pressed || actualKey == virtualKey)
            return;
        inputs.Add(KeyInput(virtualKey, keyUp));
    }

    private static Input KeyInput(int virtualKey, bool keyUp)
    {
        uint flags = keyUp ? KeyEventKeyUp : 0;
        if (virtualKey is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E)
            flags |= KeyEventExtended;
        return new Input
        {
            type = InputKeyboard,
            u = new InputUnion
            {
                ki = new KeybdInput
                {
                    wVk = (ushort)virtualKey,
                    wScan = 0,
                    dwFlags = flags,
                    dwExtraInfo = InjectMarker.Value
                }
            }
        };
    }

    private static Input MakeMouse(int x, int y, uint flags, uint data) => new()
    {
        type = InputMouse,
        u = new InputUnion
        {
            mi = new MouseInput
            {
                dx = x,
                dy = y,
                mouseData = data,
                dwFlags = flags,
                dwExtraInfo = InjectMarker.Value
            }
        }
    };

    private static void Send(List<Input> inputs)
    {
        if (inputs.Count == 0)
            return;
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput mi;
        [FieldOffset(0)] public KeybdInput ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);
}
