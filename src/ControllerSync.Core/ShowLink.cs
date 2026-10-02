using System.Threading.Channels;

namespace ControllerSync.Core;

public sealed class ShowLink : IDisposable
{
    private readonly ISyncTransport _transport;
    private readonly ILocalInputSource _input;
    private readonly ILocalInputInjector _injector;
    private readonly ISlideBridge _slides;
    private readonly ShowLinkOptions _options;
    private readonly Func<DateTime> _now;
    private readonly bool _inlineSend;
    private readonly bool _backgroundLoops;
    private readonly SlideFollowPolicy _policy = new();
    private readonly object _pollGate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<SyncMessage> _queue = Channel.CreateBounded<SyncMessage>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });

    private long _seq;
    private bool _localInShow;
    private bool _remoteInShow;
    private SlideEvent? _lastRemote;
    private SlideEvent? _lastLogged;
    private DateTime _lastSlideSentAt = DateTime.MinValue;
    private string? _lastWarning;
    private int _catchUp;
    private int _started;
    private Task? _sendTask;
    private Task? _pollTask;

    public ShowLink(
        ISyncTransport transport,
        ILocalInputSource input,
        ILocalInputInjector injector,
        ISlideBridge slides,
        ShowLinkOptions options,
        Func<DateTime>? now = null,
        bool inlineSend = false,
        bool backgroundLoops = true)
    {
        _transport = transport;
        _input = input;
        _injector = injector;
        _slides = slides;
        _options = options;
        _now = now ?? (() => DateTime.UtcNow);
        _inlineSend = inlineSend;
        _backgroundLoops = backgroundLoops && !inlineSend;
        _policy.PublishEnabled = options.PublishSlides;
    }

    public event Action<string>? Log;
    public event Action<string>? Cue;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        _transport.MessageReceived += OnRemote;
        _input.Key += OnLocalKey;
        _input.Mouse += OnLocalMouse;
        if (_backgroundLoops)
        {
            _sendTask = Task.Run(() => SendLoop(_cts.Token));
            _pollTask = Task.Run(() => PollLoop(_cts.Token));
        }

        PollOnce();
        if (_options.SendKeyboard || _options.SendMouseClicks || _options.SendMouseMove)
            _input.Start();

        Log?.Invoke(_options.PublishSlides
            ? "Publishing the PowerPoint position on this laptop."
            : "This laptop will follow cues from the other one.");
    }

    public void PollOnce()
    {
        if (!_options.PublishSlides)
            return;

        lock (_pollGate)
        {
            var local = _slides.TryRead();
            if (local == null)
                return;

            _localInShow = local.InShow;
            var heartbeat = _now() - _lastSlideSentAt >= TimeSpan.FromSeconds(2);
            if (!_policy.ShouldPublish(local, heartbeat))
                return;

            _lastSlideSentAt = _now();
            if (_lastLogged == null || !SlideEvent.SamePosition(_lastLogged, local))
            {
                _lastLogged = local;
                Log?.Invoke(local.InShow
                    ? $"Show is on slide {local.SlideIndex} of {Math.Max(local.SlideCount, local.SlideIndex)}."
                    : "Slideshow is not running on this laptop.");
            }

            var message = New(SyncKind.Slide);
            message.Slide = local;
            Enqueue(message);
        }
    }

    public void Nudge(ShowAction action)
    {
        if (action == ShowAction.None)
            return;

        if (_options.PublishSlides && _slides.TryStep(action))
        {
            Log?.Invoke($"Moved this laptop ({CueText.Label(action)}). The other laptop follows the slide.");
            PollOnce();
            Interlocked.Exchange(ref _catchUp, 15);
            if (!_transport.IsConnected)
                Log?.Invoke("The other laptop is not linked yet.");
            return;
        }

        if (!_transport.IsConnected && !_inlineSend)
        {
            Log?.Invoke("Not linked yet. Start ControllerSync on the other laptop and check the outgoing IP.");
            return;
        }

        var message = New(SyncKind.Cue);
        message.Action = action;
        Enqueue(message);
        Log?.Invoke("Sent " + CueText.Label(action) + " to the other laptop.");
    }

    public void Dispose()
    {
        _transport.MessageReceived -= OnRemote;
        _input.Key -= OnLocalKey;
        _input.Mouse -= OnLocalMouse;
        _cts.Cancel();
        try
        {
            _input.Stop();
        }
        catch
        {
            // The hook thread is already gone.
        }

        _queue.Writer.TryComplete();
    }

    private async Task PollLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var fast = Volatile.Read(ref _catchUp) > 0;
                await Task.Delay(fast ? 30 : 150, cancellationToken).ConfigureAwait(false);
                if (fast)
                    Interlocked.Decrement(ref _catchUp);
                PollOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        catch (ObjectDisposedException)
        {
            // Stopping.
        }
    }

    private async Task SendLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var message))
                {
                    if (!_transport.IsConnected)
                        continue;
                    try
                    {
                        await _transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke("Could not send: " + ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private void OnLocalKey(KeyEvent key)
    {
        if (!_options.SendKeyboard || key.VirtualKey == 0)
            return;

        if (key.Phase == KeyPhase.Down && _options.PublishSlides && PresentationKeys.IsShowControl(key))
            Interlocked.Exchange(ref _catchUp, 15);

        var mirror = _options.SendAllKeys || PresentationKeys.IsShowControl(key);
        if (!mirror)
            return;
        if (!InputGate.ShouldSendKey(_options.PublishSlides, _localInShow, key))
            return;

        var message = New(SyncKind.Key);
        message.Key = key;
        Enqueue(message);
    }

    private void OnLocalMouse(MouseEvent mouse)
    {
        if (!InputGate.ShouldSendMouse(
                _options.PublishSlides,
                _localInShow,
                mouse.Phase,
                _options.SendMouseClicks,
                _options.SendMouseMove))
        {
            if (mouse.Phase is MousePhase.Down or MousePhase.Wheel && _options.PublishSlides)
                Interlocked.Exchange(ref _catchUp, 15);
            return;
        }

        var message = New(SyncKind.Mouse);
        message.Mouse = mouse;
        Enqueue(message);
    }

    private void OnRemote(SyncMessage message)
    {
        if (message.Kind == SyncKind.Slide && message.Slide is { } slide)
        {
            _remoteInShow = slide.InShow;
            if (!_options.FollowSlides)
                return;
            if (_lastRemote != null && SlideEvent.SamePosition(_lastRemote, slide))
                return;

            _lastRemote = slide;
            var text = CueText.From(message);
            if (text.Length > 0)
            {
                Cue?.Invoke(text);
                Log?.Invoke(Peer(message) + " → " + text);
            }

            var result = _slides.Apply(slide);
            if (result.Applied)
                _policy.NoteRemoteApplied(slide);
            if (!string.IsNullOrEmpty(result.Warning) && result.Warning != _lastWarning)
            {
                _lastWarning = result.Warning;
                Log?.Invoke(result.Warning);
            }

            return;
        }

        if (message.Kind == SyncKind.Cue)
        {
            var text = CueText.Label(message.Action);
            if (text.Length == 0 || message.Action == ShowAction.None)
                return;
            Cue?.Invoke(text);
            Log?.Invoke(Peer(message) + " → " + text);
            if (_options.FollowSlides && _slides.TryStep(message.Action))
                return;
            if (!_options.AcceptKeyboard)
                return;
            _injector.InjectKey(PresentationKeys.Make(message.Action, KeyPhase.Down));
            _injector.InjectKey(PresentationKeys.Make(message.Action, KeyPhase.Up));
            return;
        }

        if (message.Kind == SyncKind.Key && message.Key is { } key)
        {
            if (!_options.AcceptKeyboard)
                return;
            var text = CueText.From(message);
            if (text.Length > 0)
            {
                Cue?.Invoke(text);
                Log?.Invoke(Peer(message) + " → " + text);
            }

            if (!InputGate.ShouldInjectKey(_options.FollowSlides, _remoteInShow, key))
                return;
            _injector.InjectKey(key);
            return;
        }

        if (message.Kind == SyncKind.Mouse && message.Mouse is { } mouse)
        {
            if (!_options.AcceptMouse)
                return;
            var text = CueText.From(message);
            if (text.Length > 0)
            {
                Cue?.Invoke(text);
                Log?.Invoke(Peer(message) + " → " + text);
            }

            if (!InputGate.ShouldInjectMouse(_options.FollowSlides, _remoteInShow, mouse.Phase))
                return;
            _injector.InjectMouse(mouse);
        }
    }

    private void Enqueue(SyncMessage message)
    {
        if (_inlineSend)
        {
            _transport.SendAsync(message).GetAwaiter().GetResult();
            return;
        }

        if (!_queue.Writer.TryWrite(message))
            Log?.Invoke("The link is busy, so a cue was dropped.");
    }

    private SyncMessage New(SyncKind kind)
    {
        var seq = Interlocked.Increment(ref _seq);
        return SyncMessage.Create(_options.MachineId, _options.MachineName, _options.Channel, kind, seq);
    }

    private static string Peer(SyncMessage message) =>
        string.IsNullOrWhiteSpace(message.Name) ? "Other laptop" : message.Name;
}
