using System.Net;
using System.Net.Sockets;

namespace ControllerSync.Core;

public sealed record LinkRequest(
    string BindAddress,
    string ListenPort,
    string RemoteAddress,
    string RemotePort,
    string Channel,
    bool NeedsRemote);

public sealed record LinkEndpoints(
    string BindAddress,
    int ListenPort,
    string? RemoteHost,
    int RemotePort,
    string Channel);

public static class LinkForm
{
    public static bool TryParse(LinkRequest request, out LinkEndpoints? endpoints, out string? error)
    {
        endpoints = null;
        var bind = (request.BindAddress ?? "").Trim();
        var remote = (request.RemoteAddress ?? "").Trim();
        var channel = (request.Channel ?? "").Trim();

        if (bind.Length == 0)
            bind = "0.0.0.0";
        if (!IsBindAddress(bind))
        {
            error = "Incoming IP must be 0.0.0.0 or an IP address of this laptop.";
            return false;
        }

        if (!int.TryParse((request.ListenPort ?? "").Trim(), out var listen) || listen is < 1 or > 65535)
        {
            error = "Incoming port must be a number from 1 to 65535.";
            return false;
        }

        if (!int.TryParse((request.RemotePort ?? "").Trim(), out var remotePort) || remotePort is < 1 or > 65535)
        {
            error = "Outgoing port must be a number from 1 to 65535.";
            return false;
        }

        if (channel.Length == 0)
        {
            error = "Enter a channel name. Both laptops must use the same one.";
            return false;
        }

        if (channel.Length > 64)
        {
            error = "Channel name must be 64 characters or less.";
            return false;
        }

        if (request.NeedsRemote && remote.Length == 0)
        {
            error = "Enter the outgoing IP — that is the other laptop's address.";
            return false;
        }

        if (remote.Length > 0 && !IsRemoteHost(remote))
        {
            error = "Outgoing IP is not a valid address or computer name.";
            return false;
        }

        endpoints = new LinkEndpoints(bind, listen, remote.Length == 0 ? null : remote, remotePort, channel);
        error = null;
        return true;
    }

    public static bool IsBindAddress(string value)
    {
        if (value is "0.0.0.0" or "127.0.0.1" or "localhost")
            return true;
        return IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetwork;
    }

    public static bool IsRemoteHost(string value)
    {
        if (IPAddress.TryParse(value, out var address))
            return address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;
        return Uri.CheckHostName(value) != UriHostNameType.Unknown;
    }
}
