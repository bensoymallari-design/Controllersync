using System.Net.Http;
using System.Text;
using ControllerSync.Core;

namespace ControllerSync.App.Services;

public interface IResolumeLoader
{
    Task<string> OpenAsync(string absolutePath, int layer, int clip, bool play, int port, ClipFrame? frame, CancellationToken cancellationToken);
}

public sealed class ResolumeLoader : IResolumeLoader
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<string> OpenAsync(string absolutePath, int layer, int clip, bool play, int port, ClipFrame? frame, CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535)
            return "Resolume port must be from 1 to 65535.";
        if (layer < 1 || clip < 1)
            return "Layer and clip start at 1, matching the numbers in Resolume.";

        await ClearSlotAsync(port, layer, clip, cancellationToken).ConfigureAwait(false);
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

        var placed = frame is { Any: true }
            ? await ApplyFrameAsync(port, layer, clip, frame, cancellationToken).ConfigureAwait(false)
            : "";
        if (!play)
            return "Loaded into Resolume layer " + layer + ", clip " + clip + "." + placed;

        try
        {
            // A slot that already played this session ignores a second connect until it is released.
            using var released = await PostTextAsync(ResolumeAddress.ConnectUrl(port, layer, clip), "0", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Releasing an idle slot can fail. Connecting below still runs.
        }

        try
        {
            using var connected = await PostTextAsync(ResolumeAddress.ConnectUrl(port, layer, clip), "1", cancellationToken).ConfigureAwait(false);
            if (!connected.IsSuccessStatusCode)
                return "Loaded into layer " + layer + ", clip " + clip + ". Resolume did not start playback." + placed;
        }
        catch (Exception ex)
        {
            return "Loaded into layer " + layer + ", clip " + clip + ". Playback was not started. " + ex.Message + placed;
        }

        return "Loaded into Resolume layer " + layer + ", clip " + clip + ", and started." + placed;
    }

    private async Task<string> ApplyFrameAsync(int port, int layer, int clip, ClipFrame frame, CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await _http.GetStringAsync(ResolumeAddress.ClipUrl(port, layer, clip), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return " Size and position were not applied. " + ex.Message;
        }

        var plan = ResolumePlacement.Read(json, frame);
        if (plan.NeedsTransform)
        {
            try
            {
                using var effect = new StringContent(ResolumePlacement.TransformEffect, Encoding.UTF8, "text/plain");
                using var added = await _http.PostAsync(ResolumeAddress.AddTransformUrl(port, layer, clip), effect, cancellationToken).ConfigureAwait(false);
                if (!added.IsSuccessStatusCode)
                    return " Position was not applied. In Resolume, add a Transform effect on that clip.";
                json = await _http.GetStringAsync(ResolumeAddress.ClipUrl(port, layer, clip), cancellationToken).ConfigureAwait(false);
                plan = ResolumePlacement.Read(json, frame);
            }
            catch (Exception ex)
            {
                return " Position was not applied. " + ex.Message;
            }
        }

        var problems = new List<string>(plan.Missing);
        foreach (var write in plan.Writes)
        {
            try
            {
                using var body = new StringContent(write.Body, Encoding.UTF8, "application/json");
                using var updated = await _http.PutAsync(ResolumeAddress.ParameterUrl(port, write.Id), body, cancellationToken).ConfigureAwait(false);
                if (!updated.IsSuccessStatusCode)
                    problems.Add(write.Label);
            }
            catch (Exception ex)
            {
                return " Size and position were not finished. " + ex.Message;
            }
        }

        if (plan.NeedsTransform)
            problems.Add("position");
        if (problems.Count > 0)
            return " Could not set " + string.Join(", ", problems) + ".";
        var described = frame.Describe();
        return described.Length == 0 ? "" : " Placed " + described + ".";
    }

    private async Task ClearSlotAsync(int port, int layer, int clip, CancellationToken cancellationToken)
    {
        try
        {
            using var cleared = await PostTextAsync(ResolumeAddress.ClearUrl(port, layer, clip), "1", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // An empty slot, or Resolume with the web server off, is reported by the open call.
        }
    }

    private async Task<HttpResponseMessage> PostTextAsync(string url, string text, CancellationToken cancellationToken)
    {
        using var body = new StringContent(text, Encoding.UTF8, "text/plain");
        return await _http.PostAsync(url, body, cancellationToken).ConfigureAwait(false);
    }

    private static string Trim(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 180 ? text : text[..180];
    }
}
