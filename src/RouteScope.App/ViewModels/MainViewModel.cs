using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using RouteScope.App.Converters;
using RouteScope.App.Mvvm;
using RouteScope.Core;
using RouteScope.Core.Engine;
using RouteScope.Core.Models;
using RouteScope.Core.Nodes;
using RouteScope.Core.Runtime;
using RouteScope.Core.Storage;
using RouteScope.Core.SystemTools;
using RouteScope.Core.Updates;

namespace RouteScope.App.ViewModels;

public enum Page
{
    Home,
    Targets,
    Servers,
    Live,
    Logs,
    Settings,
    Support,
}

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly StateStore _store;
    private readonly RouteEngine _engine = new();
    private readonly EngineInstaller _installer = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statsTimer;
    private readonly Queue<double> _upHistory = new();
    private readonly Queue<double> _downHistory = new();

    private Page _page = Page.Home;
    private bool _isBusy;
    private string _busyMessage = "";
    private RunState _runState = RunState.Stopped;
    private string? _lastError;
    private long _totalUp;
    private long _totalDown;
    private double _upSpeed;
    private double _downSpeed;
    private double[] _upSeries = Array.Empty<double>();
    private double[] _downSeries = Array.Empty<double>();
    private string? _toast;
    private DispatcherTimer? _toastTimer;

    /// <summary>Feeds traffic totals when the engine has no Clash API (Xray).</summary>
    private DispatcherTimer? _fallbackTimer;
    private long _fallbackUp;
    private long _fallbackDown;
    private bool _isDownloading;
    private double _downloadFraction;
    private string _downloadText = "";
    private bool _engineReady;
    private string _engineVersion = "not installed";
    private int _connectionCount;
    private bool _advancedExpanded;
    private string _logFilter = "";
    private string _newLinkText = "";
    private bool _isOnboardingVisible;
    private int _onboardingStep;

    public MainViewModel(StateStore store)
    {
        _store = store;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Targets = new ObservableCollection<ProxyTarget>(_store.State.Targets);
        Nodes = new ObservableCollection<ProxyNode>(_store.State.Nodes);
        Externals = new ObservableCollection<ExternalProxy>(_store.State.Externals);
        Connections = new ObservableCollection<ConnectionInfo>();
        Logs = new ObservableCollection<LogLine>();

        _engine.StateChanged += OnEngineStateChanged;
        LogBus.LineWritten += OnLogLine;

        foreach (var line in LogBus.Snapshot()) Logs.Add(line);
        RebuildVisibleLogs();

        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), _dispatcher);
        _statsTimer.Start();

        foreach (var sub in _store.State.Subscriptions) Subscriptions.Add(sub);

        LoadEngines();
        RefreshSelectionFlags();
        ResolveChainHops();

        _ = Task.Run(async () =>
        {
            await Task.Delay(1200);
            if (_store.State.Route == RouteKind.Node && _store.State.ActiveNode is { } activeNode)
                await TestLatencyAsync(activeNode);
            else if (_store.State.Route == RouteKind.External && _store.State.ActiveExternal is { } activeExt)
                await TestLatencyAsync(activeExt);
        });
        _isOnboardingVisible = !_store.State.Settings.OnboardingCompleted && Targets.Count == 0;

        ToggleConnectCommand = new AsyncRelayCommand(ToggleConnectAsync, () => !IsBusy);
        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync);
        AddApplicationCommand = new AsyncRelayCommand(AddApplicationAsync);
        AddProcessNameCommand = new RelayCommand(p => AddProcessName(p as string));
        RemoveTargetCommand = new RelayCommand(p => RemoveTarget(p as ProxyTarget));
        LaunchTargetCommand = new RelayCommand(p => LaunchTarget(p as ProxyTarget));
        BrowseToTargetCommand = new RelayCommand(p => BrowseToTarget(p as ProxyTarget));
        NavigateCommand = new RelayCommand(p => Navigate(p));
        AddNodesFromTextCommand = new AsyncRelayCommand(AddNodesFromTextAsync);
        RemoveNodeCommand = new RelayCommand(p => RemoveNode(p as ProxyNode));
        SelectNodeCommand = new RelayCommand(p => SelectNode(p as ProxyNode));
        SelectExternalCommand = new RelayCommand(p => SelectExternal(p as ExternalProxy));
        UseDirectCommand = new RelayCommand(UseDirect);
        DetectProxiesCommand = new AsyncRelayCommand(DetectProxiesAsync);
        DownloadEngineCommand = new AsyncRelayCommand(DownloadEngineAsync);
        CancelDownloadCommand = new RelayCommand(_ => CancelDownload());
        LocateEngineCommand = new AsyncRelayCommand(p => LocateEngineAsync(p as EngineRow));
        SetModeCommand = new RelayCommand(p => SetMode(p));
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        ClearLogsCommand = new RelayCommand(ClearLogs);
        FinishOnboardingCommand = new RelayCommand(FinishOnboarding);
        NextOnboardingCommand = new RelayCommand(_ => OnboardingStep++);
        OpenLogFolderCommand = new RelayCommand(() => LogBus.OpenLogFolder());
        CopyTextCommand = new RelayCommand(CopyText);
        OpenProjectPageCommand = new RelayCommand(() => OpenUrl(ProjectUrl));
        OpenIssuesCommand = new RelayCommand(() => OpenUrl(ProjectUrl + "/issues"));
        OpenLicenceCommand = new RelayCommand(() => OpenUrl(ProjectUrl + "#license"));
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.DataDir));
        OpenConfigCommand = new RelayCommand(OpenRuntimeConfig);
        AddExternalCommand = new RelayCommand(_ => AddExternal());
        RemoveExternalCommand = new RelayCommand(p => RemoveExternal(p as ExternalProxy));
        CloseConnectionsCommand = new AsyncRelayCommand(CloseAllConnectionsAsync);
        TestLatencyCommand = new AsyncRelayCommand(TestLatencyAsync);
        TestChainHopPingCommand = new AsyncRelayCommand(p => TestChainHopPingAsync(p as ChainHop));
        CopyNodeLinkCommand = new RelayCommand(p => CopyNodeLink(p as ProxyNode));
        AddExternalPresetCommand = new RelayCommand(AddExternalPreset);
        AddPsiphonCommand = new RelayCommand(_ => AddPsiphon());
        AddWireGuardCommand = new RelayCommand(_ => AddWireGuard());
        AddOpenVpnCommand = new RelayCommand(_ => AddOpenVpn());
        LocateOpenVpnCommand = new RelayCommand(_ => LocateOpenVpn());
        OpenVpnDownloadCommand = new RelayCommand(_ => OpenUrl("https://openvpn.net/community-downloads/"));
        BrowsePsiphonCommand = new RelayCommand(_ => BrowsePsiphon());
        AddChainHopCommand = new RelayCommand(AddChainHop);
        RemoveChainHopCommand = new RelayCommand(p => RemoveChainHop(p as ChainHop));
        SelectServerCommand = new RelayCommand(SelectServer);
        AddServerToChainCommand = new RelayCommand(AddServerToChain);
        EditServerCommand = new RelayCommand(EditServer);
        RemoveServerCommand = new RelayCommand(RemoveServer);
        MoveChainHopUpCommand = new RelayCommand(p => MoveChainHop(p as ChainHop, -1));
        MoveChainHopDownCommand = new RelayCommand(p => MoveChainHop(p as ChainHop, 1));
        EditExternalCommand = new RelayCommand(p => EditExternal(p as ExternalProxy));
        EditNodeCommand = new RelayCommand(p => EditNode(p as ProxyNode));
        TestRealPingCommand = new AsyncRelayCommand(TestRealPingAsync);
        AddSubscriptionCommand = new RelayCommand(_ => AddSubscription());
        RemoveSubscriptionCommand = new RelayCommand(p => RemoveSubscription(p as Subscription));
        UpdateSubscriptionCommand = new AsyncRelayCommand(p => UpdateSubscriptionAsync(p as Subscription, false));
        UpdateSubscriptionViaProxyCommand = new AsyncRelayCommand(p => UpdateSubscriptionAsync(p as Subscription, true));
        InstallEngineCommand = new AsyncRelayCommand(p => InstallEngineAsync(p as EngineRow));
        CheckEngineUpdatesCommand = new AsyncRelayCommand(CheckEngineUpdatesAsync);
        LaunchAllCommand = new RelayCommand(_ => LaunchAllTargets());
    }

    // ------------------------------------------------------------ collections

    public ObservableCollection<ProxyTarget> Targets { get; }
    public ObservableCollection<ProxyNode> Nodes { get; }
    public ObservableCollection<ExternalProxy> Externals { get; }

    /// <summary>Local proxies the user runs themselves.</summary>
    public ObservableCollection<ExternalProxy> LocalProxies { get; } = new();

    /// <summary>
    /// Whole configs imported from a subscription. They are servers, not local proxies — they
    /// only live in <see cref="Externals"/> because RouteScope starts them the same way.
    /// </summary>
    public ObservableCollection<ExternalProxy> ConfigProxies { get; } = new();

    public bool HasConfigProxies => ConfigProxies.Count > 0;

    /// <summary>Servers you added and configs from a subscription, shown as one list.</summary>
    public ObservableCollection<ServerEntry> Servers { get; } = new();

    private void RefreshExternalViews()
    {
        LocalProxies.Clear();
        ConfigProxies.Clear();

        foreach (var ext in Externals)
        {
            if (string.Equals(ext.Type, "Xray config", StringComparison.OrdinalIgnoreCase) || ext.IsWireGuard || ext.IsOpenVpn)
                ConfigProxies.Add(ext);      // tunnels and imported configs are servers, not local proxies
            else
                LocalProxies.Add(ext);
        }

        Servers.Clear();
        foreach (var node in Nodes) Servers.Add(new ServerEntry { Node = node });
        foreach (var config in ConfigProxies) Servers.Add(new ServerEntry { Config = config });

        RaiseAll(nameof(HasConfigProxies));
    }
    public ObservableCollection<ConnectionInfo> Connections { get; }
    public ObservableCollection<LogLine> Logs { get; }

    /// <summary>Downloadable cores shown in Settings → Cores &amp; updates.</summary>
    public ObservableCollection<EngineRow> Engines { get; } = new();

    /// <summary>Log lines currently shown (filtered, newest first).</summary>
    public ObservableCollection<LogLine> VisibleLogs { get; } = new();

    public AppState State => _store.State;
    public Settings Settings => _store.State.Settings;

    // ------------------------------------------------------------- navigation

    public Page CurrentPage
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            RaiseAll(nameof(IsHome), nameof(IsTargets), nameof(IsServers), nameof(IsLive), nameof(IsLogs), nameof(IsSettings), nameof(IsSupport),
                nameof(PageTitle), nameof(PageSubtitle));
        }
    }

    public bool IsHome => CurrentPage == Page.Home;
    public bool IsTargets => CurrentPage == Page.Targets;
    public bool IsServers => CurrentPage == Page.Servers;
    public bool IsLive => CurrentPage == Page.Live;
    public bool IsLogs => CurrentPage == Page.Logs;
    public bool IsSettings => CurrentPage == Page.Settings;

    public bool IsSupport => CurrentPage == Page.Support;

    public string PageTitle => CurrentPage switch
    {
        Page.Home => "Overview",
        Page.Targets => "What to route",
        Page.Servers => "Where to route it",
        Page.Live => "Live connections",
        Page.Logs => "Activity log",
        Page.Settings => "Settings",
        Page.Support => "Support RouteScope",
        _ => "RouteScope",
    };

    public string PageSubtitle => CurrentPage switch
    {
        Page.Home => "Only the apps you choose use the proxy — everything else stays direct.",
        Page.Targets => "Add a folder, an application, or a process name.",
        Page.Servers => "Use a local client such as v2rayN, or add your own servers.",
        Page.Live => "Every connection RouteScope is handling right now.",
        Page.Logs => "Engine output and RouteScope events.",
        Page.Settings => "Ports, DNS, updates and the advanced escape hatches.",
        Page.Support => "Free and open source. If it earns it, you can buy it a coffee.",
        _ => "",
    };

    // ----------------------------------------------------------------- status

    public RunState RunState
    {
        get => _runState;
        private set
        {
            if (!Set(ref _runState, value)) return;
            RaiseAll(nameof(IsRunning), nameof(IsStarting), nameof(IsStopped), nameof(IsFaulted),
                nameof(StatusTitle), nameof(StatusSubtitle), nameof(StatusGlyph), nameof(ConnectLabel),
                nameof(StatusAccentKey));
        }
    }

    public bool IsRunning => RunState == RunState.Running;
    public bool IsStarting => RunState is RunState.Starting or RunState.Stopping;
    public bool IsStopped => RunState is RunState.Stopped;
    public bool IsFaulted => RunState == RunState.Faulted;

    public string StatusTitle => RunState switch
    {
        RunState.Running => EnabledTargetsCount switch
        {
            0 => "Routing everything",
            1 => "Routing 1 app",
            var n => $"Routing {n} apps",
        },
        RunState.Starting => "Starting…",
        RunState.Stopping => "Stopping…",
        RunState.Faulted => "Something went wrong",
        _ => "Not routing",
    };

    public string StatusSubtitle => RunState switch
    {
        RunState.Running => State.Mode switch
        {
            CaptureMode.AppProxy => "Only apps launched from RouteScope are using the proxy",
            CaptureMode.Process => "Only your selected apps are intercepted — everything else is untouched",
            CaptureMode.Transparent => "Whole computer · every app on this PC is captured",
            CaptureMode.SystemProxy => "Windows system proxy is pointing at RouteScope",
            _ => "",
        },
        RunState.Faulted => _lastError ?? "Check the activity log for details.",
        RunState.Starting => "The engine is warming up.",
        _ => EnabledTargetsCount == 0
            ? "Add a folder or an app to get started."
            : "Press start to route your selected apps.",
    };

    public string StatusGlyph => RunState switch
    {
        RunState.Running => "\uE769",
        RunState.Starting or RunState.Stopping => "\uE895",
        RunState.Faulted => "\uEA39",
        _ => "\uE7E8",
    };

    public string StatusAccentKey => RunState switch
    {
        RunState.Faulted => "B.Danger",
        _ => "B.Accent",
    };

    public string ConnectLabel => IsRunning ? "Stop" : IsStarting ? "Working…" : "Start routing";

    public int EnabledTargetsCount => Targets.Count(t => t.Enabled);
    public bool HasTargets => Targets.Count > 0;
    public bool HasNoTargets => Targets.Count == 0;
    public int TargetCount => Targets.Count;
    public int NodeCount => Nodes.Count;

    public string RouteSummary => State.RoutePath;

    public bool IsDirectRoute => State.Route == RouteKind.Direct;

    /// <summary>Extra proxies stacked on top of the base route.</summary>
    public ObservableCollection<ChainHop> Chain { get; } = new();

    public bool HasChain => Chain.Count > 0;
    public string ChainSummary => Chain.Count == 0
        ? "Traffic goes straight out through the route above."
        : $"{Chain.Count} extra hop{(Chain.Count == 1 ? "" : "s")} — traffic exits from the last one.";

    public string BaseRouteLabel => State.RouteSummary;

    public string BaseRouteBadge => State.Route switch
    {
        RouteKind.External => "local proxy",
        RouteKind.Node => "server",
        _ => "direct",
    };

    /// <summary>Fills in the display fields of each hop from the servers it points at.</summary>
    private void ResolveChainHops()
    {
        // Drop hops whose proxy or server no longer exists.
        State.Chain.RemoveAll(h => h.Kind == RouteKind.External
            ? Externals.All(e => e.Id != h.RefId)
            : Nodes.All(n => n.Id != h.RefId));

        Chain.Clear();
        foreach (var hop in State.Chain)
        {
            if (hop.Kind == RouteKind.External)
            {
                var ext = Externals.FirstOrDefault(e => e.Id == hop.RefId);
                if (ext is null) continue;
                hop.DisplayName = ext.Name;
                hop.Endpoint = ext.Endpoint;
                hop.Badge = ext.IsManaged ? "managed" : string.IsNullOrWhiteSpace(ext.DetectedType) ? "local" : ext.DetectedType!;
                hop.IsManaged = ext.IsManaged;
            }
            else
            {
                var node = Nodes.FirstOrDefault(n => n.Id == hop.RefId);
                if (node is null) continue;
                hop.DisplayName = node.Name;
                hop.Endpoint = node.Endpoint;
                hop.Badge = node.ProtocolLabel;
                hop.IsManaged = false;
            }
            Chain.Add(hop);
        }

        RaiseAll(nameof(HasChain), nameof(ChainSummary), nameof(RouteSummary),
            nameof(BaseRouteLabel), nameof(BaseRouteBadge));
    }

    private void AddChainHop(object? source)
    {
        ChainHop? hop = source switch
        {
            ExternalProxy ext => new ChainHop { Kind = RouteKind.External, RefId = ext.Id },
            ProxyNode node => new ChainHop { Kind = RouteKind.Node, RefId = node.Id },
            _ => null,
        };

        if (hop is null) return;
        State.Chain.Add(hop);
        _store.Save();
        ResolveChainHops();
        ShowToast("Added to the chain");
    }

    private void RemoveChainHop(ChainHop? hop)
    {
        if (hop is null) return;
        State.Chain.Remove(hop);
        _store.Save();
        ResolveChainHops();
    }

    private void MoveChainHop(ChainHop? hop, int delta)
    {
        if (hop is null) return;
        var index = State.Chain.IndexOf(hop);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= State.Chain.Count) return;

        State.Chain.RemoveAt(index);
        State.Chain.Insert(target, hop);
        _store.Save();
        ResolveChainHops();
    }

    private void EditExternal(ExternalProxy? proxy)
    {
        if (proxy is null) return;

        var dialog = new Dialogs.ProxyEditorDialog(proxy, isNew: false)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true) return;

        _store.Save();
        ResolveChainHops();
        RaiseAll(nameof(RouteSummary));
        ShowToast($"{proxy.Name} saved");
    }

    // A server row can be either one of the user's servers or an imported config.
    private void SelectServer(object? parameter)
    {
        switch (parameter)
        {
            case ServerEntry { Node: { } node }: SelectNode(node); break;
            case ServerEntry { Config: { } config }: SelectExternal(config); break;
        }
    }

    private void AddServerToChain(object? parameter)
    {
        switch (parameter)
        {
            case ServerEntry { Node: { } node }: AddChainHop(node); break;
            case ServerEntry { Config: { } config }: AddChainHop(config); break;
        }
    }

    private void EditServer(object? parameter)
    {
        switch (parameter)
        {
            case ServerEntry { Node: { } node }: EditNode(node); break;
            case ServerEntry { Config: { } config }: EditConfig(config); break;
        }
    }

    private void RemoveServer(object? parameter)
    {
        switch (parameter)
        {
            case ServerEntry { Node: { } node }: RemoveNode(node); break;
            case ServerEntry { Config: { } config }: RemoveExternal(config); break;
        }
    }

    /// <summary>Imports a WireGuard .conf as a route. The engine dials it; no process, no admin.</summary>
    private void AddWireGuard()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import a WireGuard config",
            Filter = "WireGuard config (*.conf)|*.conf|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true) return;
        ImportWireGuardFile(dialog.FileName);
    }

    /// <summary>Shared by the button and by drag-and-drop.</summary>
    public void ImportWireGuardFile(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var parsed = Core.Nodes.WireGuardConfig.Parse(text);

            if (!parsed.IsUsable)
            {
                ShowToast("That file is missing a key, an address or an endpoint.");
                return;
            }

            var entry = new ExternalProxy
            {
                Name = Path.GetFileNameWithoutExtension(path),
                Type = "WireGuard",
                Host = parsed.EndpointHost,
                Port = parsed.EndpointPort,
                ImportedConfigText = text,
            };

            Externals.Add(entry);
            State.Externals.Add(entry);
            _store.Save();
            RefreshSelectionFlags();
            ResolveChainHops();
            ShowToast($"{entry.Name} imported ({parsed.Endpoint})");
        }
        catch (Exception ex)
        {
            ShowToast("Could not read that file: " + ex.Message);
        }
    }

    /// <summary>Imports an .ovpn as a route. Needs OpenVPN installed and administrator rights.</summary>
    private void AddOpenVpn()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import an OpenVPN config",
            Filter = "OpenVPN config (*.ovpn)|*.ovpn|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true) return;
        ImportOpenVpnFile(dialog.FileName);
    }

    public void ImportOpenVpnFile(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var parsed = Core.Nodes.OpenVpnConfig.Parse(text);

            if (!parsed.IsUsable)
            {
                ShowToast("That .ovpn file has no remote server in it.");
                return;
            }

            string? openVpnUser = null, openVpnPass = null;
            if (parsed.NeedsCredentials)
            {
                var login = new Dialogs.OpenVpnCredentialsDialog(Path.GetFileNameWithoutExtension(path))
                {
                    Owner = Application.Current.MainWindow,
                };

                if (login.ShowDialog() != true) return;   // user cancelled the import
                openVpnUser = login.Username;
                openVpnPass = login.Password;
            }

            var entry = new ExternalProxy
            {
                Name = Path.GetFileNameWithoutExtension(path),
                Type = "OpenVPN",
                Host = parsed.RemoteHost,
                Port = parsed.RemotePort,
                ImportedConfigText = text,
                Username = openVpnUser,
                Password = openVpnPass,
            };

            Externals.Add(entry);
            State.Externals.Add(entry);
            _store.Save();
            RefreshSelectionFlags();
            ResolveChainHops();
            ShowToast($"{entry.Name} imported ({parsed.Endpoint})");
        }
        catch (Exception ex)
        {
            ShowToast("Could not read that file: " + ex.Message);
        }
    }

    private void EditConfig(ExternalProxy config)
    {
        // OpenVPN configs that need a login get their own small dialog first.
        if (config.IsOpenVpn &&
            Core.Nodes.OpenVpnConfig.Parse(config.ImportedConfigText ?? "").NeedsCredentials)
        {
            var login = new Dialogs.OpenVpnCredentialsDialog(config.Name, config.Username, config.Password)
            {
                Owner = Application.Current.MainWindow,
            };

            if (login.ShowDialog() == true)
            {
                config.Username = login.Username;
                config.Password = login.Password;
                _store.Save();
                ShowToast($"{config.Name} login saved");
            }
            // Cancelling the login still opens the config editor — never trap the user.
        }

        var dialog = new Dialogs.ConfigEditorDialog(config)
        {
            Owner = Application.Current.MainWindow,
        };

        if (dialog.ShowDialog() != true) return;

        _store.Save();
        RefreshSelectionFlags();
        ResolveChainHops();
        ShowToast($"{config.Name} saved");
    }

    private void EditNode(ProxyNode? node)
    {
        if (node is null) return;

        var dialog = new Dialogs.NodeEditorDialog(node) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        _store.Save();
        ResolveChainHops();
        RaiseAll(nameof(RouteSummary));
        ShowToast($"{node.Name} saved");
    }

    // ------------------------------------------------------------------ real ping

    private async Task TestRealPingAsync(object? target)
    {
        // A row in the servers list can wrap either kind of entry.
        if (target is ServerEntry entry) target = (object?)entry.Node ?? entry.Config;

        switch (target)
        {
            case ExternalProxy { IsManaged: true } managed:
                ShowToast($"Start routing to measure {managed.Name} — the tunnel only exists then.");
                break;

            case ExternalProxy ext:
            {
                ext.RealPingMs = null;
                var type = ext.DetectedType ?? ext.Type;
                var ms = await RealPing.ThroughLocalProxyAsync(ext.Host, ext.Port, type, 8000, CancellationToken.None);
                ext.RealPingMs = ms;
                ShowToast(ms > 0 ? $"{ext.Name}: {ms} ms through the proxy" : $"{ext.Name}: no route through this proxy");
                break;
            }

            case ProxyNode node:
            {
                node.RealPingMs = null;
                BusyMessage = $"Opening a tunnel through {node.Name}…";
                IsBusy = true;
                try
                {
                    var ms = await RealPing.ThroughNodeAsync(node, 9000, CancellationToken.None);
                    node.RealPingMs = ms;
                    ShowToast(ms > 0 ? $"{node.Name}: {ms} ms through the server" : $"{node.Name}: could not reach the internet");
                }
                finally { IsBusy = false; }
                break;
            }
        }
    }

    // ------------------------------------------------------------ subscriptions

    public ObservableCollection<Subscription> Subscriptions { get; } = new();

    public bool HasSubscriptions => Subscriptions.Count > 0;

    private string _newSubscriptionUrl = "";
    public string NewSubscriptionUrl
    {
        get => _newSubscriptionUrl;
        set => Set(ref _newSubscriptionUrl, value);
    }

    private void AddSubscription()
    {
        var url = NewSubscriptionUrl.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowToast("Paste a subscription URL first.");
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ShowToast("That does not look like a URL.");
            return;
        }

        var sub = new Subscription
        {
            Name = uri.Host.Replace("www.", ""),
            Url = url,
        };

        Subscriptions.Add(sub);
        State.Subscriptions.Add(sub);
        NewSubscriptionUrl = "";
        _store.Save();
        RaiseAll(nameof(HasSubscriptions));

        // Deliberately not fetched: adding a link and refreshing it are separate actions.
        ShowToast("Subscription saved — press Update when you want its servers.");
    }

    private void RemoveSubscription(Subscription? sub)
    {
        if (sub is null) return;

        Subscriptions.Remove(sub);
        State.Subscriptions.Remove(sub);

        foreach (var node in Nodes.Where(n => n.SubscriptionId == sub.Id).ToList())
        {
            Nodes.Remove(node);
            State.Nodes.Remove(node);
        }

        State.Chain.RemoveAll(h => h.Kind == RouteKind.Node && Nodes.All(n => n.Id != h.RefId));
        if (State.ActiveNodeId is not null && Nodes.All(n => n.Id != State.ActiveNodeId))
        {
            State.ActiveNodeId = Nodes.FirstOrDefault()?.Id;
            if (Nodes.Count == 0 && State.Route == RouteKind.Node) State.Route = RouteKind.Direct;
        }

        _store.Save();
        RaiseAll(nameof(HasSubscriptions), nameof(NodeCount));
        RefreshSelectionFlags();
        ResolveChainHops();
        ShowToast($"{sub.Name} removed");
    }

    private async Task UpdateSubscriptionAsync(Subscription? sub, bool viaProxy)
    {
        if (sub is null) return;

        string? proxyUrl = null;

        if (viaProxy)
        {
            var ext = State.ActiveExternal;
            if (ext is null)
            {
                ShowToast("Pick a local proxy first — that is what the update goes through.");
                return;
            }

            var type = ext.DetectedType ?? ext.Type;
            proxyUrl = string.Equals(type, "HTTP", StringComparison.OrdinalIgnoreCase)
                ? $"http://{ext.Host}:{ext.Port}"
                : $"socks5://{ext.Host}:{ext.Port}";
        }

        BusyMessage = viaProxy ? $"Updating {sub.Name} through the proxy…" : $"Updating {sub.Name}…";
        IsBusy = true;
        try
        {
            var body = await SubscriptionFetcher.FetchRawAsync(sub.Url, proxyUrl, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(body))
            {
                sub.LastError = "The download was empty";
                ShowToast($"{sub.Name}: nothing came back. Check the URL.");
                return;
            }

            // Whole-config subscriptions are not server links: they are run as-is by RouteScope.
            var customConfigs = SubscriptionFetcher.TryParseCustomConfigs(body);

            if (customConfigs is not null)
            {
                foreach (var old in Externals.Where(e => e.SubscriptionId == sub.Id).ToList())
                {
                    Externals.Remove(old);
                    State.Externals.Remove(old);
                }

                foreach (var item in customConfigs)
                {
                    var entry = new ExternalProxy
                    {
                        Name = item.Name,
                        Host = "127.0.0.1",
                        Port = 0,
                        Type = "Xray config",
                        CustomConfigJson = item.Json,
                        SubscriptionId = sub.Id,
                    };

                    Externals.Add(entry);
                    State.Externals.Add(entry);
                }

                sub.NodeCount = customConfigs.Count;
                sub.LastUpdatedUtc = DateTime.UtcNow;
                sub.LastError = null;
                _store.Save();
                RefreshSelectionFlags();
                ResolveChainHops();
                ShowToast($"{sub.Name}: {customConfigs.Count} config(s) added as managed proxies");
                return;
            }

            var fetched = SubscriptionFetcher.Parse(body);
            if (fetched.Count == 0)
            {
                sub.LastError = "No servers in the response";
                ShowToast($"{sub.Name}: the format was not recognised.");
                return;
            }

            foreach (var old in Nodes.Where(n => n.SubscriptionId == sub.Id).ToList())
            {
                Nodes.Remove(old);
                State.Nodes.Remove(old);
            }

            foreach (var node in fetched)
            {
                node.SubscriptionId = sub.Id;
                Nodes.Add(node);
                State.Nodes.Add(node);
            }

            sub.NodeCount = fetched.Count;
            sub.LastUpdatedUtc = DateTime.UtcNow;
            sub.LastError = null;

            _store.Save();
            RaiseAll(nameof(NodeCount), nameof(RouteSummary));
            RefreshSelectionFlags();
            ResolveChainHops();
            ShowToast($"{sub.Name}: {fetched.Count} server{(fetched.Count == 1 ? "" : "s")}");
        }
        catch (Exception ex)
        {
            sub.LastError = ex.Message;
            ShowToast($"{sub.Name}: update failed");
        }
        finally { IsBusy = false; }
    }

    private void RefreshSelectionFlags()
    {
        foreach (var e in Externals)
            e.IsActive = State.Route == RouteKind.External && e.Id == State.ActiveExternalId;
        foreach (var n in Nodes)
            n.IsActive = State.Route == RouteKind.Node && n.Id == State.ActiveNodeId;

        RefreshExternalViews();
        RaiseAll(nameof(RouteSummary), nameof(IsDirectRoute), nameof(BaseRouteLabel), nameof(BaseRouteBadge));
    }

    public string ModeSummary => State.Mode switch
    {
        CaptureMode.AppProxy => "App proxy",
        CaptureMode.Process => "Process mode",
        CaptureMode.Transparent => "Whole computer (TUN)",
        CaptureMode.SystemProxy => "System proxy",
        _ => "",
    };

    public string ModeHint => State.Mode switch
    {
        CaptureMode.AppProxy =>
            "Only apps launched from RouteScope use the proxy. Nothing else on your PC is touched.",
        CaptureMode.Process =>
            "Only the apps and folders in your list are intercepted, even ones with no proxy support.",
        CaptureMode.Transparent =>
            "Every app on this PC is captured. Use it only when you want the whole machine routed.",
        CaptureMode.SystemProxy =>
            "Every app that follows the Windows proxy setting is routed.",
        _ => "",
    };

    /// <summary>False in whole-computer modes, where the target list has no effect.</summary>
    public bool TargetsInUse =>
        State.Mode == CaptureMode.AppProxy ||
        State.Mode == CaptureMode.Process ||
        (State.Mode == CaptureMode.Transparent && State.Settings.Advanced.TunOnlyTargets);

    /// <summary>Banner shown on the targets page when the current mode ignores the list.</summary>
    public string TargetsUnusedMessage => State.Mode switch
    {
        CaptureMode.Transparent =>
            "Whole-computer mode is on, so every app is routed and this list is not used. " +
            "Switch to App proxy or Process mode in Where to route if you only want these apps proxied.",
        CaptureMode.SystemProxy =>
            "System proxy mode is on, so every app that follows the Windows proxy is routed and this list is not used. " +
            "Switch to App proxy or Process mode in Where to route if you only want these apps proxied.",
        _ =>
            "This mode routes everything, so this list is not used. " +
            "Switch to App proxy or Process mode in Where to route.",
    };

    public bool IsTransparent => State.Mode == CaptureMode.Transparent;
    public bool IsAppProxy => State.Mode == CaptureMode.AppProxy;
    public bool IsSystemProxy => State.Mode == CaptureMode.SystemProxy;
    public bool IsProcessMode => State.Mode == CaptureMode.Process;

    public string MixedPortLabel => Settings.MixedPort.ToString();

    // ------------------------------------------------------------------- live

    public double UpSpeed
    {
        get => _upSpeed;
        private set
        {
            if (Set(ref _upSpeed, value)) RaiseAll(nameof(UpSpeedText));
        }
    }

    public double DownSpeed
    {
        get => _downSpeed;
        private set
        {
            if (Set(ref _downSpeed, value)) RaiseAll(nameof(DownSpeedText));
        }
    }

    public string UpSpeedText => BytesConverter.Format(UpSpeed) + "/s";
    public string DownSpeedText => BytesConverter.Format(DownSpeed) + "/s";

    public long TotalUp
    {
        get => _totalUp;
        private set
        {
            if (Set(ref _totalUp, value)) Raise(nameof(TotalUpText));
        }
    }

    public long TotalDown
    {
        get => _totalDown;
        private set
        {
            if (Set(ref _totalDown, value)) Raise(nameof(TotalDownText));
        }
    }

    public string TotalUpText => BytesConverter.Format(TotalUp);
    public string TotalDownText => BytesConverter.Format(TotalDown);

    public double[] UpSeries
    {
        get => _upSeries;
        private set => Set(ref _upSeries, value);
    }

    public double[] DownSeries
    {
        get => _downSeries;
        private set => Set(ref _downSeries, value);
    }

    public int ConnectionCount
    {
        get => _connectionCount;
        private set
        {
            if (Set(ref _connectionCount, value)) Raise(nameof(ConnectionCountLabel));
        }
    }

    public string ConnectionCountLabel => ConnectionCount == 1 ? "1 connection" : $"{ConnectionCount} connections";

    // ------------------------------------------------------------------ busy

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) Raise(nameof(BusyVisibility));
        }
    }

    public string BusyMessage
    {
        get => _busyMessage;
        private set => Set(ref _busyMessage, value);
    }

    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public bool IsDownloading
    {
        get => _isDownloading;
        private set => Set(ref _isDownloading, value);
    }

    public double DownloadFraction
    {
        get => _downloadFraction;
        private set => Set(ref _downloadFraction, value);
    }

    public string DownloadText
    {
        get => _downloadText;
        private set => Set(ref _downloadText, value);
    }

    public bool EngineReady
    {
        get => _engineReady;
        private set
        {
            if (Set(ref _engineReady, value)) RaiseAll(nameof(EngineMissing), nameof(EngineStatusText), nameof(EngineChoiceHint));
        }
    }

    public bool EngineMissing => !EngineReady;
    /// <summary>
    /// The engine that will actually route, not just whichever one is installed. Shows
    /// Xray-core when that is selected, so the label never disagrees with what runs.
    /// </summary>
    private (string Name, string Version, bool Ready) ResolveRoutingEngine()
    {
        var installer = new EngineInstaller();
        var singBox = installer.GetActive(EngineCatalog.SingBox);
        var xray = installer.GetActive(EngineCatalog.Get("xray")!);
        var preference = (State.Settings.PreferredEngine ?? "auto").ToLowerInvariant();

        if (preference == "xray" && xray is not null)
            return ("Xray-core", xray.Version, true);

        if (singBox is not null)
            return ("sing-box", singBox.Version, true);

        if (xray is not null)
            return ("Xray-core", xray.Version, true);

        return ("engine", "", false);
    }

    public string EngineStatusText
    {
        get
        {
            var (name, version, ready) = ResolveRoutingEngine();
            return ready ? $"{name} {version}" : "engine not installed";
        }
    }

    /// <summary>Bound to the engine picker so the labels refresh the moment it changes.</summary>
    public string PreferredEngine
    {
        get => State.Settings.PreferredEngine;
        set
        {
            if (string.Equals(State.Settings.PreferredEngine, value, StringComparison.Ordinal)) return;
            State.Settings.PreferredEngine = value ?? "auto";
            _store.Save();
            RaiseAll(nameof(PreferredEngine), nameof(EngineStatusText), nameof(EngineChoiceHint));
        }
    }

    /// <summary>Explains what the engine picker will actually do for this profile.</summary>
    public string EngineChoiceHint
    {
        get
        {
            var preference = (State.Settings.PreferredEngine ?? "auto").ToLowerInvariant();
            var xrayInstalled = new EngineInstaller().GetActive(EngineCatalog.Get("xray")!) is not null;

            return preference switch
            {
                "xray" when !xrayInstalled =>
                    "Xray-core is not installed yet. Install it under Cores & updates first.",
                "xray" =>
                    "Xray-core routes VLESS, VMess, Trojan, Shadowsocks, SOCKS and HTTP. Hysteria2, TUIC, AnyTLS and whole-computer mode fall back to sing-box.",
                "singbox" => "sing-box routes every protocol and powers the live connection stats.",
                _ => "sing-box when it is installed, otherwise Xray-core.",
            };
        }
    }

    public string EngineVersion
    {
        get => _engineVersion;
        private set
        {
            if (Set(ref _engineVersion, value)) RaiseAll(nameof(EngineStatusText), nameof(EngineChoiceHint));
        }
    }

    // ----------------------------------------------------------------- toast

    public string? Toast
    {
        get => _toast;
        private set
        {
            if (Set(ref _toast, value)) Raise(nameof(HasToast));
        }
    }

    public bool HasToast => !string.IsNullOrEmpty(Toast);

    public void ShowToast(string message)
    {
        // Toasts are often raised from engine/event threads. A DispatcherTimer created there
        // belongs to that thread's dispatcher, which never pumps — so the toast would stay
        // on screen forever. Always marshal to the UI dispatcher first.
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => ShowToast(message));
            return;
        }

        // Long unbroken text (a JSON blob from a crashed client, say) would break the layout.
        Toast = string.IsNullOrEmpty(message) || message.Length <= 260
            ? message
            : message[..257] + "…";
        // Long messages (error explanations) need time to read; short ones should not linger.
        var seconds = Math.Clamp(3.5 + (message.Length / 60.0), 3.5, 15.0);
        _toastTimer ??= new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Stop();
        _toastTimer.Tick -= ClearToast;
        _toastTimer.Tick += ClearToast;
        _toastTimer.Start();
    }

    private void ClearToast(object? sender, EventArgs e)
    {
        _toastTimer?.Stop();
        Toast = null;
    }

    // ------------------------------------------------------------ onboarding

    public bool IsOnboardingVisible
    {
        get => _isOnboardingVisible;
        private set => Set(ref _isOnboardingVisible, value);
    }

    public int OnboardingStep
    {
        get => _onboardingStep;
        set
        {
            if (Set(ref _onboardingStep, value)) RaiseAll(nameof(OnboardingIsIntro), nameof(OnboardingIsMode), nameof(OnboardingIsTarget));
        }
    }

    public bool OnboardingIsIntro => OnboardingStep == 0;
    public bool OnboardingIsMode => OnboardingStep == 1;
    public bool OnboardingIsTarget => OnboardingStep >= 2;

    public void ShowOnboarding(int step = 0)
    {
        OnboardingStep = step;
        IsOnboardingVisible = true;
    }

    public void HideOnboarding() => IsOnboardingVisible = false;

    // ------------------------------------------------------------- log filter

    public string LogFilter
    {
        get => _logFilter;
        set
        {
            if (Set(ref _logFilter, value)) RebuildVisibleLogs();
        }
    }

    private bool MatchesFilter(LogLine line) =>
        string.IsNullOrWhiteSpace(_logFilter) ||
        line.Message.Contains(_logFilter, StringComparison.OrdinalIgnoreCase) ||
        line.Source.Contains(_logFilter, StringComparison.OrdinalIgnoreCase);

    private void RebuildVisibleLogs()
    {
        VisibleLogs.Clear();
        foreach (var line in Logs.Reverse().Where(MatchesFilter).Take(600))
            VisibleLogs.Add(line);
    }

    public string NewLinkText
    {
        get => _newLinkText;
        set => Set(ref _newLinkText, value);
    }

    public bool AdvancedExpanded
    {
        get => _advancedExpanded;
        set => Set(ref _advancedExpanded, value);
    }

    // --------------------------------------------------------------- commands

    public AsyncRelayCommand ToggleConnectCommand { get; }
    public AsyncRelayCommand AddFolderCommand { get; }
    public AsyncRelayCommand AddApplicationCommand { get; }
    public RelayCommand AddProcessNameCommand { get; }
    public RelayCommand RemoveTargetCommand { get; }
    public RelayCommand LaunchTargetCommand { get; }
    public RelayCommand BrowseToTargetCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public AsyncRelayCommand AddNodesFromTextCommand { get; }
    public RelayCommand RemoveNodeCommand { get; }
    public RelayCommand SelectNodeCommand { get; }
    public RelayCommand SelectExternalCommand { get; }
    public RelayCommand UseDirectCommand { get; }
    public AsyncRelayCommand DetectProxiesCommand { get; }
    public AsyncRelayCommand DownloadEngineCommand { get; }
    public RelayCommand CancelDownloadCommand { get; }
    public AsyncRelayCommand LocateEngineCommand { get; }
    public RelayCommand SetModeCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand ClearLogsCommand { get; }
    public RelayCommand FinishOnboardingCommand { get; }
    public RelayCommand NextOnboardingCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand CopyTextCommand { get; }
    public RelayCommand OpenProjectPageCommand { get; }
    public RelayCommand OpenIssuesCommand { get; }
    public RelayCommand OpenLicenceCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }

    /// <summary>The public home of the project. One place to change if it ever moves.</summary>
    public const string ProjectUrl = "https://github.com/GamerFoid/RouteScope";

    private void CopyText(object? value)
    {
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            Clipboard.SetText(text);
            ShowToast("Address copied to the clipboard");
        }
        catch
        {
            ShowToast("Could not reach the clipboard. Select the text and copy it manually.");
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }
    public RelayCommand OpenConfigCommand { get; }
    public RelayCommand AddExternalCommand { get; }
    public RelayCommand RemoveExternalCommand { get; }
    public AsyncRelayCommand CloseConnectionsCommand { get; }
    public AsyncRelayCommand TestLatencyCommand { get; }
    public AsyncRelayCommand TestChainHopPingCommand { get; }
    public RelayCommand CopyNodeLinkCommand { get; }
    public AsyncRelayCommand InstallEngineCommand { get; }
    public AsyncRelayCommand CheckEngineUpdatesCommand { get; }
    public RelayCommand LaunchAllCommand { get; }
    public RelayCommand AddExternalPresetCommand { get; }
    public RelayCommand AddPsiphonCommand { get; }
    public RelayCommand AddWireGuardCommand { get; }
    public RelayCommand AddOpenVpnCommand { get; }
    public RelayCommand LocateOpenVpnCommand { get; }
    public RelayCommand OpenVpnDownloadCommand { get; }

    /// <summary>Where openvpn.exe was found, or what to do about it.</summary>
    public string OpenVpnStatusText
    {
        get
        {
            var configured = Core.Runtime.OpenVpnSession.FindBinary(State.Settings.OpenVpnBinaryPath);
            if (configured is not null) return configured;
            return "Not found. Install OpenVPN, or press Locate… to point at openvpn.exe.";
        }
    }

    private void LocateOpenVpn()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Locate openvpn.exe",
            Filter = "openvpn.exe|openvpn.exe|Executables (*.exe)|*.exe",
        };

        if (dialog.ShowDialog() != true) return;

        State.Settings.OpenVpnBinaryPath = dialog.FileName;
        _store.Save();
        RaiseAll(nameof(OpenVpnStatusText));
        ShowToast("OpenVPN located");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch { }
    }
    public RelayCommand BrowsePsiphonCommand { get; }
    public RelayCommand AddChainHopCommand { get; }
    public RelayCommand RemoveChainHopCommand { get; }
    public RelayCommand SelectServerCommand { get; }
    public RelayCommand AddServerToChainCommand { get; }
    public RelayCommand EditServerCommand { get; }
    public RelayCommand RemoveServerCommand { get; }
    public RelayCommand MoveChainHopUpCommand { get; }
    public RelayCommand MoveChainHopDownCommand { get; }
    public RelayCommand EditExternalCommand { get; }
    public RelayCommand EditNodeCommand { get; }
    public AsyncRelayCommand TestRealPingCommand { get; }
    public RelayCommand AddSubscriptionCommand { get; }
    public RelayCommand RemoveSubscriptionCommand { get; }
    public AsyncRelayCommand UpdateSubscriptionCommand { get; }
    public AsyncRelayCommand UpdateSubscriptionViaProxyCommand { get; }

    // ------------------------------------------------------------- operations

    private void Navigate(object? page)
    {
        if (page is Page p) CurrentPage = p;
        else if (page is string s && Enum.TryParse<Page>(s, true, out var parsed)) CurrentPage = parsed;
    }

    private async Task ToggleConnectAsync()
    {
        if (IsRunning)
        {
            await StopAsync();
            return;
        }
        await StartAsync();
    }

    public async Task StartAsync()
    {
        IsBusy = true;
        BusyMessage = "Starting the engine…";
        _lastError = null;
        try
        {
            var result = await _engine.StartAsync(State, _installer, CancellationToken.None);

            if (!result.Ok)
            {
                switch (result.ErrorCode)
                {
                    case "needs-admin":
                        BusyMessage = "Waiting for administrator approval…";
                        IsBusy = false;
                        var elevated = Elevation.RelaunchElevated("--autostart");
                        if (elevated)
                        {
                            Application.Current.Shutdown();
                            return;
                        }
                        ShowToast("Administrator rights are required for transparent mode.");
                        RunState = RunState.Faulted;
                        _lastError = result.Error;
                        return;

                    case "engine-missing":
                        IsBusy = false;
                        await DownloadEngineAsync();
                        if (EngineReady)
                        {
                            // The user pressed Start before the engine existed; carry on now.
                            await StartAsync();
                        }
                        return;

                    default:
                        RunState = RunState.Faulted;
                        _lastError = result.Error;

                        // Release everything the failed start may have opened: a leftover
                        // OpenVPN keeps the TAP adapter busy, and the next attempt then fails
                        // with a completely different error.
                        try { await _engine.StopAsync(); } catch { }

                        ShowToast(result.Error ?? "Could not start.");
                        return;
                }
            }

            RunState = RunState.Running;
            HookClash();
            HookInterceptor();
            _store.Save();
            if (Settings.NotifyOnStateChange) ShowToast($"Routing through {_engine.ProxyDescription}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task StopAsync()
    {
        IsBusy = true;
        BusyMessage = "Stopping…";
        try
        {
            await _engine.StopAsync();
            UnhookClash();
            RunState = RunState.Stopped;
            UpSpeed = 0;
            DownSpeed = 0;
            Connections.Clear();
            ConnectionCount = 0;
            foreach (var t in Targets)
            {
                t.LiveConnections = null;
                t.IsVerified = false;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void HookClash()
    {
        var clash = _engine.Clash;
        if (clash is null) return;
        clash.Traffic += OnTraffic;
        clash.ConnectionsUpdated += OnConnections;
    }

    private void HookInterceptor()
    {
        var interceptor = _engine.Interceptor;
        if (interceptor is null) return;
        interceptor.ProcessMatched += OnProcessMatched;

        // Xray has no Clash API. The relay still counts every byte, so use those counters
        // for the graph and totals. The connection list cannot be reconstructed from them.
        if (_engine.Clash is not null) return;

        _fallbackUp = interceptor.BytesUploaded;
        _fallbackDown = interceptor.BytesDownloaded;
        _fallbackTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) =>
            {
                var current = _engine.Interceptor;
                if (current is null) return;

                var up = current.BytesUploaded;
                var down = current.BytesDownloaded;
                OnTraffic(new TrafficSample(
                    Math.Max(0, up - _fallbackUp),
                    Math.Max(0, down - _fallbackDown)));
                _fallbackUp = up;
                _fallbackDown = down;
            }, _dispatcher);
        _fallbackTimer.Start();
    }

    /// <summary>Fired from a driver thread the first time a target actually gets intercepted.</summary>
    private void OnProcessMatched(string path)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var name = Path.GetFileName(path);
            foreach (var target in Targets)
                if (target.Matches(path, name))
                    target.IsVerified = true;
        });
    }

    private void UnhookClash()
    {
        if (_fallbackTimer is not null)
        {
            _fallbackTimer.Stop();
            _fallbackTimer = null;
        }

        var clash = _engine.Clash;
        if (clash is null) return;
        clash.Traffic -= OnTraffic;
        clash.ConnectionsUpdated -= OnConnections;
    }

    private void OnTraffic(TrafficSample sample)
    {
        _dispatcher.BeginInvoke(() =>
        {
            UpSpeed = sample.Upload;
            DownSpeed = sample.Download;
            TotalUp += sample.Upload;
            TotalDown += sample.Download;
        });
    }

    private void OnConnections(IReadOnlyList<ConnectionInfo> list)
    {
        _dispatcher.BeginInvoke(() =>
        {
            ConnectionCount = list.Count;
            Connections.Clear();
            foreach (var c in list.OrderByDescending(x => x.Total).Take(120)) Connections.Add(c);

            foreach (var t in Targets)
            {
                var count = list.Count(c => MatchesTarget(t, c));
                t.LiveConnections = count > 0 ? count : null;
                if (count > 0) t.IsVerified = true;
            }
        });
    }

    private static bool MatchesTarget(ProxyTarget target, ConnectionInfo c)
    {
        if (string.IsNullOrEmpty(c.ProcessPath) && string.IsNullOrEmpty(c.ProcessName)) return false;
        return target.Matches(c.ProcessPath, c.ProcessName);
    }

    private void Tick()
    {
        _upHistory.Enqueue(UpSpeed);
        _downHistory.Enqueue(DownSpeed);
        while (_upHistory.Count > 90) _upHistory.Dequeue();
        while (_downHistory.Count > 90) _downHistory.Dequeue();
        UpSeries = _upHistory.ToArray();
        DownSeries = _downHistory.ToArray();
    }

    private void OnEngineStateChanged(RunState state, string? detail)
    {
        _dispatcher.BeginInvoke(() =>
        {
            RunState = state;
            if (detail is not null) _lastError = detail;
        });
    }

    private void OnLogLine(LogLine line)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Logs.Add(line);
            while (Logs.Count > 1200) Logs.RemoveAt(0);

            if (MatchesFilter(line))
            {
                VisibleLogs.Insert(0, line);
                while (VisibleLogs.Count > 600) VisibleLogs.RemoveAt(VisibleLogs.Count - 1);
            }
        });
    }

    // --------------------------------------------------------------- targets

    private async Task AddFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder to route through the proxy",
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true) return;

        AddTarget(new ProxyTarget
        {
            Kind = TargetKind.Folder,
            Value = dialog.FolderName,
            DisplayName = Path.GetFileName(dialog.FolderName.TrimEnd('\\')),
        });

        if (EngineMissing) await DownloadEngineAsync();
    }

    private async Task AddApplicationAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an application to route",
            Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;

        foreach (var file in dialog.FileNames)
        {
            AddTarget(new ProxyTarget
            {
                Kind = TargetKind.Application,
                Value = file,
                DisplayName = Path.GetFileNameWithoutExtension(file),
            });
        }

        if (EngineMissing) await DownloadEngineAsync();
    }

    public void AddProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var clean = name.Trim();
        if (!clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) clean += ".exe";

        if (Targets.Any(t => t.Kind == TargetKind.ProcessName &&
                             string.Equals(t.Value, clean, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast($"{clean} is already in the list.");
            return;
        }

        AddTarget(new ProxyTarget
        {
            Kind = TargetKind.ProcessName,
            Value = clean,
            DisplayName = Path.GetFileNameWithoutExtension(clean),
        });
    }

    public void AddTarget(ProxyTarget target)
    {
        if (target.Kind != TargetKind.ProcessName && !string.IsNullOrWhiteSpace(target.Value))
        {
            var exists = Targets.Any(t => t.Kind == target.Kind &&
                                          string.Equals(t.Value, target.Value, StringComparison.OrdinalIgnoreCase));
            if (exists)
            {
                ShowToast($"{target.EffectiveName} is already routed.");
                return;
            }
        }

        Targets.Add(target);
        State.Targets.Add(target);
        _store.Save();
        RaiseTargetsChanged();
        ShowToast($"Added {target.EffectiveName}");
    }

    private void RemoveTarget(ProxyTarget? target)
    {
        if (target is null) return;
        Targets.Remove(target);
        State.Targets.Remove(target);
        _store.Save();
        RaiseTargetsChanged();
    }

    private void RaiseTargetsChanged()
    {
        RaiseAll(nameof(HasTargets), nameof(HasNoTargets), nameof(TargetCount), nameof(EnabledTargetsCount),
            nameof(StatusTitle));
    }

    private void LaunchTarget(ProxyTarget? target)
    {
        if (target is null) return;

        var exe = ResolveTargetExe(target);
        if (exe is null)
        {
            ShowToast("Open this target's folder and start it yourself — RouteScope will pick it up.");
            return;
        }

        var launcher = new AppProxyLauncher(_engine.MixedPort > 0 ? _engine.MixedPort : Settings.MixedPort);
        launcher.Launch(exe);
        ShowToast($"Launched {Path.GetFileName(exe)} · {launcher.DescribeMechanism(exe)}");
    }

    /// <summary>Launches every enabled target so the user does not have to hunt them down.</summary>
    private void LaunchAllTargets()
    {
        var launcher = new AppProxyLauncher(_engine.MixedPort > 0 ? _engine.MixedPort : Settings.MixedPort);
        var launched = 0;

        foreach (var target in Targets.Where(t => t.Enabled))
        {
            var exe = ResolveTargetExe(target);
            if (exe is null) continue;
            if (launcher.Launch(exe) is not null) launched++;
        }

        ShowToast(launched == 0
            ? "Nothing to launch — add an application or a folder with a .exe in it."
            : $"Launched {launched} app{(launched == 1 ? "" : "s")} through the proxy.");
    }

    private static string? ResolveTargetExe(ProxyTarget target) => target.Kind switch
    {
        TargetKind.Application => File.Exists(target.Value) ? target.Value : null,
        TargetKind.Folder => ProcessScanner.FindExecutables(target.Value).FirstOrDefault(),
        _ => null,
    };

    private void BrowseToTarget(ProxyTarget? target)
    {
        if (target is null) return;
        try
        {
            var path = target.Kind == TargetKind.Folder ? target.Value : Path.GetDirectoryName(target.Value);
            if (path is not null && Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    // --------------------------------------------------------------- servers

    private async Task AddNodesFromTextAsync()
    {
        var text = NewLinkText;
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast("Paste a share link or a subscription first.");
            return;
        }

        List<ProxyNode> parsed;
        if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            parsed = await SubscriptionFetcher.FetchAsync(text, CancellationToken.None);
        }
        else
        {
            parsed = NodeLinkParser.ParseMany(text);
        }

        if (parsed.Count == 0)
        {
            ShowToast("No servers could be read from that.");
            return;
        }

        foreach (var node in parsed)
        {
            Nodes.Add(node);
            State.Nodes.Add(node);
        }

        NewLinkText = "";
        State.Route = RouteKind.Node;
        State.ActiveNodeId ??= parsed[0].Id;
        _store.Save();
        RefreshSelectionFlags();
        Raise(nameof(NodeCount));
        ShowToast(parsed.Count == 1 ? $"Added {parsed[0].Name}" : $"Added {parsed.Count} servers");
    }

    private void RemoveNode(ProxyNode? node)
    {
        if (node is null) return;
        Nodes.Remove(node);
        State.Nodes.Remove(node);
        if (State.ActiveNodeId == node.Id)
        {
            State.ActiveNodeId = Nodes.FirstOrDefault()?.Id;
            if (Nodes.Count == 0 && State.Route == RouteKind.Node) State.Route = RouteKind.External;
        }
        _store.Save();
        RefreshSelectionFlags();
        Raise(nameof(NodeCount));
    }

    private void SelectNode(ProxyNode? node)
    {
        if (node is null) return;
        State.Route = RouteKind.Node;
        State.ActiveNodeId = node.Id;
        _store.Save();
        RefreshSelectionFlags();
        ShowToast($"Server set to {node.Name}");

        // Picking a server also checks it, so the row always shows a live latency.
        _ = TestLatencyAsync(node);
    }

    private void SelectExternal(ExternalProxy? ext)
    {
        if (ext is null) return;
        State.Route = RouteKind.External;
        State.ActiveExternalId = ext.Id;
        _store.Save();
        RefreshSelectionFlags();
        ShowToast($"Chaining through {ext.Endpoint}");

        _ = TestLatencyAsync(ext);
    }

    private void UseDirect()
    {
        State.Route = RouteKind.Direct;
        State.Chain.Clear();
        _store.Save();
        RefreshSelectionFlags();
        ResolveChainHops();
        ShowToast("Traffic goes direct.");
    }

    private async Task DetectProxiesAsync()
    {
        IsBusy = true;
        BusyMessage = "Looking for local proxies…";
        try
        {
            var found = await ProxyDetector.ScanAsync();

            // Never offer RouteScope's own ports as something to chain to.
            found = found
                .Where(f => f.Port != State.Settings.MixedPort && f.Port != State.Settings.ClashApiPort)
                .ToList();

            foreach (var ext in Externals) ext.IsReachable = false;

            var added = 0;
            foreach (var f in found)
            {
                var existing = Externals.FirstOrDefault(e => e.Port == f.Port && e.Host == f.Host);
                if (existing is not null)
                {
                    existing.IsReachable = true;
                    existing.DetectedType = f.Type;
                    existing.LastSeenUtc = DateTime.UtcNow;
                }
                else
                {
                    var created = new ExternalProxy
                    {
                        Name = LabelFor(f),
                        Host = f.Host,
                        Port = f.Port,
                        Type = f.Type,
                        AutoDetected = true,
                        IsReachable = true,
                        DetectedType = f.Type,
                        LastSeenUtc = DateTime.UtcNow,
                    };
                    Externals.Add(created);
                    State.Externals.Add(created);
                    added++;
                }
            }

            _store.Save();
            RefreshSelectionFlags();

            ShowToast(found.Count == 0
                ? "No local proxy found. Start v2rayN, Clash or Hiddify first."
                : added > 0
                    ? $"Found {found.Count} local prox{(found.Count == 1 ? "y" : "ies")}, {added} new."
                    : $"Found {found.Count} local prox{(found.Count == 1 ? "y" : "ies")} — all already listed.");
        }
        finally { IsBusy = false; }
    }

    /// <summary>Prefers the real process name ("clash"), falling back to a port guess.</summary>
    private static string LabelFor(DetectedProxy proxy)
    {
        if (!string.IsNullOrWhiteSpace(proxy.ProcessName))
            return $"{proxy.ProcessName} · {proxy.Port}";
        return GuessName(proxy.Port);
    }

    private static string GuessName(int port) => port switch
    {
        10808 or 10809 or 2080 or 2081 => "v2rayN",
        7890 or 7891 or 7897 => "Clash",
        20170 or 20171 => "Clash Verge",
        _ => $"Local proxy {port}",
    };

    /// <summary>One-click entry for the proxy clients people actually run. "name|port".</summary>
    private void AddExternalPreset(object? parameter)
    {
        if (parameter is not string spec) return;

        var parts = spec.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port)) return;

        var name = parts[0];
        if (Externals.Any(e => e.Port == port && string.Equals(e.Host, "127.0.0.1", StringComparison.Ordinal)))
        {
            ShowToast($"{name} is already in the list.");
            return;
        }

        var ext = new ExternalProxy { Name = name, Host = "127.0.0.1", Port = port, Type = "Auto" };
        Externals.Add(ext);
        State.Externals.Add(ext);
        State.ActiveExternalId ??= ext.Id;
        if (State.Route == RouteKind.Direct) State.Route = RouteKind.External;
        _store.Save();
        RefreshSelectionFlags();
        ShowToast($"{name} added on port {port}");
        _ = TestLatencyAsync(ext);
    }

    /// <summary>
    /// Adds Psiphon as a managed client. It is not an outbound the engine can dial: RouteScope
    /// starts psiphon-tunnel-core itself and chains it through whatever hop sits above it.
    /// </summary>
    private void AddPsiphon()
    {
        // The client is downloaded or located in Cores & updates, so send people there first.
        if (Core.Psiphon.PsiphonSession.FindBinary(State.Settings.PsiphonBinaryPath) is null)
        {
            CurrentPage = Page.Settings;
            ShowToast("Install Psiphon under Cores & updates, then add it here.");
            return;
        }

        if (Externals.Any(e => e.IsPsiphon))
        {
            ShowToast("Psiphon is already in the list.");
            return;
        }

        var ext = new ExternalProxy { Name = "Psiphon", Host = "127.0.0.1", Port = 0, Type = "Psiphon" };
        Externals.Add(ext);
        State.Externals.Add(ext);
        _store.Save();
        RefreshSelectionFlags();
        ShowToast("Psiphon added. Chain it after a local proxy when a direct connection is blocked.");
    }

    /// <summary>Country Psiphon should exit from. Empty means "let Psiphon choose".</summary>
    public string PsiphonRegion
    {
        get => State.Settings.PsiphonRegion ?? "";
        set
        {
            if (string.Equals(State.Settings.PsiphonRegion, value, StringComparison.Ordinal)) return;
            State.Settings.PsiphonRegion = value ?? "";
            _store.Save();
            Raise(nameof(PsiphonRegion));
        }
    }

    /// <summary>Download cores through the route instead of directly.</summary>
    public bool DownloadViaProxy
    {
        get => State.Settings.DownloadViaProxy;
        set
        {
            if (State.Settings.DownloadViaProxy == value) return;
            State.Settings.DownloadViaProxy = value;
            _store.Save();
            Raise(nameof(DownloadViaProxy));
        }
    }

    /// <summary>
    /// The proxy downloads should use. RouteScope's own port is preferred because it already
    /// carries the whole route, including a chain.
    /// </summary>
    /// <summary>True when the proxy switch is on but there is nothing to route through.</summary>
    private bool DownloadProxyUnavailable =>
        Settings.DownloadViaProxy && ResolveDownloadProxy() is null;

    private string? ResolveDownloadProxy()
    {
        if (!Settings.DownloadViaProxy) return null;

        if (IsRunning) return $"http://127.0.0.1:{Settings.MixedPort}";

        var ext = State.ActiveExternal;
        if (ext is not null && !ext.IsManaged && ext.Port > 0)
        {
            var type = ext.DetectedType ?? ext.Type;
            return string.Equals(type, "HTTP", StringComparison.OrdinalIgnoreCase)
                ? $"http://{ext.Host}:{ext.Port}"
                : $"socks5://{ext.Host}:{ext.Port}";
        }

        return null;
    }

    /// <summary>Keeps a probe's output from becoming a version label when it is not a version.</summary>
    private static string CleanVersionLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "local";

        var first = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();

        if (string.IsNullOrEmpty(first)) return "local";
        if (first.Contains('{') || first.Contains('"') || first.Length > 24) return "local";

        return first;
    }

    private async Task LocateEngineAsync(EngineRow? row)
    {
        if (row is null) return;
        var def = row.Definition;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Locate {def.DisplayName}",
            Filter = $"{def.DisplayName} ({def.ExeName})|{def.ExeName}|Executables (*.exe)|*.exe",
        };

        if (dialog.ShowDialog() != true) return;

        row.IsBusy = true;
        row.StatusText = "Reading the version…";
        try
        {
            // Clients such as Psiphon have no "version" argument; running them would only
            // produce an error, so they are simply labelled "local".
            var version = def.IsManagedClient
                ? "local"
                : CleanVersionLabel(await EngineInstaller.ProbeVersionAsync(def, dialog.FileName, CancellationToken.None));

            var installed = _installer.InstallLocalFile(def, dialog.FileName, version);

            if (installed is null)
            {
                row.StatusText = "That file could not be used.";
                return;
            }

            row.StatusText = "";
            if (def.IsRunnable) Settings.LastEngineVersion = installed.Version;
            _store.Save();
            RefreshEngineState();
            LoadEngines();
            RaiseAll(nameof(PsiphonStatusText));
            ShowToast($"{def.DisplayName} registered from your file.");
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    public string PsiphonStatusText
    {
        get
        {
            var binary = Core.Psiphon.PsiphonSession.FindBinary(State.Settings.PsiphonBinaryPath);
            return binary is null
                ? "psiphon-tunnel-core.exe not found — locate it to enable Psiphon"
                : binary;
        }
    }

    private void BrowsePsiphon()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Locate psiphon-tunnel-core.exe",
            Filter = "psiphon-tunnel-core (psiphon-tunnel-core.exe)|psiphon-tunnel-core.exe|Executables (*.exe)|*.exe",
        };

        if (dialog.ShowDialog() != true) return;

        State.Settings.PsiphonBinaryPath = dialog.FileName;
        _store.Save();
        RaiseAll(nameof(PsiphonStatusText));
        ShowToast("Psiphon binary saved");
    }

    private void AddExternal()
    {
        var ext = new ExternalProxy
        {
            Name = "Local proxy",
            Host = "127.0.0.1",
            Port = ProxyDetector.FindFreePort(10808),
            Type = "Auto",
        };

        var dialog = new Dialogs.ProxyEditorDialog(ext, isNew: true)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true) return;

        Externals.Add(ext);
        State.Externals.Add(ext);
        State.ActiveExternalId ??= ext.Id;
        if (State.Route == RouteKind.Direct) State.Route = RouteKind.External;
        _store.Save();
        RefreshSelectionFlags();
        ShowToast($"{ext.Name} added");
    }

    private void RemoveExternal(ExternalProxy? ext)
    {
        if (ext is null) return;

        Externals.Remove(ext);
        State.Externals.Remove(ext);

        // Drop any chain hop that pointed at it.
        State.Chain.RemoveAll(h => h.Kind == RouteKind.External && h.RefId == ext.Id);

        if (State.ActiveExternalId == ext.Id) State.ActiveExternalId = Externals.FirstOrDefault()?.Id;
        if (Externals.Count == 0 && State.Route == RouteKind.External) State.Route = RouteKind.Direct;

        _store.Save();
        RefreshSelectionFlags();
        ResolveChainHops();
        ShowToast($"{ext.Name} removed");
    }

    private async Task TestLatencyAsync(object? target)
    {
        if (target is ServerEntry latencyEntry) target = (object?)latencyEntry.Node ?? latencyEntry.Config;

        switch (target)
        {
            case ProxyNode node:
                node.LastLatencyMs = null;
                node.LastLatencyMs = await ProbeTcpAsync(node.Server, node.Port);
                break;

            case ExternalProxy { IsManaged: true } managed:
                // Psiphon is not running until routing starts, so there is no port to probe.
                ShowToast($"{managed.Name} starts with routing — there is nothing to ping yet.");
                break;

            case ExternalProxy ext:
                ext.PingMs = null;
                ext.PingMs = await ProbeTcpAsync(ext.Host, ext.Port);
                if (ext.PingMs is >= 0) ext.IsReachable = true;
                break;
        }
    }

    /// <summary>A plain TCP connect: is this endpoint listening at all?</summary>
    private static async Task<int> ProbeTcpAsync(string host, int port)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(4000);
            await client.ConnectAsync(host, port, cts.Token);
            return (int)sw.ElapsedMilliseconds;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Pings a chain hop through everything above it, by standing up a throwaway engine with
    /// the hops wired together exactly as they will be when routing starts.
    /// </summary>
    private async Task TestChainHopPingAsync(ChainHop? hop)
    {
        if (hop is null) return;

        var index = State.Chain.IndexOf(hop);
        if (index < 0) return;

        if (hop.IsManaged)
        {
            ShowToast($"{hop.DisplayName} starts with routing — there is nothing to ping yet.");
            return;
        }

        var hops = BuildHopsUpTo(index + 2);
        if (hops.Count < 2)
        {
            ShowToast("This hop needs a first hop below it.");
            return;
        }

        hop.PingMs = null;
        BusyMessage = $"Testing {hop.DisplayName} through the chain…";
        IsBusy = true;
        try
        {
            var ms = await RealPing.ThroughChainAsync(hops, 10_000, CancellationToken.None);
            hop.PingMs = ms;
            ShowToast(ms > 0
                ? $"{hop.DisplayName}: {ms} ms through {hops.Count - 1} hop(s) above it"
                : $"{hop.DisplayName}: no route through the chain");
        }
        finally { IsBusy = false; }
    }

    /// <summary>Builds the ordered outbounds for the route, cut off after <paramref name="count"/>.</summary>
    private List<RouteHop> BuildHopsUpTo(int count)
    {
        var hops = new List<RouteHop>();

        switch (State.Route)
        {
            case RouteKind.External when State.ActiveExternal is { } ext:
                hops.Add(RouteHop.From(ext));
                break;
            case RouteKind.Node when State.ActiveNode is { } node:
                hops.Add(RouteHop.From(node));
                break;
            default:
                return hops;
        }

        foreach (var hop in State.Chain.Take(Math.Max(0, count - 1)))
        {
            if (hop.Kind == RouteKind.External)
            {
                if (State.Externals.FirstOrDefault(e => e.Id == hop.RefId) is { } e) hops.Add(RouteHop.From(e));
            }
            else if (State.Nodes.FirstOrDefault(n => n.Id == hop.RefId) is { } n)
            {
                hops.Add(RouteHop.From(n));
            }
        }

        return hops;
    }

    private void CopyNodeLink(ProxyNode? node)
    {
        if (node is null) return;
        try
        {
            Clipboard.SetText(Core.Nodes.NodeLinkWriter.ToLink(node));
            ShowToast("Link copied to the clipboard");
        }
        catch (Exception ex)
        {
            ShowToast("Could not copy: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ mode

    private void SetMode(object? mode)
    {
        CaptureMode parsed;
        if (mode is CaptureMode m) parsed = m;
        else if (mode is string s && Enum.TryParse(s, true, out parsed)) { }
        else return;

        State.Mode = parsed;
        _store.Save();
        RaiseAll(nameof(ModeSummary), nameof(ModeHint), nameof(IsTransparent), nameof(IsAppProxy),
            nameof(IsSystemProxy), nameof(IsProcessMode), nameof(TargetsInUse), nameof(TargetsUnusedMessage));
        ShowToast($"Mode: {ModeSummary}");
    }

    // --------------------------------------------------------------- engine

    public void RefreshEngineState()
    {
        var installed = _installer.GetActive(EngineCatalog.SingBox);
        EngineReady = installed is not null;
        EngineVersion = installed?.Version ?? "not installed";

        foreach (var row in Engines)
        {
            var found = _installer.GetActive(row.Definition);
            row.IsInstalled = found is not null;
            row.InstalledVersion = found?.Version ?? "";
        }
    }

    private void LoadEngines()
    {
        if (Engines.Count == 0)
            foreach (var def in EngineCatalog.All)
                Engines.Add(new EngineRow(def));
        RefreshEngineState();
    }

    /// <summary>
    /// Installs or updates one core from its row. Checks the published version first, so an
    /// up-to-date core is never downloaded again.
    /// </summary>
    private async Task InstallEngineAsync(EngineRow? row)
    {
        if (row is null || row.IsBusy) return;

        if (DownloadProxyUnavailable)
        {
            ShowToast("Pick a local proxy first — the download-proxy switch has nothing to use.");
            return;
        }

        var def = row.Definition;

        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        row.IsBusy = true;
        row.Progress = 0;
        row.StatusText = "Checking for the current version…";

        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                row.Progress = p.Fraction;
                row.StatusText = p.Human + "  " + p.SpeedHuman;
            });

            var current = _installer.GetActive(def);

            if (def.RepoFilePath is { Length: > 0 })
            {
                var file = await GitHubReleases.GetRepoFileAsync(def.Repo, def.RepoFilePath, ct, ResolveDownloadProxy());
                if (file is null)
                {
                    row.StatusText = "GitHub is unreachable — set a mirror below.";
                    return;
                }

                var version = file.Sha.Length > 8 ? file.Sha[..8] : file.Sha;
                row.LatestVersion = version;

                if (current is not null && string.Equals(current.Version, version, StringComparison.OrdinalIgnoreCase))
                {
                    row.StatusText = "";
                    ShowToast($"{row.Name} is already up to date.");
                    return;
                }

                row.StatusText = $"Downloading {version}…";
                var installedFile = await _installer.InstallFromRepoFileAsync(def, file, Settings.Mirror, progress, ct, ResolveDownloadProxy());
                if (installedFile is null)
                {
                    row.StatusText = ct.IsCancellationRequested ? "Cancelled." : "Download failed — try a mirror below.";
                    return;
                }
            }
            else
            {
                var release = await GitHubReleases.GetLatestAsync(def.Repo, includePrerelease: false, ct, ResolveDownloadProxy());
                if (release is null)
                {
                    row.StatusText = "GitHub is unreachable — set a mirror below.";
                    return;
                }

                var asset = GitHubReleases.PickAsset(release, def);
                if (asset is null)
                {
                    row.StatusText = "That release has no Windows build.";
                    return;
                }

                row.LatestVersion = release.Version;

                if (current is not null && string.Equals(current.Version, release.Version, StringComparison.OrdinalIgnoreCase))
                {
                    row.StatusText = "";
                    ShowToast($"{row.Name} is already up to date ({release.Version}).");
                    return;
                }

                var installed = await _installer.InstallAsync(def, asset, release.Version, Settings.Mirror, progress, ct, ResolveDownloadProxy());
                if (installed is null)
                {
                    row.StatusText = ct.IsCancellationRequested ? "Cancelled." : "Download failed — try a mirror below.";
                    return;
                }

                if (def.IsRunnable) Settings.LastEngineVersion = installed.Version;
            }

            row.StatusText = "";
            row.Progress = 0;
            _store.Save();
            RefreshEngineState();
            LoadEngines();
            RaiseAll(nameof(PsiphonStatusText));
            ShowToast($"{row.Name} is ready");
        }
        catch (OperationCanceledException)
        {
            row.StatusText = "Cancelled.";
            row.Progress = 0;
        }
        finally
        {
            row.IsBusy = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }
    private async Task CheckEngineUpdatesAsync()
    {
        IsBusy = true;
        BusyMessage = "Checking for new core versions…";
        try
        {
            var tasks = Engines.Select(async row =>
            {
                var release = await GitHubReleases.GetLatestAsync(row.Definition.Repo, false, CancellationToken.None,
                ResolveDownloadProxy());
                if (release is not null) row.LatestVersion = release.Version;
                return release is not null;
            });

            var results = await Task.WhenAll(tasks);
            var found = Engines.Count(e => e.HasUpdate);
            ShowToast(results.All(r => !r)
                ? "Could not reach GitHub. Try a mirror in Advanced."
                : found == 0 ? "Everything is up to date." : $"{found} update(s) available.");
        }
        finally { IsBusy = false; }
    }

    private CancellationTokenSource? _downloadCts;

    private Task DownloadEngineAsync() => InstallEngineAsync(EngineCatalog.SingBox);

    /// <summary>
    /// Installs or updates one core. Checks first: if the installed version already matches
    /// what is published, nothing is downloaded at all.
    /// </summary>
    private async Task InstallEngineAsync(EngineDefinition def)
    {
        if (IsDownloading) return;

        if (DownloadProxyUnavailable)
        {
            ShowToast("Pick a local proxy first — the download-proxy switch has nothing to use.");
            return;
        }

        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        IsDownloading = true;
        DownloadFraction = 0;
        DownloadText = $"Checking {def.DisplayName}…";

        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                DownloadFraction = p.Fraction;
                DownloadText = $"{p.Human}  {p.SpeedHuman}";
            });

            if (def.RepoFilePath is { Length: > 0 })
            {
                var file = await GitHubReleases.GetRepoFileAsync(def.Repo, def.RepoFilePath, ct, ResolveDownloadProxy());
                if (file is null)
                {
                    ShowToast($"Could not reach GitHub for {def.DisplayName}.");
                    return;
                }

                var version = file.Sha.Length > 8 ? file.Sha[..8] : file.Sha;
                var current = _installer.GetActive(def);
                if (current is not null && string.Equals(current.Version, version, StringComparison.OrdinalIgnoreCase))
                {
                    DownloadText = "";
                    ShowToast($"{def.DisplayName} is already up to date ({version}).");
                    return;
                }

                DownloadText = $"Downloading {def.DisplayName} {version}…";
                var installed = await _installer.InstallFromRepoFileAsync(def, file, Settings.Mirror, progress, ct, ResolveDownloadProxy());
                if (installed is null)
                {
                    if (!ct.IsCancellationRequested)
                        ShowToast("The download failed. Try a mirror in Settings → Advanced.");
                    return;
                }

                Settings.LastEngineVersion = installed.Version;
            }
            else
            {
                var release = await GitHubReleases.GetLatestAsync(def.Repo, includePrerelease: false, ct, ResolveDownloadProxy());
                if (release is null)
                {
                    ShowToast("Could not reach GitHub. Try again, or set a mirror in Settings.");
                    return;
                }

                var asset = GitHubReleases.PickAsset(release, def);
                if (asset is null)
                {
                    ShowToast("That release has no Windows build.");
                    return;
                }

                var current = _installer.GetActive(def);
                if (current is not null && string.Equals(current.Version, release.Version, StringComparison.OrdinalIgnoreCase))
                {
                    DownloadText = "";
                    ShowToast($"{def.DisplayName} is already up to date ({release.Version}).");
                    return;
                }

                DownloadText = $"Downloading {def.DisplayName} {release.Version}…";
                var installed = await _installer.InstallAsync(def, asset, release.Version, Settings.Mirror, progress, ct, ResolveDownloadProxy());
                if (installed is null)
                {
                    if (!ct.IsCancellationRequested)
                        ShowToast("The download failed. Try a mirror in Settings → Advanced.");
                    return;
                }

                Settings.LastEngineVersion = installed.Version;
            }

            _store.Save();
            RefreshEngineState();
            LoadEngines();
            RaiseAll(nameof(PsiphonStatusText));
            ShowToast($"{def.DisplayName} installed.");
        }
        catch (OperationCanceledException)
        {
            DownloadText = "";
            ShowToast("Download cancelled.");
        }
        finally
        {
            IsDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private void CancelDownload()
    {
        try { _downloadCts?.Cancel(); } catch { }
        DownloadText = "Cancelling…";
    }

    private void InstallEngineById(object? id)
    {
        if (id is not string key) return;
        var def = EngineCatalog.Get(key);
        if (def is not null) _ = InstallEngineAsync(def);
    }
    private void OpenRuntimeConfig()
    {
        try
        {
            if (File.Exists(AppPaths.RuntimeConfigFile))
                Process.Start(new ProcessStartInfo(AppPaths.RuntimeConfigFile) { UseShellExecute = true });
            else
                ShowToast("Start routing once to generate the config.");
        }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    // --------------------------------------------------------------- settings

    /// <summary>Re-reads the collections after the underlying state was replaced (import/demo).</summary>
    public void ReloadFromStore()
    {
        Targets.Clear();
        foreach (var t in _store.State.Targets) Targets.Add(t);
        Nodes.Clear();
        foreach (var n in _store.State.Nodes) Nodes.Add(n);
        Externals.Clear();
        foreach (var e in _store.State.Externals) Externals.Add(e);
        Subscriptions.Clear();
        foreach (var s in _store.State.Subscriptions) Subscriptions.Add(s);
        RaiseAll(nameof(HasSubscriptions));
        RefreshSelectionFlags();
        ResolveChainHops();
        RaiseTargetsChanged();
        RaiseAll(nameof(NodeCount), nameof(RouteSummary), nameof(ModeSummary), nameof(ModeHint),
            nameof(IsTransparent), nameof(IsAppProxy), nameof(IsSystemProxy), nameof(IsProcessMode),
            nameof(TargetsInUse), nameof(TargetsUnusedMessage), nameof(EnabledTargetsCount));
    }

    /// <summary>Used by --demo and the screenshot harness so populated states can be reviewed.</summary>
    public void LoadDemoConnections(IReadOnlyList<ConnectionInfo> list)
    {
        Connections.Clear();
        foreach (var c in list) Connections.Add(c);
        ConnectionCount = list.Count;
    }

    public void ApplyDemoRuntime()
    {
        RunState = RunState.Running;
        EngineReady = true;
        EngineVersion = "1.14.4";
        UpSpeed = 1_240_000;
        DownSpeed = 8_640_000;
        TotalUp = 128_400_000;
        TotalDown = 1_940_000_000;

        var random = new Random(17);
        for (var i = 0; i < 64; i++)
        {
            // A gentle wave plus noise reads like real traffic instead of random spikes.
            var wave = 0.55 + 0.35 * Math.Sin(i / 9.0) + 0.12 * Math.Sin(i / 2.7);
            var up = Math.Max(30_000, wave * 1_600_000 * (0.85 + random.NextDouble() * 0.3));
            var down = Math.Max(80_000, wave * 9_000_000 * (0.85 + random.NextDouble() * 0.3));
            _upHistory.Enqueue(up);
            _downHistory.Enqueue(down);
        }
        UpSeries = _upHistory.ToArray();
        DownSeries = _downHistory.ToArray();
    }

    private void SaveSettings()
    {
        AutoStart.Set(Settings.LaunchAtLogin);
        _store.Save();
        RaiseAll(nameof(RouteSummary), nameof(MixedPortLabel));
        ShowToast("Settings saved");
    }

    private void ClearLogs()
    {
        Logs.Clear();
        VisibleLogs.Clear();
        LogBus.Clear();
    }

    private async Task CloseAllConnectionsAsync()
    {
        if (_engine.Clash is null) return;
        await _engine.Clash.CloseAllConnectionsAsync();
        ShowToast("Closed all connections");
    }

    private void FinishOnboarding()
    {
        IsOnboardingVisible = false;
        Settings.OnboardingCompleted = true;
        _store.Save();
    }

    public async ValueTask DisposeAsync()
    {
        _statsTimer.Stop();
        LogBus.LineWritten -= OnLogLine;
        await _engine.DisposeAsync();
    }
}


