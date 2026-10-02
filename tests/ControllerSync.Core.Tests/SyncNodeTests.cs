namespace ControllerSync.Core.Tests;

public class SyncNodeTests
{
    [Fact]
    public async Task Two_laptops_exchange_a_next_cue()
    {
        await using var backup = new SyncNode();
        await backup.StartAsync(new SyncNodeOptions
        {
            BindAddress = "127.0.0.1",
            ListenPort = 0,
            Channel = "show",
            MachineId = "backup",
            MachineName = "BACKUP-PC"
        });

        var got = new TaskCompletionSource<SyncMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        backup.MessageReceived += message =>
        {
            if (message.Kind == SyncKind.Cue)
                got.TrySetResult(message);
        };

        await using var primary = new SyncNode();
        await primary.StartAsync(new SyncNodeOptions
        {
            BindAddress = "127.0.0.1",
            ListenPort = 0,
            RemoteAddress = "127.0.0.1",
            RemotePort = backup.BoundPort,
            Channel = "show",
            MachineId = "primary",
            MachineName = "SHOW-PC"
        });

        var linked = DateTime.UtcNow.AddSeconds(5);
        while (!primary.IsConnected && DateTime.UtcNow < linked)
            await Task.Delay(20);
        Assert.True(primary.IsConnected);

        var cue = SyncMessage.Create("primary", "SHOW-PC", "show", SyncKind.Cue, 1);
        cue.Action = ShowAction.Next;
        await primary.SendAsync(cue);

        var received = await got.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ShowAction.Next, received.Action);
        Assert.Equal("SHOW-PC", received.Name);
    }

    [Fact]
    public async Task A_different_channel_is_ignored()
    {
        await using var backup = new SyncNode();
        var notices = new System.Collections.Concurrent.ConcurrentQueue<string>();
        backup.Notice += notices.Enqueue;
        await backup.StartAsync(new SyncNodeOptions
        {
            BindAddress = "127.0.0.1",
            ListenPort = 0,
            Channel = "show",
            MachineId = "backup",
            MachineName = "BACKUP-PC"
        });

        var got = new TaskCompletionSource<SyncMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        backup.MessageReceived += message => got.TrySetResult(message);

        await using var primary = new SyncNode();
        await primary.StartAsync(new SyncNodeOptions
        {
            BindAddress = "127.0.0.1",
            ListenPort = 0,
            RemoteAddress = "127.0.0.1",
            RemotePort = backup.BoundPort,
            Channel = "show",
            MachineId = "primary",
            MachineName = "SHOW-PC"
        });

        var linked = DateTime.UtcNow.AddSeconds(5);
        while (!primary.IsConnected && DateTime.UtcNow < linked)
            await Task.Delay(20);

        var cue = SyncMessage.Create("primary", "SHOW-PC", "rehearsal", SyncKind.Cue, 4);
        cue.Action = ShowAction.Next;
        await primary.SendAsync(cue);
        await Task.Delay(300);

        Assert.False(got.Task.IsCompleted);
        Assert.Contains(notices, notice => notice.Contains("channel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_frame_split_across_packets_still_arrives()
    {
        await using var backup = new SyncNode();
        await backup.StartAsync(new SyncNodeOptions
        {
            BindAddress = "127.0.0.1",
            ListenPort = 0,
            Channel = "show",
            MachineId = "backup",
            MachineName = "BACKUP-PC"
        });

        var got = new TaskCompletionSource<SyncMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        backup.MessageReceived += message =>
        {
            if (message.Kind == SyncKind.Key)
                got.TrySetResult(message);
        };

        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync("127.0.0.1", backup.BoundPort);
        var message = SyncMessage.Create("primary", "SHOW-PC", "show", SyncKind.Key, 1);
        message.Key = new KeyEvent(PresentationKeys.Right, 0, KeyPhase.Down, false, false, false);
        var frame = SyncCodec.EncodeFrame(message);
        var stream = client.GetStream();
        await stream.WriteAsync(frame.AsMemory(0, 3));
        await Task.Delay(50);
        await stream.WriteAsync(frame.AsMemory(3));

        var received = await got.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(PresentationKeys.Right, received.Key!.VirtualKey);
    }

    [Fact]
    public async Task Show_link_over_the_network_delivers_next_to_the_backup()
    {
        await using var backupNode = new SyncNode();
        await backupNode.StartAsync(new SyncNodeOptions
        {
            BindAddress = "127.0.0.1",
            ListenPort = 0,
            Channel = "hall",
            MachineId = "backup",
            MachineName = "BACKUP-PC"
        });

        await using var primaryNode = new SyncNode();
        await primaryNode.StartAsync(new SyncNodeOptions
        {
            BindAddress = "127.0.0.1",
            ListenPort = 0,
            RemoteAddress = "127.0.0.1",
            RemotePort = backupNode.BoundPort,
            Channel = "hall",
            MachineId = "primary",
            MachineName = "SHOW-PC"
        });

        var cue = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var backup = new ShowLink(
            backupNode,
            new IdleInput(),
            new IdleInjector(),
            new IdleSlides(),
            new ShowLinkOptions
            {
                FollowSlides = true,
                AcceptKeyboard = true,
                Channel = "hall",
                MachineId = "backup",
                MachineName = "BACKUP-PC"
            },
            backgroundLoops: false);
        backup.Cue += text => cue.TrySetResult(text);
        backup.Start();

        using var primary = new ShowLink(
            primaryNode,
            new IdleInput(),
            new IdleInjector(),
            new IdleSlides(),
            new ShowLinkOptions
            {
                PublishSlides = true,
                SendKeyboard = true,
                Channel = "hall",
                MachineId = "primary",
                MachineName = "SHOW-PC"
            });
        primary.Start();

        var linked = DateTime.UtcNow.AddSeconds(5);
        while (!primaryNode.IsConnected && DateTime.UtcNow < linked)
            await Task.Delay(20);
        Assert.True(primaryNode.IsConnected);

        primary.Nudge(ShowAction.Next);
        var shown = await cue.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("NEXT", shown);
    }

    [Fact]
    public async Task The_second_listener_on_a_port_gets_a_clear_error()
    {
        await using var first = new SyncNode();
        await first.StartAsync(new SyncNodeOptions
        {
            BindAddress = "127.0.0.1",
            ListenPort = 0,
            Channel = "show",
            MachineId = "a",
            MachineName = "A"
        });

        var second = new SyncNode();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync(new SyncNodeOptions
        {
            BindAddress = "127.0.0.1",
            ListenPort = first.BoundPort,
            Channel = "show",
            MachineId = "b",
            MachineName = "B"
        }));
        Assert.Contains("already in use", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class IdleInput : ILocalInputSource
    {
        public event Action<KeyEvent>? Key { add { } remove { } }
        public event Action<MouseEvent>? Mouse { add { } remove { } }
        public void Start() { }
        public void Stop() { }
    }

    private sealed class IdleInjector : ILocalInputInjector
    {
        public void InjectKey(KeyEvent key) { }
        public void InjectMouse(MouseEvent mouse) { }
    }

    private sealed class IdleSlides : ISlideBridge
    {
        public SlideEvent? TryRead() => null;
        public SlideApplyResult Apply(SlideEvent slide) => new(false, null);
        public bool TryStep(ShowAction action) => false;
    }
}
