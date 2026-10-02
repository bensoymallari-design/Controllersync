using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using ControllerSync.App.Platform;
using ControllerSync.App.Services;
using ControllerSync.Core;

namespace ControllerSync.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly string _settingsDir;
    private readonly string _machineId;
    private readonly string _machineName;
    private readonly DispatcherTimer _ipTimer;
    private readonly RelayCommand _startCommand;
    private readonly RelayCommand _stopCommand;
    private readonly RelayCommand _previousCommand;
    private readonly RelayCommand _nextCommand;
    private readonly RelayCommand _blackCommand;
    private readonly RelayCommand _sendContentCommand;
    private readonly ResolumeLoader _resolume = new();
    private bool _loading;
    private bool _disposed;
    private SyncNode? _node;
    private ShowLink? _link;
    private IDisposable? _slides;
    private MediaServer? _media;

    private string _bindAddress = "0.0.0.0";
    private string _listenPortText = "24710";
    private string _remoteAddress = "";
    private string _remotePortText = "24710";
    private string _channel = "show";
    private string _role = "Primary";
    private bool _sendKeyboard = true;
    private bool _sendAllKeys;
    private bool _sendMouseClicks = true;
    private bool _sendMouseMove;
    private bool _publishSlides = true;
    private bool _followSlides;
    private bool _acceptKeyboard;
    private bool _acceptMouse;
    private bool _isRunning;
    private bool _isLinked;
    private string _linkDetail = "Not started";
    private string _tallyText = "OFF";
    private string _banner = "READY";
    private string _formError = "";
    private string _roleHelp = "";
    private string _thisLaptopIp = "No address yet";
    private string _contentPath = "";
    private string _layerText = "1";
    private string _clipText = "1";
    private string _resolumePortText = "8080";
    private bool _playAfterLoad = true;
    private bool _loadOnThisLaptop = true;
    private string _extraLaptops = "";
    private bool _isSending;
    private double _contentProgress;
    private string _contentStatus = "";

    public MainViewModel()
    {
        _settingsDir = SettingsStore.ChooseDirectory();
        try
        {
            _machineId = MachineIdentity.GetOrCreate();
        }
        catch
        {
            _machineId = Guid.NewGuid().ToString("N");
        }

        _machineName = Environment.MachineName;
        _startCommand = new RelayCommand(StartAsync, () => !IsRunning);
        _stopCommand = new RelayCommand(Stop, () => IsRunning);
        _previousCommand = new RelayCommand(() => Nudge(ShowAction.Previous), () => IsRunning);
        _nextCommand = new RelayCommand(() => Nudge(ShowAction.Next), () => IsRunning);
        _blackCommand = new RelayCommand(() => Nudge(ShowAction.BlackScreen), () => IsRunning);
        _sendContentCommand = new RelayCommand(SendContentAsync, () => IsRunning && !_isSending);
        SetRoleCommand = new RelayCommand(parameter =>
        {
            ApplyRole(parameter as string);
            return Task.CompletedTask;
        }, _ => !IsRunning);
        CopyIpCommand = new RelayCommand(CopyIpAsync);
        FirewallCommand = new RelayCommand(() =>
        {
            AllowFirewall();
            return Task.CompletedTask;
        });
        PickContentCommand = new RelayCommand(PickContentAsync);

        Load();
        RefreshIps();
        UpdateRoleHelp();
        _ipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _ipTimer.Tick += (_, _) => RefreshIps();
        _ipTimer.Start();
        AddLog("This laptop is " + _machineName + ".");
        AddLog("Use the same channel on both laptops, then start the link.");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> LogLines { get; } = new();

    public Func<string, Task>? CopyText { get; set; }

    public Func<Task<string?>>? PickFile { get; set; }

    public string MachineName => _machineName;
    public bool IsWindows { get; } = OperatingSystem.IsWindows();

    public RelayCommand StartCommand => _startCommand;
    public RelayCommand StopCommand => _stopCommand;
    public RelayCommand PreviousCommand => _previousCommand;
    public RelayCommand NextCommand => _nextCommand;
    public RelayCommand BlackCommand => _blackCommand;
    public RelayCommand SetRoleCommand { get; }
    public RelayCommand CopyIpCommand { get; }
    public RelayCommand FirewallCommand { get; }
    public RelayCommand SendContentCommand => _sendContentCommand;
    public RelayCommand PickContentCommand { get; }

    public string BindAddress
    {
        get => _bindAddress;
        set => Set(ref _bindAddress, value.Trim(), save: true);
    }

    public string ListenPortText
    {
        get => _listenPortText;
        set
        {
            if (Set(ref _listenPortText, value.Trim(), save: true))
                Raise(nameof(ThisLaptopHint));
        }
    }

    public string RemoteAddress
    {
        get => _remoteAddress;
        set => Set(ref _remoteAddress, value.Trim(), save: true);
    }

    public string RemotePortText
    {
        get => _remotePortText;
        set => Set(ref _remotePortText, value.Trim(), save: true);
    }

    public string Channel
    {
        get => _channel;
        set => Set(ref _channel, value.Trim(), save: true);
    }

    public string Role
    {
        get => _role;
        private set
        {
            if (!Set(ref _role, value, save: true))
                return;
            Raise(nameof(IsPrimary));
            Raise(nameof(IsBackup));
            Raise(nameof(IsTwoWay));
            UpdateRoleHelp();
        }
    }

    public bool IsPrimary => Role == "Primary";
    public bool IsBackup => Role == "Backup";
    public bool IsTwoWay => Role == "TwoWay";

    public bool SendKeyboard
    {
        get => _sendKeyboard;
        set => SetOption(ref _sendKeyboard, value);
    }

    public bool SendAllKeys
    {
        get => _sendAllKeys;
        set => SetOption(ref _sendAllKeys, value);
    }

    public bool SendMouseClicks
    {
        get => _sendMouseClicks;
        set => SetOption(ref _sendMouseClicks, value);
    }

    public bool SendMouseMove
    {
        get => _sendMouseMove;
        set => SetOption(ref _sendMouseMove, value);
    }

    public bool PublishSlides
    {
        get => _publishSlides;
        set => SetOption(ref _publishSlides, value);
    }

    public bool FollowSlides
    {
        get => _followSlides;
        set => SetOption(ref _followSlides, value);
    }

    public bool AcceptKeyboard
    {
        get => _acceptKeyboard;
        set => SetOption(ref _acceptKeyboard, value);
    }

    public bool AcceptMouse
    {
        get => _acceptMouse;
        set => SetOption(ref _acceptMouse, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value, save: false))
                return;
            UpdateTally();
            RefreshCommands();
        }
    }

    public bool IsLinked
    {
        get => _isLinked;
        private set
        {
            if (Set(ref _isLinked, value, save: false))
                UpdateTally();
        }
    }

    public string LinkDetail
    {
        get => _linkDetail;
        private set => Set(ref _linkDetail, value, save: false);
    }

    public string TallyText
    {
        get => _tallyText;
        private set => Set(ref _tallyText, value, save: false);
    }

    public string Banner
    {
        get => _banner;
        private set => Set(ref _banner, value, save: false);
    }

    public string FormError
    {
        get => _formError;
        private set
        {
            if (Set(ref _formError, value, save: false))
                Raise(nameof(HasError));
        }
    }

    public bool HasError => FormError.Length > 0;

    public string RoleHelp
    {
        get => _roleHelp;
        private set => Set(ref _roleHelp, value, save: false);
    }

    public string ThisLaptopIp
    {
        get => _thisLaptopIp;
        private set
        {
            if (Set(ref _thisLaptopIp, value, save: false))
                Raise(nameof(ThisLaptopHint));
        }
    }

    public string ThisLaptopHint =>
        "On the other laptop, set outgoing IP to " + ThisLaptopIp + " and outgoing port to " + ListenPortText + ".";

    public string ContentPath
    {
        get => _contentPath;
        set => Set(ref _contentPath, value, save: true);
    }

    public string LayerText
    {
        get => _layerText;
        set => Set(ref _layerText, value.Trim(), save: true);
    }

    public string ClipText
    {
        get => _clipText;
        set => Set(ref _clipText, value.Trim(), save: true);
    }

    public string ResolumePortText
    {
        get => _resolumePortText;
        set => Set(ref _resolumePortText, value.Trim(), save: true);
    }

    public bool PlayAfterLoad
    {
        get => _playAfterLoad;
        set => Set(ref _playAfterLoad, value, save: true);
    }

    public bool LoadOnThisLaptop
    {
        get => _loadOnThisLaptop;
        set => Set(ref _loadOnThisLaptop, value, save: true);
    }

    public string ExtraLaptops
    {
        get => _extraLaptops;
        set => Set(ref _extraLaptops, value, save: true);
    }

    public bool IsSending
    {
        get => _isSending;
        private set
        {
            if (!Set(ref _isSending, value, save: false))
                return;
            _sendContentCommand.RaiseCanExecuteChanged();
        }
    }

    public double ContentProgress
    {
        get => _contentProgress;
        private set => Set(ref _contentProgress, value, save: false);
    }

    public string ContentStatus
    {
        get => _contentStatus;
        private set
        {
            if (Set(ref _contentStatus, value, save: false))
                Raise(nameof(HasContentStatus));
        }
    }

    public bool HasContentStatus => ContentStatus.Length > 0;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ipTimer.Stop();
        Stop();
        SaveSettings();
    }

    private async Task StartAsync()
    {
        if (IsRunning)
            return;
        FormError = "";
        RefreshIps();
        var needsRemote = SendKeyboard || SendAllKeys || SendMouseClicks || SendMouseMove || PublishSlides;
        if (!LinkForm.TryParse(
                new LinkRequest(BindAddress, ListenPortText, RemoteAddress, RemotePortText, Channel, needsRemote),
                out var endpoints,
                out var error)
            || endpoints == null)
        {
            FormError = error ?? "Check the IP addresses.";
            return;
        }

        SaveSettings();
        var node = new SyncNode();
        var slides = PlatformHost.CreateSlides();
        var link = new ShowLink(
            node,
            PlatformHost.CreateInput(),
            PlatformHost.CreateInjector(),
            slides,
            new ShowLinkOptions
            {
                SendKeyboard = SendKeyboard,
                SendAllKeys = SendAllKeys,
                SendMouseClicks = SendMouseClicks,
                SendMouseMove = SendMouseMove,
                PublishSlides = PublishSlides,
                FollowSlides = FollowSlides,
                AcceptKeyboard = AcceptKeyboard,
                AcceptMouse = AcceptMouse,
                Channel = endpoints.Channel,
                MachineId = _machineId,
                MachineName = _machineName
            });

        node.Notice += line => Ui(() => AddLog(line));
        node.LinkChanged += snapshot => Ui(() => ApplyLink(snapshot));
        link.Log += line => Ui(() => AddLog(line));
        link.Cue += text => Ui(() => Banner = text);

        try
        {
            await node.StartAsync(new SyncNodeOptions
            {
                BindAddress = endpoints.BindAddress,
                ListenPort = endpoints.ListenPort,
                RemoteAddress = endpoints.RemoteHost,
                RemotePort = endpoints.RemotePort,
                Channel = endpoints.Channel,
                MachineId = _machineId,
                MachineName = _machineName
            });
            link.Start();
        }
        catch (Exception ex)
        {
            link.Dispose();
            if (slides is IDisposable disposableSlides)
                disposableSlides.Dispose();
            await node.DisposeAsync();
            FormError = ex.Message;
            AddLog(ex.Message);
            return;
        }

        _node = node;
        _link = link;
        _slides = slides as IDisposable;
        IsRunning = true;
        Banner = "READY";
        AddLog("Listening on " + endpoints.BindAddress + ":" + node.BoundPort + ".");
        if (!string.IsNullOrWhiteSpace(endpoints.RemoteHost))
            AddLog("Outgoing address is " + endpoints.RemoteHost + ":" + endpoints.RemotePort + ".");
        AddLog("Click the PowerPoint window when you are ready. Typing in this window is not sent.");
        await StartMediaAsync(endpoints);
    }

    private void Stop()
    {
        var link = _link;
        var node = _node;
        var slides = _slides;
        var media = _media;
        _link = null;
        _node = null;
        _slides = null;
        _media = null;
        link?.Dispose();
        slides?.Dispose();
        if (node != null)
            _ = node.DisposeAsync();
        if (media != null)
            _ = media.DisposeAsync();
        if (!IsRunning)
            return;
        IsRunning = false;
        IsLinked = false;
        LinkDetail = "Stopped";
        AddLog("Link stopped.");
    }

    private void Nudge(ShowAction action)
    {
        if (_link == null)
        {
            FormError = "Start the link first.";
            return;
        }

        _link.Nudge(action);
    }

    private async Task StartMediaAsync(LinkEndpoints endpoints)
    {
        if (_node == null)
            return;
        if (endpoints.ListenPort >= 65535)
        {
            AddLog("Incoming port 65535 leaves no room for Resolume content. Use 24710. Slide sync still works.");
            return;
        }

        var directory = Path.Combine(_settingsDir, "media");
        var media = new MediaServer(directory, endpoints.Channel, async (header, path, token) =>
        {
            var port = CurrentResolumePort();
            var loaded = await _resolume.OpenAsync(path, header.Layer, header.Clip, header.Play, port, token).ConfigureAwait(false);
            var detail = loaded + " Saved at " + path + ".";
            Ui(() => AddLog(detail));
            return detail;
        });
        try
        {
            await media.StartAsync(endpoints.BindAddress, endpoints.ListenPort + 1);
        }
        catch (Exception ex)
        {
            await media.DisposeAsync();
            AddLog("Resolume content port did not open. Slide sync still works. " + ex.Message);
            return;
        }

        if (_node == null)
        {
            await media.DisposeAsync();
            return;
        }

        _media = media;
        AddLog("Resolume content listens on port " + media.BoundPort + ". Received clips are saved in " + directory + ".");
    }

    private async Task PickContentAsync()
    {
        if (PickFile == null)
            return;
        var path = await PickFile();
        if (path == null)
            return;
        if (path.Length == 0)
        {
            FormError = "Choose a file that is saved on this laptop.";
            return;
        }

        ContentPath = path;
        FormError = "";
    }

    private async Task SendContentAsync()
    {
        if (!IsRunning || _media == null)
        {
            FormError = _media == null && IsRunning
                ? "The content port is not open. Stop the link and start it again."
                : "Start the link on this laptop and on every backup first.";
            return;
        }

        if (!File.Exists(ContentPath))
        {
            FormError = "Choose a video or picture that is saved on this laptop.";
            return;
        }

        if (!int.TryParse(LayerText, out var layer) || layer < 1)
        {
            FormError = "Layer starts at 1, matching the number in Resolume.";
            return;
        }

        if (!int.TryParse(ClipText, out var clip) || clip < 1)
        {
            FormError = "Clip starts at 1, matching the number in Resolume.";
            return;
        }

        var resolumePort = CurrentResolumePort();
        if (resolumePort is < 1 or > 65535)
        {
            FormError = "Resolume port must be from 1 to 65535.";
            return;
        }

        if (!int.TryParse(RemotePortText, out var remotePort) || remotePort is < 1 or > 65534)
            remotePort = 24710;
        var targets = MediaNames.Targets(RemoteAddress, remotePort, ExtraLaptops);
        if (targets.Count == 0 && !LoadOnThisLaptop)
        {
            FormError = "Add the backup laptops under outgoing IP, or list them on the Resolume tab.";
            return;
        }

        IsSending = true;
        FormError = "";
        ContentProgress = 0;
        var failures = new List<string>();
        try
        {
            AddLog("Sending " + Path.GetFileName(ContentPath) + " to layer " + layer + ", clip " + clip + ".");
            var index = 0;
            foreach (var target in targets)
            {
                index++;
                ContentStatus = "Sending to " + target.Host + " (" + index + " of " + targets.Count + ")...";
                ContentProgress = 0;
                var progress = new Progress<double>(value => ContentProgress = value);
                try
                {
                    var receipt = await MediaClient.SendFileAsync(
                        target.Host,
                        target.Port,
                        Channel,
                        ContentPath,
                        layer,
                        clip,
                        PlayAfterLoad,
                        progress);
                    AddLog(target.Host + ": " + receipt.Detail);
                    if (!receipt.Ok)
                        failures.Add(target.Host + ": " + receipt.Detail);
                }
                catch (Exception ex)
                {
                    var message = target.Host + " did not receive the file. Start the link on that laptop first.";
                    AddLog(message + " " + ex.Message);
                    failures.Add(message);
                }
            }

            if (LoadOnThisLaptop)
            {
                ContentStatus = "Loading on this laptop...";
                var local = await _resolume.OpenAsync(ContentPath, layer, clip, PlayAfterLoad, resolumePort, CancellationToken.None);
                AddLog(local);
                if (!local.StartsWith("Loaded", StringComparison.Ordinal))
                    failures.Add(local);
            }

            ContentProgress = failures.Count == 0 ? 1 : 0;
            ContentStatus = failures.Count == 0
                ? targets.Count == 0
                    ? "Loaded on this laptop."
                    : "Sent to " + targets.Count + (targets.Count == 1 ? " laptop." : " laptops.")
                : "Finished with a problem. The activity log has the detail.";
            FormError = failures.Count == 0 ? "" : failures[0];
        }
        finally
        {
            IsSending = false;
        }
    }

    private int CurrentResolumePort() =>
        int.TryParse(ResolumePortText, out var port) && port is >= 1 and <= 65535 ? port : 8080;

    private async Task CopyIpAsync()
    {
        RefreshIps();
        var ip = LocalAddresses.IPv4().FirstOrDefault();
        if (ip == null)
        {
            FormError = "This laptop does not have a network address yet. Join the venue network and try again.";
            return;
        }

        FormError = "";
        if (CopyText != null)
            await CopyText(ip);
        AddLog("Copied " + ip + ". That is the outgoing IP on the other laptop.");
    }

    private void AllowFirewall()
    {
        if (!OperatingSystem.IsWindows())
        {
            FormError = "The firewall button is for the Windows laptops.";
            return;
        }

        if (!int.TryParse(ListenPortText, out var port))
        {
            FormError = "Enter the incoming port first.";
            return;
        }

        try
        {
            var ports = port < 65535 ? new[] { port, port + 1 } : new[] { port };
            FirewallRule.Allow(ports);
            FormError = "";
            AddLog(ports.Length == 1
                ? "Windows will ask permission to open incoming port " + port + "."
                : "Windows will ask permission to open incoming port " + port + " and content port " + (port + 1) + ".");
        }
        catch (Exception ex)
        {
            FormError = ex.Message;
        }
    }

    private void ApplyRole(string? role)
    {
        if (role is not ("Primary" or "Backup" or "TwoWay"))
            return;
        _loading = true;
        Role = role;
        switch (role)
        {
            case "Primary":
                SendKeyboard = true;
                SendAllKeys = false;
                SendMouseClicks = true;
                SendMouseMove = false;
                PublishSlides = true;
                FollowSlides = false;
                AcceptKeyboard = false;
                AcceptMouse = false;
                break;
            case "Backup":
                SendKeyboard = false;
                SendAllKeys = false;
                SendMouseClicks = false;
                SendMouseMove = false;
                PublishSlides = false;
                FollowSlides = true;
                AcceptKeyboard = true;
                AcceptMouse = true;
                break;
            default:
                SendKeyboard = true;
                SendAllKeys = false;
                SendMouseClicks = true;
                SendMouseMove = false;
                PublishSlides = true;
                FollowSlides = true;
                AcceptKeyboard = true;
                AcceptMouse = true;
                break;
        }

        _loading = false;
        SaveSettings();
        AddLog(role switch
        {
            "Primary" => "This laptop is the show.",
            "Backup" => "This laptop is the backup.",
            _ => "Either laptop can advance the show."
        });
    }

    private void ApplyLink(LinkSnapshot snapshot)
    {
        var linked = snapshot.State == LinkState.Linked;
        if (linked != IsLinked)
            IsLinked = linked;
        if (snapshot.Detail == LinkDetail)
            return;
        LinkDetail = snapshot.Detail;
        if (snapshot.State != LinkState.Stopped)
            AddLog(snapshot.Detail + ".");
    }

    private void UpdateTally()
    {
        TallyText = !IsRunning ? "OFF" : IsLinked ? "LINKED" : "WAITING";
    }

    private void UpdateRoleHelp()
    {
        RoleHelp = Role switch
        {
            "Backup" => "Leave outgoing IP blank. The show laptop connects here. If that laptop dies, keep presenting on this one — it is already on the same slide.",
            "TwoWay" => "Put each laptop's address in the other laptop's outgoing IP. Either person can press the arrow keys.",
            "Custom" => "Custom mix. Primary should publish slides. Backup should follow slides.",
            _ => "Outgoing IP is the backup laptop. Incoming can stay 0.0.0.0. Arrow keys on this laptop move the backup too."
        };
    }

    private void RefreshIps()
    {
        var ips = LocalAddresses.IPv4();
        ThisLaptopIp = ips.Count == 0 ? "No address yet" : string.Join("    ", ips);
    }

    private void RefreshCommands()
    {
        _startCommand.RaiseCanExecuteChanged();
        _stopCommand.RaiseCanExecuteChanged();
        _previousCommand.RaiseCanExecuteChanged();
        _nextCommand.RaiseCanExecuteChanged();
        _blackCommand.RaiseCanExecuteChanged();
        _sendContentCommand.RaiseCanExecuteChanged();
        SetRoleCommand.RaiseCanExecuteChanged();
    }

    private void Load()
    {
        _loading = true;
        var settings = SettingsStore.Load(_settingsDir);
        BindAddress = settings.BindAddress;
        ListenPortText = settings.ListenPort.ToString();
        RemoteAddress = settings.RemoteAddress;
        RemotePortText = settings.RemotePort.ToString();
        Channel = settings.Channel;
        SendKeyboard = settings.SendKeyboard;
        SendAllKeys = settings.SendAllKeys;
        SendMouseClicks = settings.SendMouseClicks;
        SendMouseMove = settings.SendMouseMove;
        PublishSlides = settings.PublishSlides;
        FollowSlides = settings.FollowSlides;
        AcceptKeyboard = settings.AcceptKeyboard;
        AcceptMouse = settings.AcceptMouse;
        ResolumePortText = settings.ResolumePort is < 1 or > 65535 ? "8080" : settings.ResolumePort.ToString();
        LayerText = settings.Layer < 1 ? "1" : settings.Layer.ToString();
        ClipText = settings.Clip < 1 ? "1" : settings.Clip.ToString();
        PlayAfterLoad = settings.PlayAfterLoad;
        LoadOnThisLaptop = settings.LoadOnThisLaptop;
        ExtraLaptops = settings.ExtraLaptops ?? "";
        ContentPath = settings.ContentPath ?? "";
        Role = string.IsNullOrWhiteSpace(settings.Role) ? "Primary" : settings.Role;
        _loading = false;
    }

    private void SaveSettings()
    {
        if (_loading)
            return;
        int.TryParse(ListenPortText, out var listen);
        int.TryParse(RemotePortText, out var remote);
        int.TryParse(ResolumePortText, out var resolumePort);
        int.TryParse(LayerText, out var layer);
        int.TryParse(ClipText, out var clip);
        SettingsStore.Save(_settingsDir, new AppSettings
        {
            BindAddress = BindAddress,
            ListenPort = listen == 0 ? 24710 : listen,
            RemoteAddress = RemoteAddress,
            RemotePort = remote == 0 ? 24710 : remote,
            Channel = Channel,
            Role = Role,
            SendKeyboard = SendKeyboard,
            SendAllKeys = SendAllKeys,
            SendMouseClicks = SendMouseClicks,
            SendMouseMove = SendMouseMove,
            PublishSlides = PublishSlides,
            FollowSlides = FollowSlides,
            AcceptKeyboard = AcceptKeyboard,
            AcceptMouse = AcceptMouse,
            ResolumePort = resolumePort is < 1 or > 65535 ? 8080 : resolumePort,
            Layer = layer < 1 ? 1 : layer,
            Clip = clip < 1 ? 1 : clip,
            PlayAfterLoad = PlayAfterLoad,
            LoadOnThisLaptop = LoadOnThisLaptop,
            ExtraLaptops = ExtraLaptops,
            ContentPath = ContentPath
        });
    }

    private void AddLog(string line)
    {
        LogLines.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
        while (LogLines.Count > 200)
            LogLines.RemoveAt(0);
    }

    private void Ui(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    private void SetOption(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, save: false, name))
            return;
        if (!_loading && Role is "Primary" or "Backup" or "TwoWay")
            Role = "Custom";
        Touch();
    }

    private void Touch()
    {
        if (!_loading)
            SaveSettings();
    }

    private bool Set<T>(ref T field, T value, bool save, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        if (save)
            Touch();
        return true;
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
