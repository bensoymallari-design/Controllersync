using System.Diagnostics;

namespace ControllerSync.App.Platform;

public static class FirewallRule
{
    public static void Allow(int port)
    {
        if (port is < 1 or > 65535)
            throw new InvalidOperationException("Incoming port must be a number from 1 to 65535.");

        var start = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments =
                "/c netsh advfirewall firewall add rule " +
                $"name=\"ControllerSync {port}\" dir=in action=allow protocol=TCP localport={port}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("The firewall change was cancelled.");
        }
    }
}
