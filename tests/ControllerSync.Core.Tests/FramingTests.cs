using System.Text;

namespace ControllerSync.Core.Tests;

public class FramingTests
{
    [Fact]
    public void Frame_round_trip_survives_being_split_into_single_bytes()
    {
        var message = SampleKey();
        var frame = SyncCodec.EncodeFrame(message);
        var reader = new FrameReader();
        byte[]? body = null;
        foreach (var b in frame)
        {
            reader.Append(new[] { b });
            if (reader.TryRead(out var read))
                body = read;
        }

        Assert.NotNull(body);
        Assert.True(SyncCodec.TryDecode(body, out var decoded, out var error), error);
        Assert.Equal(message.Id, decoded!.Id);
        Assert.Equal(0x27, decoded.Key!.VirtualKey);
        Assert.Equal(KeyPhase.Down, decoded.Key.Phase);
    }

    [Fact]
    public void Two_frames_in_one_chunk_are_both_read()
    {
        var first = SyncCodec.EncodeFrame(SampleKey());
        var second = SyncCodec.EncodeFrame(SyncMessage.Create("a", "A", "show", SyncKind.Slide, 2));
        var chunk = first.Concat(second).ToArray();
        var reader = new FrameReader();
        reader.Append(chunk);
        Assert.True(reader.TryRead(out _));
        Assert.True(reader.TryRead(out _));
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void A_huge_length_is_rejected()
    {
        var reader = new FrameReader();
        var tooBig = FrameReader.MaxFrameBytes + 1;
        var header = new byte[]
        {
            (byte)tooBig,
            (byte)(tooBig >> 8),
            (byte)(tooBig >> 16),
            (byte)(tooBig >> 24)
        };
        reader.Append(header);
        Assert.Throws<InvalidDataException>(() => reader.TryRead(out _));
    }

    [Fact]
    public void Codec_keeps_slide_mouse_and_channel()
    {
        var message = SyncMessage.Create("laptop-a", "SHOW-PC", "main hall", SyncKind.Slide, 9);
        message.Slide = new SlideEvent(@"C:\Shows\Keynote.pptx", 4, 2, 22, true, ShowScreen.Black);
        message.Mouse = new MouseEvent(MousePhase.Down, MouseButton.Left, 0.25, 0.5, 0);
        var frame = SyncCodec.EncodeFrame(message);
        Assert.True(SyncCodec.TryDecode(frame.AsSpan(4), out var decoded, out var error), error);
        Assert.Equal("main hall", decoded!.Channel);
        Assert.Equal(4, decoded.Slide!.SlideIndex);
        Assert.Equal(ShowScreen.Black, decoded.Slide.Screen);
        Assert.Equal(0.25, decoded.Mouse!.X);
        Assert.False(SlideEvent.SamePosition(
            decoded.Slide,
            decoded.Slide with { DeckName = "other.pptx", Screen = ShowScreen.Running }));
        Assert.True(SlideEvent.SamePosition(decoded.Slide, decoded.Slide with { DeckName = "other.pptx" }));
    }

    [Fact]
    public void Damaged_json_and_the_wrong_version_are_ignored()
    {
        Assert.False(SyncCodec.TryDecode("{"u8, out _, out var damaged));
        Assert.Contains("damaged", damaged, StringComparison.OrdinalIgnoreCase);

        var json = Encoding.UTF8.GetBytes("""{"v":2,"id":"abc","kind":"key"}""");
        Assert.False(SyncCodec.TryDecode(json, out _, out _));
    }

    [Fact]
    public void Channel_match_ignores_nothing_but_the_exact_name()
    {
        Assert.True(ChannelGuard.Matches("show", "show"));
        Assert.False(ChannelGuard.Matches("show", "Show"));
        Assert.False(ChannelGuard.Matches("show", ""));
    }

    [Theory]
    [InlineData("0.0.0.0", "24710", "192.168.1.20", "24710", "show", true, true)]
    [InlineData("0.0.0.0", "24710", "", "24710", "show", false, true)]
    [InlineData("0.0.0.0", "24710", "", "24710", "show", true, false)]
    [InlineData("nope", "24710", "10.0.0.2", "24710", "show", true, false)]
    [InlineData("0.0.0.0", "0", "10.0.0.2", "24710", "show", true, false)]
    [InlineData("0.0.0.0", "24710", "10.0.0.2", "24710", "", true, false)]
    public void Link_form_checks_the_addresses(
        string bind, string listen, string remote, string remotePort, string channel, bool needsRemote, bool ok)
    {
        var parsed = LinkForm.TryParse(
            new LinkRequest(bind, listen, remote, remotePort, channel, needsRemote),
            out var endpoints,
            out var error);
        Assert.Equal(ok, parsed);
        if (ok)
            Assert.NotNull(endpoints);
        else
            Assert.False(string.IsNullOrWhiteSpace(error));
    }

    private static SyncMessage SampleKey()
    {
        var message = SyncMessage.Create("laptop-a", "SHOW-PC", "show", SyncKind.Key, 1);
        message.Key = new KeyEvent(0x27, 0, KeyPhase.Down, false, false, false);
        return message;
    }
}
