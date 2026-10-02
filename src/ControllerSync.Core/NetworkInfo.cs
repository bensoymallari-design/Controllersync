using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ControllerSync.Core;

public static class LocalAddresses
{
    public static IReadOnlyList<string> IPv4()
    {
        var list = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                var text = unicast.Address.ToString();
                if (text.StartsWith("127.", StringComparison.Ordinal) || text.StartsWith("169.254.", StringComparison.Ordinal))
                    continue;
                if (!list.Contains(text))
                    list.Add(text);
            }
        }

        return list;
    }
}

public static class MachineIdentity
{
    public static string GetOrCreate()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ControllerSync");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "machine.id");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 8)
                return existing;
        }

        var created = Guid.NewGuid().ToString("N");
        File.WriteAllText(path, created);
        return created;
    }
}
