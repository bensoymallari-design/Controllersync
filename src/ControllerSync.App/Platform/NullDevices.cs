using ControllerSync.Core;

namespace ControllerSync.App.Platform;

public sealed class NullInputSource : ILocalInputSource
{
    public event Action<KeyEvent>? Key { add { } remove { } }
    public event Action<MouseEvent>? Mouse { add { } remove { } }
    public void Start() { }
    public void Stop() { }
}

public sealed class NullInputInjector : ILocalInputInjector
{
    public void InjectKey(KeyEvent key) { }
    public void InjectMouse(MouseEvent mouse) { }
}

public sealed class NullSlideBridge : ISlideBridge
{
    public SlideEvent? TryRead() => null;
    public SlideApplyResult Apply(SlideEvent slide) => new(false, null);
    public bool TryStep(ShowAction action) => false;
}
