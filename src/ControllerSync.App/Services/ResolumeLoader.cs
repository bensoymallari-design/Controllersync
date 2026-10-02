using System.Net.Http;
using System.Text;
using ControllerSync.Core;

namespace ControllerSync.App.Services;

public interface IResolumeLoader
{
    Task<string> OpenAsync(string absolutePath, int layer, int clip, bool play, int port, CancellationToken cancellationToken);
}

public sealed class ResolumeLoader : IResolumeLoader
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<string> OpenAsync(string absolutePath, int layer, int clip, bool play, int port, CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535)
            return "Resolume port must be from 1 to 65535.";
        if (layer < 1 || clip < 1)
            return "Layer and clip start at 1, matching the numbers in Resolume.";

        var uri = ResolumeAddress.FileUri(absolutePath);
        using var body = new StringContent(uri, Encoding.UTF8, "text/plain");
        HttpResponseMessage opened;
        try
        {
            opened = await _http.PostAsync(ResolumeAddress.OpenUrl(port, layer, clip), body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return "Saved the file, but Resolume did not answer on port " + port + ". In Resolume, open Preferences, Web Server, and turn it on. " + ex.Message;
        }

        var openedText = await opened.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccessStatusCode)
            return "Saved the file, but Resolume refused layer " + layer + " clip " + clip + " (" + (int)opened.StatusCode + "). " + Trim(openedText);

        if (!play)
            return "Loaded into Resolume layer " + layer + ", clip " + clip + ".";

        try
        {
            using var connectBody = new StringContent("1", Encoding.UTF8, "text/plain");
            using var connected = await _http.PostAsync(ResolumeAddress.ConnectUrl(port, layer, clip), connectBody, cancellationToken).ConfigureAwait(false);
            if (!connected.IsSuccessStatusCode)
                return "Loaded into layer " + layer + ", clip " + clip + ". Resolume did not start playback.";
        }
        catch (Exception ex)
        {
            return "Loaded into layer " + layer + ", clip " + clip + ". Playback was not started. " + ex.Message;
        }

        return "Loaded into Resolume layer " + layer + ", clip " + clip + ", and started.";
    }

    private static string Trim(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 180 ? text : text[..180];
    }
}
