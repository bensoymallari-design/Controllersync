namespace ControllerSync.Core.Tests;

public class MediaTransferTests
{
    [Fact]
    public void File_uri_keeps_slashes_and_encodes_spaces()
    {
        var path = Path.Combine(Path.GetTempPath(), "show clips", "intro video.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var uri = ResolumeAddress.FileUri(path);
        Assert.StartsWith("file:///", uri);
        Assert.Contains("intro%20video.mp4", uri);
        Assert.DoesNotContain("%2F", uri);
        Assert.Equal("http://127.0.0.1:8080/api/v1/composition/layers/2/clips/5/open", ResolumeAddress.OpenUrl(8080, 2, 5));
    }

    [Fact]
    public void Names_cannot_escape_the_media_folder()
    {
        Assert.Equal("evil.mp4", MediaNames.SafeFileName(@"..\..\evil.mp4"));
        Assert.Equal("clip", MediaNames.SafeFileName(".."));
        Assert.Equal("clip.mov", MediaNames.SafeFileName("clip.mov"));
    }

    [Fact]
    public void Extra_laptops_are_added_without_duplicates()
    {
        var targets = MediaNames.Targets("192.168.1.20", 24710, "192.168.1.21\n192.168.1.20\n10.0.0.5:25000");
        Assert.Equal(new[]
        {
            new MediaTarget("192.168.1.20", 24711),
            new MediaTarget("192.168.1.21", 24711),
            new MediaTarget("10.0.0.5", 25001)
        }, targets);
    }

    [Fact]
    public async Task A_file_sent_to_another_laptop_is_saved_intact()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cs-media-" + Guid.NewGuid().ToString("N"));
        var payload = System.Text.Encoding.UTF8.GetBytes("resolume-clip-bytes");
        MediaHeader? seen = null;
        await using var server = new MediaServer(directory, "show", (header, path, _) =>
        {
            seen = header;
            Assert.Equal(payload, File.ReadAllBytes(path));
            return Task.FromResult("Loaded into layer 3 clip 2.");
        });
        await server.StartAsync("127.0.0.1", 0);

        var receipt = await MediaClient.SendBytesAsync("127.0.0.1", server.BoundPort, "show", "intro.mp4", payload, 3, 2, true);
        Assert.True(receipt.Ok);
        Assert.Equal("Loaded into layer 3 clip 2.", receipt.Detail);
        Assert.Equal(3, seen!.Layer);
        Assert.Equal(2, seen.Clip);
        Assert.True(seen.Play);
        Assert.True(File.Exists(Path.Combine(directory, "intro.mp4")));
    }

    [Fact]
    public async Task A_different_channel_is_refused()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cs-media-" + Guid.NewGuid().ToString("N"));
        await using var server = new MediaServer(directory, "show");
        await server.StartAsync("127.0.0.1", 0);
        var receipt = await MediaClient.SendBytesAsync("127.0.0.1", server.BoundPort, "other", "intro.mp4", new byte[] { 1, 2, 3 }, 1, 1, false);
        Assert.False(receipt.Ok);
        Assert.Contains("channel", receipt.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(directory, "intro.mp4")));
    }

    [Fact]
    public async Task A_damaged_upload_is_not_kept()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cs-media-" + Guid.NewGuid().ToString("N"));
        await using var server = new MediaServer(directory, "show");
        await server.StartAsync("127.0.0.1", 0);
        var receipt = await MediaClient.SendBytesAsync(
            "127.0.0.1",
            server.BoundPort,
            "show",
            "intro.mp4",
            new byte[] { 9, 9, 9 },
            1,
            1,
            false,
            shaOverride: "AAAA");
        Assert.False(receipt.Ok);
        Assert.Contains("damaged", receipt.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(directory, "intro.mp4")));
    }
}
