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
    private bool _loading;
    private bool _disposed;
    private SyncNode? _node;
    private ShowLink? _link;
    private IDisposable? _slides;

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
    }

    private void Stop()
    {
        var link = _link;
        var node = _node;
        var slides = _slides;
        _link = null;
        _node = null;
        _slides = null;
        link?.Dispose();
        slides?.Dispose();
        if (node != null)
            _ = node.DisposeAsync();
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
            FirewallRule.Allow(port);
            FormError = "";
            AddLog("Windows will ask permission to open incoming port " + port + ".");
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
        Role = string.IsNullOrWhiteSpace(settings.Role) ? "Primary" : settings.Role;
        _loading = false;
    }

    private void SaveSettings()
    {
        if (_loading)
            return;
        int.TryParse(ListenPortText, out var listen);
        int.TryParse(RemotePortText, out var remote);
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
            AcceptMouse = AcceptMouse
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
