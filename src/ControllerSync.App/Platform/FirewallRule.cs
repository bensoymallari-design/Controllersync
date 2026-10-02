using System.Diagnostics;

namespace ControllerSync.App.Platform;

public static class FirewallRule
{
    public static void Allow(params int[] ports)
    {
        var distinct = ports.Where(port => port is >= 1 and <= 65535).Distinct().ToArray();
        if (distinct.Length == 0)
            throw new InvalidOperationException("Incoming port must be a number from 1 to 65535.");

        var commands = string.Join(
            " & ",
            distinct.Select(port =>
                "netsh advfirewall firewall add rule " +
                $"name=\"ControllerSync {port}\" dir=in action=allow protocol=TCP localport={port}"));
        var start = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + commands,
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
