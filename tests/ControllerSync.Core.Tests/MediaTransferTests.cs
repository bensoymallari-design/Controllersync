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
        Assert.Equal("http://127.0.0.1:8080/api/v1/composition/layers/2/clips/5/clear", ResolumeAddress.ClearUrl(8080, 2, 5));
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

    [Fact]
    public void A_second_copy_gets_a_new_name_and_a_list_keeps_each_path()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cs-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "intro.mp4"), new byte[] { 1 });
        Assert.Equal(Path.Combine(directory, "intro-2.mp4"), MediaNames.NextFreePath(directory, "intro.mp4"));
        File.WriteAllBytes(Path.Combine(directory, "intro-2.mp4"), new byte[] { 2 });
        Assert.Equal(Path.Combine(directory, "intro-3.mp4"), MediaNames.NextFreePath(directory, @"..\intro.mp4"));

        var files = MediaNames.FileList("C:\\Shows\\intro.mp4\r\nC:\\Shows\\loop.mov\nC:\\Shows\\intro.mp4\n");
        Assert.Equal(new[] { "C:\\Shows\\intro.mp4", "C:\\Shows\\loop.mov" }, files);
    }

    [Fact]
    public async Task Sending_the_same_name_again_saves_a_new_copy_and_still_opens_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cs-media-" + Guid.NewGuid().ToString("N"));
        var payload = System.Text.Encoding.UTF8.GetBytes("same-clip-again");
        var opened = new List<string>();
        await using var server = new MediaServer(directory, "show", (_, path, _) =>
        {
            opened.Add(path);
            return Task.FromResult("Loaded " + Path.GetFileName(path));
        });
        await server.StartAsync("127.0.0.1", 0);

        var first = await MediaClient.SendBytesAsync("127.0.0.1", server.BoundPort, "show", "intro.mp4", payload, 2, 4, true);
        var second = await MediaClient.SendBytesAsync("127.0.0.1", server.BoundPort, "show", "intro.mp4", payload, 2, 4, true);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(Path.Combine(directory, "intro.mp4"), first.SavedPath);
        Assert.Equal(Path.Combine(directory, "intro-2.mp4"), second.SavedPath);
        Assert.Equal(new[] { first.SavedPath, second.SavedPath }, opened);
        Assert.Equal(payload, File.ReadAllBytes(second.SavedPath));
        Assert.Contains("intro-2.mp4", second.Detail);
    }

    [Fact]
    public void Size_and_position_accept_pixels_and_blank_boxes()
    {
        Assert.True(ResolumePlacement.TryParse("", "", "", "", out var empty, out var error));
        Assert.Null(error);
        Assert.False(empty!.Any);

        Assert.True(ResolumePlacement.TryParse("1920", "1080", "-40", "12,5", out var frame, out error));
        Assert.Null(error);
        Assert.Equal(1920, frame!.Width);
        Assert.Equal(1080, frame.Height);
        Assert.Equal(-40, frame.X);
        Assert.Equal(12.5, frame.Y);
        Assert.Equal("1920×1080, X -40, Y 12.5", frame.Describe());

        Assert.False(ResolumePlacement.TryParse("0", "", "", "", out _, out error));
        Assert.Contains("Width", error);
        Assert.False(ResolumePlacement.TryParse("wide", "", "", "", out _, out error));
        Assert.Contains("Width", error);
    }

    [Fact]
    public void Clip_json_maps_size_and_position_to_resolume_parameters()
    {
        const string json = """
            {
              "video": {
                "width": { "id": 11, "valuetype": "ParamRange", "value": 1280 },
                "height": { "id": 12, "valuetype": "ParamRange", "value": 720 },
                "resize": { "id": 13, "valuetype": "ParamChoice", "value": "Original" },
                "effects": [
                  {
                    "name": "Transform",
                    "params": {
                      "Position X": { "id": 21, "valuetype": "ParamRange", "value": 0 },
                      "Position Y": { "id": 22, "valuetype": "ParamRange", "value": 0 }
                    }
                  }
                ]
              }
            }
            """;

        var plan = ResolumePlacement.Read(json, new ClipFrame(800, 600, 100, -20));
        Assert.False(plan.NeedsTransform);
        Assert.Empty(plan.Missing);
        Assert.Equal(new long[] { 11, 12, 13, 21, 22 }, plan.Writes.Select(write => write.Id));
        Assert.Contains("\"value\":800", plan.Writes[0].Body);
        Assert.Contains("\"value\":\"Stretch\"", plan.Writes[2].Body);
        Assert.Contains("\"value\":-20", plan.Writes[4].Body);
    }

    [Fact]
    public void Position_asks_for_a_transform_effect_when_the_clip_has_none()
    {
        const string json = """
            { "video": { "width": { "id": 11 }, "height": { "id": 12 } } }
            """;
        var plan = ResolumePlacement.Read(json, new ClipFrame(null, null, 10, 20));
        Assert.True(plan.NeedsTransform);
        Assert.Empty(plan.Writes);
    }

    [Fact]
    public async Task Size_and_position_travel_with_the_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cs-media-" + Guid.NewGuid().ToString("N"));
        MediaHeader? seen = null;
        await using var server = new MediaServer(directory, "show", (header, _, _) =>
        {
            seen = header;
            return Task.FromResult("ok");
        });
        await server.StartAsync("127.0.0.1", 0);
        var receipt = await MediaClient.SendBytesAsync(
            "127.0.0.1",
            server.BoundPort,
            "show",
            "intro.mp4",
            new byte[] { 1, 2, 3, 4 },
            1,
            1,
            false,
            new ClipFrame(1920, 1080, 15, 30));
        Assert.True(receipt.Ok);
        Assert.Equal(1920, seen!.Width);
        Assert.Equal(1080, seen.Height);
        Assert.Equal(15, seen.X);
        Assert.Equal(30, seen.Y);
    }
}
