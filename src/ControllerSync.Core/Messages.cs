namespace ControllerSync.Core;

public enum SyncKind
{
    Hello = 1,
    Ping = 2,
    Pong = 3,
    Key = 4,
    Mouse = 5,
    Slide = 6,
    Cue = 7
}

public enum KeyPhase
{
    Down = 1,
    Up = 2
}

public enum MousePhase
{
    Move = 1,
    Down = 2,
    Up = 3,
    Wheel = 4
}

public enum MouseButton
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3
}

/// <summary>PowerPoint SlideShowView.State values.</summary>
public enum ShowScreen
{
    Unknown = 0,
    Running = 1,
    Paused = 2,
    Black = 3,
    White = 4,
    Done = 5
}

public enum ShowAction
{
    None = 0,
    Next,
    Previous,
    First,
    Last,
    StartFromFirst,
    StartFromCurrent,
    EndShow,
    BlackScreen,
    WhiteScreen
}

public sealed record KeyEvent(int VirtualKey, int ScanCode, KeyPhase Phase, bool Alt, bool Ctrl, bool Shift);

public sealed record MouseEvent(MousePhase Phase, MouseButton Button, double X, double Y, int WheelDelta);

public sealed record SlideEvent(
    string DeckName,
    int SlideIndex,
    int ClickIndex,
    int SlideCount,
    bool InShow,
    ShowScreen Screen)
{
    public static bool SamePosition(SlideEvent a, SlideEvent b) =>
        a.InShow == b.InShow
        && a.SlideIndex == b.SlideIndex
        && a.ClickIndex == b.ClickIndex
        && a.SlideCount == b.SlideCount
        && a.Screen == b.Screen;
}

public sealed class SyncMessage
{
    public int V { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Src { get; set; } = "";
    public string Name { get; set; } = "";
    public SyncKind Kind { get; set; }
    public long Seq { get; set; }
    public long UnixMs { get; set; }
    public string Channel { get; set; } = "";
    public ShowAction Action { get; set; }
    public KeyEvent? Key { get; set; }
    public MouseEvent? Mouse { get; set; }
    public SlideEvent? Slide { get; set; }

    public static SyncMessage Create(string src, string name, string channel, SyncKind kind, long seq) =>
        new()
        {
            Src = src,
            Name = name,
            Channel = channel,
            Kind = kind,
            Seq = seq,
            UnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
}

public static class PresentationKeys
{
    public const int Back = 0x08;
    public const int Enter = 0x0D;
    public const int Shift = 0x10;
    public const int Ctrl = 0x11;
    public const int Alt = 0x12;
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int PageUp = 0x21;
    public const int PageDown = 0x22;
    public const int End = 0x23;
    public const int Home = 0x24;
    public const int Left = 0x25;
    public const int Up = 0x26;
    public const int Right = 0x27;
    public const int Down = 0x28;
    public const int B = 0x42;
    public const int N = 0x4E;
    public const int P = 0x50;
    public const int W = 0x57;
    public const int F5 = 0x74;
    public const int Period = 0xBE;

    public static bool IsSlideNumberKey(int virtualKey) =>
        virtualKey is >= 0x30 and <= 0x39 or >= 0x60 and <= 0x69;

    public static ShowAction Map(KeyEvent key)
    {
        if (key.VirtualKey == F5 && key.Shift && !key.Ctrl && !key.Alt)
            return ShowAction.StartFromCurrent;
        if (key.VirtualKey == F5 && !key.Ctrl && !key.Alt)
            return ShowAction.StartFromFirst;

        var letterLike = key.VirtualKey is not (Left or Up or Right or Down or PageUp or PageDown or Home or End);
        if ((key.Ctrl || key.Alt) && letterLike)
            return ShowAction.None;

        return key.VirtualKey switch
        {
            Right or Down or PageDown or Space or Enter or N => ShowAction.Next,
            Left or Up or PageUp or Back or P => ShowAction.Previous,
            Home => ShowAction.First,
            End => ShowAction.Last,
            Escape => ShowAction.EndShow,
            B or Period => ShowAction.BlackScreen,
            W => ShowAction.WhiteScreen,
            _ => ShowAction.None
        };
    }

    public static bool IsShowControl(KeyEvent key)
    {
        if ((key.Ctrl || key.Alt) && IsSlideNumberKey(key.VirtualKey))
            return false;
        if (Map(key) != ShowAction.None)
            return true;
        return IsSlideNumberKey(key.VirtualKey) && !key.Ctrl && !key.Alt;
    }

    public static KeyEvent Make(ShowAction action, KeyPhase phase)
    {
        var shift = action == ShowAction.StartFromCurrent;
        var vk = action switch
        {
            ShowAction.Next => Right,
            ShowAction.Previous => Left,
            ShowAction.First => Home,
            ShowAction.Last => End,
            ShowAction.StartFromFirst => F5,
            ShowAction.StartFromCurrent => F5,
            ShowAction.EndShow => Escape,
            ShowAction.BlackScreen => B,
            ShowAction.WhiteScreen => W,
            _ => 0
        };
        return new KeyEvent(vk, 0, phase, false, false, shift);
    }
}

public static class InputGate
{
    public static bool ShouldSendKey(bool publishSlides, bool localInShow, KeyEvent key)
    {
        if (publishSlides && localInShow && PresentationKeys.IsShowControl(key))
            return false;
        return true;
    }

    public static bool ShouldSendMouse(
        bool publishSlides,
        bool localInShow,
        MousePhase phase,
        bool clicksEnabled,
        bool moveEnabled)
    {
        if (phase == MousePhase.Move)
            return moveEnabled;
        if (!clicksEnabled)
            return false;
        if (publishSlides && localInShow)
            return false;
        return true;
    }

    public static bool ShouldInjectKey(bool followSlides, bool remoteInShow, KeyEvent key)
    {
        if (followSlides && remoteInShow && PresentationKeys.IsShowControl(key))
            return false;
        return true;
    }

    public static bool ShouldInjectMouse(bool followSlides, bool remoteInShow, MousePhase phase)
    {
        if (phase == MousePhase.Move)
            return true;
        if (followSlides && remoteInShow)
            return false;
        return true;
    }
}

public static class CueText
{
    public static string Label(ShowAction action) => action switch
    {
        ShowAction.Next => "NEXT",
        ShowAction.Previous => "PREVIOUS",
        ShowAction.First => "FIRST SLIDE",
        ShowAction.Last => "LAST SLIDE",
        ShowAction.StartFromFirst => "START SHOW",
        ShowAction.StartFromCurrent => "START FROM HERE",
        ShowAction.EndShow => "END SHOW",
        ShowAction.BlackScreen => "BLACK",
        ShowAction.WhiteScreen => "WHITE",
        _ => ""
    };

    public static string From(SyncMessage message)
    {
        if (message.Kind == SyncKind.Cue)
            return Label(message.Action);

        if (message.Slide is { } slide)
        {
            if (!slide.InShow)
                return "SHOW ENDED";
            if (slide.Screen == ShowScreen.Black)
                return "BLACK";
            if (slide.Screen == ShowScreen.White)
                return "WHITE";
            return slide.ClickIndex > 1
                ? $"SLIDE {slide.SlideIndex}  ·  {slide.ClickIndex}"
                : $"SLIDE {slide.SlideIndex}";
        }

        if (message.Key is { Phase: KeyPhase.Down } key)
        {
            var label = Label(PresentationKeys.Map(key));
            if (label.Length > 0)
                return label;
            if (PresentationKeys.IsSlideNumberKey(key.VirtualKey))
                return "NUMBER";
            return "KEY";
        }

        if (message.Mouse is { Phase: MousePhase.Down } mouse)
        {
            return mouse.Button switch
            {
                MouseButton.Right => "RIGHT CLICK",
                MouseButton.Middle => "MIDDLE CLICK",
                _ => "CLICK"
            };
        }

        return "";
    }
}

public sealed class SlideFollowPolicy
{
    private SlideEvent? _lastSent;
    private SlideEvent? _holdingRemote;

    public bool PublishEnabled { get; set; }

    public void NoteRemoteApplied(SlideEvent remote) => _holdingRemote = remote;

    public bool ShouldPublish(SlideEvent local, bool heartbeatDue)
    {
        if (!PublishEnabled)
            return false;
        if (_holdingRemote != null && SlideEvent.SamePosition(_holdingRemote, local))
            return false;

        _holdingRemote = null;
        if (_lastSent != null && SlideEvent.SamePosition(_lastSent, local) && !heartbeatDue)
            return false;

        _lastSent = local;
        return true;
    }
}
