namespace ControllerSync.Core.Tests;

public class ShowLinkTests
{
    [Fact]
    public void Primary_in_a_show_sends_the_slide_instead_of_the_arrow_key()
    {
        var transport = new FakeTransport();
        var slides = new FakeSlides
        {
            Current = new SlideEvent("deck.pptx", 3, 1, 12, true, ShowScreen.Running)
        };
        var input = new FakeInput();
        var link = new ShowLink(
            transport,
            input,
            new FakeInjector(),
            slides,
            PrimaryOptions(),
            inlineSend: true,
            backgroundLoops: false);
        link.Start();
        transport.Sent.Clear();

        input.Push(new KeyEvent(PresentationKeys.Right, 0, KeyPhase.Down, false, false, false));
        slides.Current = slides.Current with { SlideIndex = 4 };
        link.PollOnce();

        Assert.DoesNotContain(transport.Sent, message => message.Kind == SyncKind.Key);
        var slide = Assert.Single(transport.Sent, message => message.Kind == SyncKind.Slide);
        Assert.Equal(4, slide.Slide!.SlideIndex);
        link.Dispose();
    }

    [Fact]
    public void Before_the_show_starts_arrow_keys_are_mirrored()
    {
        var transport = new FakeTransport();
        var slides = new FakeSlides
        {
            Current = new SlideEvent("deck.pptx", 1, 0, 12, false, ShowScreen.Done)
        };
        var input = new FakeInput();
        var link = new ShowLink(
            transport,
            input,
            new FakeInjector(),
            slides,
            PrimaryOptions(),
            inlineSend: true,
            backgroundLoops: false);
        link.Start();
        transport.Sent.Clear();

        input.Push(new KeyEvent(PresentationKeys.F5, 0, KeyPhase.Down, false, false, false));
        Assert.Contains(transport.Sent, message => message.Key?.VirtualKey == PresentationKeys.F5);
        link.Dispose();
    }

    [Fact]
    public void A_missing_PowerPoint_does_not_tell_the_backup_to_exit()
    {
        var transport = new FakeTransport();
        var slides = new FakeSlides();
        var link = new ShowLink(
            transport,
            new FakeInput(),
            new FakeInjector(),
            slides,
            PrimaryOptions(),
            inlineSend: true,
            backgroundLoops: false);
        link.Start();
        Assert.DoesNotContain(transport.Sent, message => message.Slide is { InShow: false });
        link.Dispose();
    }

    [Fact]
    public void Backup_applies_the_slide_and_does_not_also_press_the_key()
    {
        var injector = new FakeInjector();
        var slides = new FakeSlides();
        var transport = new FakeTransport();
        var cues = new List<string>();
        var link = new ShowLink(
            transport,
            new FakeInput(),
            injector,
            slides,
            BackupOptions(),
            inlineSend: true,
            backgroundLoops: false);
        link.Cue += cues.Add;
        link.Start();

        var slide = SyncMessage.Create("primary", "SHOW-PC", "show", SyncKind.Slide, 1);
        slide.Slide = new SlideEvent("deck.pptx", 6, 1, 12, true, ShowScreen.Running);
        transport.Receive(slide);

        var key = SyncMessage.Create("primary", "SHOW-PC", "show", SyncKind.Key, 2);
        key.Key = new KeyEvent(PresentationKeys.Right, 0, KeyPhase.Down, false, false, false);
        transport.Receive(key);

        var applied = Assert.Single(slides.Applied);
        Assert.Equal(6, applied.SlideIndex);
        Assert.Empty(injector.Keys);
        Assert.Contains("SLIDE 6", cues);
        link.Dispose();
    }

    [Fact]
    public void Same_slide_heartbeat_does_not_flash_the_cue_again()
    {
        var slides = new FakeSlides();
        var transport = new FakeTransport();
        var cues = new List<string>();
        var link = new ShowLink(
            transport,
            new FakeInput(),
            new FakeInjector(),
            slides,
            BackupOptions(),
            inlineSend: true,
            backgroundLoops: false);
        link.Cue += cues.Add;
        link.Start();

        var slide = SyncMessage.Create("primary", "SHOW-PC", "show", SyncKind.Slide, 1);
        slide.Slide = new SlideEvent("deck.pptx", 2, 1, 8, true, ShowScreen.Black);
        transport.Receive(slide);
        slide.Id = Guid.NewGuid().ToString("N");
        transport.Receive(slide);

        Assert.Equal(new[] { "BLACK" }, cues);
        Assert.Single(slides.Applied);
        link.Dispose();
    }

    [Fact]
    public void Nudge_without_PowerPoint_sends_a_cue_the_backup_can_show()
    {
        var transport = new FakeTransport();
        var link = new ShowLink(
            transport,
            new FakeInput(),
            new FakeInjector(),
            new FakeSlides(),
            PrimaryOptions(),
            inlineSend: true,
            backgroundLoops: false);
        link.Start();
        transport.Sent.Clear();
        link.Nudge(ShowAction.Next);

        var cue = Assert.Single(transport.Sent);
        Assert.Equal(SyncKind.Cue, cue.Kind);
        Assert.Equal(ShowAction.Next, cue.Action);
        link.Dispose();
    }

    [Fact]
    public void Letters_are_not_sent_unless_every_key_is_enabled()
    {
        var transport = new FakeTransport();
        var input = new FakeInput();
        var link = new ShowLink(
            transport,
            input,
            new FakeInjector(),
            new FakeSlides(),
            PrimaryOptions(),
            inlineSend: true,
            backgroundLoops: false);
        link.Start();
        transport.Sent.Clear();
        input.Push(new KeyEvent(0x41, 0, KeyPhase.Down, false, false, false));
        Assert.Empty(transport.Sent);
        link.Dispose();
    }

    private static ShowLinkOptions PrimaryOptions() => new()
    {
        SendKeyboard = true,
        SendMouseClicks = true,
        PublishSlides = true,
        FollowSlides = false,
        AcceptKeyboard = false,
        AcceptMouse = false,
        Channel = "show",
        MachineId = "primary",
        MachineName = "SHOW-PC"
    };

    private static ShowLinkOptions BackupOptions() => new()
    {
        SendKeyboard = false,
        PublishSlides = false,
        FollowSlides = true,
        AcceptKeyboard = true,
        AcceptMouse = true,
        Channel = "show",
        MachineId = "backup",
        MachineName = "BACKUP-PC"
    };

    private sealed class FakeTransport : ISyncTransport
    {
        public bool IsConnected { get; set; } = true;
        public List<SyncMessage> Sent { get; } = new();
        public event Action<SyncMessage>? MessageReceived;
        public Task SendAsync(SyncMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public void Receive(SyncMessage message) => MessageReceived?.Invoke(message);
    }

    private sealed class FakeSlides : ISlideBridge
    {
        public SlideEvent? Current { get; set; }
        public List<SlideEvent> Applied { get; } = new();
        public SlideEvent? TryRead() => Current;
        public SlideApplyResult Apply(SlideEvent slide)
        {
            Applied.Add(slide);
            Current = slide;
            return new SlideApplyResult(true, null);
        }

        public bool TryStep(ShowAction action) => false;
    }

    private sealed class FakeInput : ILocalInputSource
    {
        public event Action<KeyEvent>? Key;
        public event Action<MouseEvent>? Mouse;
        public void Start() { }
        public void Stop() { }
        public void Push(KeyEvent key) => Key?.Invoke(key);
        public void Push(MouseEvent mouse) => Mouse?.Invoke(mouse);
    }

    private sealed class FakeInjector : ILocalInputInjector
    {
        public List<KeyEvent> Keys { get; } = new();
        public void InjectKey(KeyEvent key) => Keys.Add(key);
        public void InjectMouse(MouseEvent mouse) { }
    }
}
