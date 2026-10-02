using ControllerSync.Core;

namespace ControllerSync.App.Platform;

public static class PlatformHost
{
    public static ILocalInputSource CreateInput() =>
        OperatingSystem.IsWindows() ? new WindowsInputSource() : new NullInputSource();

    public static ILocalInputInjector CreateInjector() =>
        OperatingSystem.IsWindows() ? new WindowsInputInjector() : new NullInputInjector();

    public static ISlideBridge CreateSlides() =>
        OperatingSystem.IsWindows() ? new PowerPointSession() : new NullSlideBridge();
}
