namespace ControllerSync.Core;

public interface ISyncTransport
{
    bool IsConnected { get; }
    event Action<SyncMessage>? MessageReceived;
    Task SendAsync(SyncMessage message, CancellationToken cancellationToken = default);
}

public interface ILocalInputSource
{
    event Action<KeyEvent>? Key;
    event Action<MouseEvent>? Mouse;
    void Start();
    void Stop();
}

public interface ILocalInputInjector
{
    void InjectKey(KeyEvent key);
    void InjectMouse(MouseEvent mouse);
}

public readonly record struct SlideApplyResult(bool Applied, string? Warning);

public interface ISlideBridge
{
    SlideEvent? TryRead();
    SlideApplyResult Apply(SlideEvent slide);
    bool TryStep(ShowAction action);
}

public sealed class ShowLinkOptions
{
    public bool SendKeyboard { get; init; }
    public bool SendAllKeys { get; init; }
    public bool SendMouseClicks { get; init; }
    public bool SendMouseMove { get; init; }
    public bool PublishSlides { get; init; }
    public bool FollowSlides { get; init; }
    public bool AcceptKeyboard { get; init; }
    public bool AcceptMouse { get; init; }
    public string Channel { get; init; } = "show";
    public string MachineId { get; init; } = "";
    public string MachineName { get; init; } = "";
}
