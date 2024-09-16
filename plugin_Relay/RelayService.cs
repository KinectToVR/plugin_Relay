using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Amethyst.Plugins.Contract;
using Grpc.Core;
using MagicOnion;
using MagicOnion.Hosting;
using MagicOnion.Server;
using MagicOnion.Server.Hubs;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using IServiceEndpoint = Amethyst.Plugins.Contract.IServiceEndpoint;

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously

namespace plugin_Relay;

[Export(typeof(IServiceEndpoint))]
[ExportMetadata("Name", "Amethyst Tracking Relay")]
[ExportMetadata("Guid", "K2VRTEAM-AME2-APII-DVCE-TRACKINGRELAY")]
[ExportMetadata("Publisher", "K2VR Team")]
[ExportMetadata("Version", "1.0.0.1")]
[ExportMetadata("Website", "https://github.com/KimihikoAkayasaki/plugin_Relay")]
public class RelayService : IServiceEndpoint
{
    public RelayService()
    {
        Instance = this;
    }

    [Import(typeof(IAmethystHost))] public IAmethystHost Host { get; set; }
    public static RelayService Instance { get; private set; } // That's me!!
    public SortedSet<string> DevicesToUpdate { get; } = [];

    private bool PluginLoaded { get; set; }
    private Page InterfaceRoot { get; set; }
    private Exception InitException { get; set; }
    private Task ServerTask { get; set; }
    private IHost ServerHost { get; set; }
    private CancellationTokenSource ServerToken { get; set; }
    private bool IsShuttingDown { get; set; }
    public object SettingsInterfaceRoot => InterfaceRoot;
    public bool IsSettingsDaemonSupported => true;
    public int ServiceStatus { get; set; } = -1;
    public Uri ErrorDocsUri => new("https://docs.k2vr.tech/");
    public bool IsAmethystVisible => true;
    public bool IsRestartOnChangesNeeded => false;
    public bool CanAutoStartAmethyst => false;
    public string TrackingSystemName => "Relay";
    private TextBlock RefreshTextBlock { get; set; }

    public string ServiceStatusString => InitException is not null
        ? $"ERROR\n{InitException.GetType().Name}\n{InitException.Message}"
        : PluginLoaded
            ? ServiceStatus switch
            {
                0 => Host.RequestLocalizedString("/Statuses/Success"),
                1 => Host.RequestLocalizedString("/Statuses/Failure"),
                _ => $"Undefined: {ServiceStatus}\nE_UNDEFINED\nSomething weird has happened, though we can't tell what."
            }
            : $"Undefined: {ServiceStatus}\nE_UNDEFINED\nSomething weird has happened, though we can't tell what.";

    public SortedSet<TrackerType> AdditionalSupportedTrackerTypes =>
    [
        TrackerType.TrackerHanded,
        // TrackerType.TrackerLeftFoot, // Already OK
        // TrackerType.TrackerRightFoot, // Already OK
        TrackerType.TrackerLeftShoulder,
        TrackerType.TrackerRightShoulder,
        TrackerType.TrackerLeftElbow,
        TrackerType.TrackerRightElbow,
        TrackerType.TrackerLeftKnee,
        TrackerType.TrackerRightKnee,
        // TrackerType.TrackerWaist, // Already OK
        TrackerType.TrackerChest,
        TrackerType.TrackerCamera,
        TrackerType.TrackerKeyboard
    ];

    // ReSharper disable once AssignNullToNotNullAttribute
    public InputActions ControllerInputActions { get; set; } = null;

    public bool AutoStartAmethyst
    {
        get => false;
        set { }
    }

    public bool AutoCloseAmethyst
    {
        get => false;
        set { }
    }

    private int ServerPort
    {
        get => PluginLoaded ? Host?.PluginSettings.GetSetting("ServerPort", 10042) ?? 10042 : 10042;
        set
        {
            if (PluginLoaded) Host?.PluginSettings.SetSetting("ServerPort", value);
        }
    }

    public (Vector3 Position, Quaternion Orientation)? HeadsetPose => null;

    public void DisplayToast((string Title, string Text) message)
    {
        Host?.Log($"{message.Title} {message.Text} by Amethyst Tracking Relay!");
    }

    public TrackerBase GetTrackerPose(string contains, bool canBeFromAmethyst = true)
    {
        // ReSharper disable once AssignNullToNotNullAttribute
        return null;
    }

    public void Heartbeat()
    {
        if (ServiceStatus != 0 || Host is null) return;
        lock (Host.UpdateThreadLock)
        {
            DevicesToUpdate.Select(guid => _trackingDevices.GetValueOrDefault(guid, null))
                .Where(x => x is not null).Where(x => !x.IsSelfUpdateEnabled).ToList().ForEach(x => x.Update());
        }
    }

    private List<string> IpList
    {
        get
        {
            try
            {
                var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                return Windows.Networking.Connectivity.NetworkInformation.GetHostNames()
                    .Where(x => x.Type is HostNameType.Ipv4)
                    .Where(x => x.IPInformation?.NetworkAdapter?.NetworkAdapterId == profile?.NetworkAdapter?.NetworkAdapterId)
                    .Select(x => x.CanonicalName).ToList();
            }
            catch
            {
                return ["127.0.0.1"];
            }
        }
    }

    // This initializes/connects to the service
    public int Initialize()
    {
        Shutdown(); // Kill the background-ed server just in case
        Host?.Log("Tried to initialize Amethyst Tracking Relay!");

        try
        {
            MessagePackSerializer.DefaultOptions =
                MessagePackSerializerOptions.Standard.WithResolver(new CustomResolver());

            ServerToken = new CancellationTokenSource();
            ServerHost = MagicOnionHost.CreateDefaultBuilder()
                .UseMagicOnion(types: [typeof(DataService)],
                    options: new MagicOnionOptions { IsReturnExceptionStackTraceInErrorDetail = true },
                    ports: [new ServerPort("127.0.0.1", ServerPort, ServerCredentials.Insecure)],
                    channelOptions:
                    [
                        new ChannelOption(ChannelOptions.MaxReceiveMessageLength, -1),
                        new ChannelOption(ChannelOptions.MaxSendMessageLength, -1)
                    ])
                .ConfigureServices((_, services) =>
                {
                    services.Configure<MagicOnionHostingOptions>(options =>
                    {
                        options.ChannelOptions.MaxReceiveMessageLength = -1;
                        options.ChannelOptions.MaxSendMessageLength = -1;
                    });
                })
                .Build();

            ServerTask = ServerHost.StartAsync(ServerToken.Token);
            RefreshTextBlock.Visibility = Visibility.Collapsed;
            Host?.RefreshStatusInterface();
        }
        catch (Exception ex)
        {
            ServiceStatus = 1;
            InitException = ex;
            return 1;
        }

        ServiceStatus = 0;
        InitException = null;
        return 0; // S_OK
    }

    // This is called after the app loads the plugin
    public void OnLoad()
    {
        Host?.Log("Loading Amethyst Tracking Relay now!");
        PluginLoaded = true;

        var ipTextBlock = new TextBlock
        {
            Text = IpList.Count > 1 // Format as list if found multiple IPs!
                ? $"[ {string.Join(", ", IpList)} ]" // Or show a placeholder
                : IpList.ElementAtOrDefault(0) ?? "127.0.0.1",
            Margin = new Thickness(3), Opacity = 0.6
        };

        var ipLabelTextBlock = new TextBlock
        {
            Text = Host?.RequestLocalizedString(IpList.Count > 1
                ? "/Settings/Labels/LocalIP/Multiple"
                : "/Settings/Labels/LocalIP/One"),
            Margin = new Thickness { Left = 5, Top = 3, Right = 3, Bottom = 3 }
        };

        var portNumberBox = new NumberBox
        {
            Value = ServerPort,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 1, LargeChange = 1,
            Minimum = 0, Maximum = 65535,
            Header = new TextBlock
            {
                Text = "Web server port:"
            },
            Margin = new Thickness { Left = 5, Top = 3, Right = 3, Bottom = 3 }
        };

        RefreshTextBlock = new TextBlock
        {
            Text = "Click 'Refresh' to apply",
            Visibility = Visibility.Collapsed,
            FontSize = 12.0, Opacity = 0.5,
            Margin = new Thickness { Left = 5, Top = 3, Right = 3, Bottom = 3 }
        };

        InterfaceRoot = new Page
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children = { ipLabelTextBlock, ipTextBlock }
                    },
                    portNumberBox,
                    RefreshTextBlock
                }
            }
        };

        portNumberBox.ValueChanged += (sender, _) =>
        {
            if (double.IsNaN(sender.Value))
            {
                sender.Value = ServerPort;
                Host?.RefreshStatusInterface();
                return; // Don't do anything
            }

            ServerPort = (int)sender.Value;
            sender.Value = ServerPort;
            RefreshTextBlock.Visibility = Visibility.Visible;
            Host?.RefreshStatusInterface();
        };
    }

    // This is called when the service is closed
    public void Shutdown()
    {
        Host?.Log("Tried to shut down Amethyst Tracking Relay!");

        try
        {
            ServerToken?.Cancel();
            ServerHost?.StopAsync();

            ServerHost?.Dispose();
            ServerTask?.Dispose();
        }
        catch (Exception e)
        {
            Host?.Log(e);
        }

        InitException = null;
        ServiceStatus = -1;
    }

    public bool? RequestServiceRestart(string reason, bool wantReply = false)
    {
        return null; // Not needed
    }

    public async Task<(int Status, string StatusMessage, long PingTime)> TestConnection()
    {
        return (ServerTask.Status is TaskStatus.Running ? 0 : 1,
            ServerTask.Status is TaskStatus.Running ? "OK" : $"Server status is {ServerTask.Status}", 0L);
    }

    public Task<IEnumerable<(TrackerBase Tracker, bool Success)>> SetTrackerStates(
        IEnumerable<TrackerBase> trackerBases, bool wantReply = true)
    {
        return Task.FromResult(wantReply ? trackerBases.Select(x => (x, true)) : null);
    }

    public async Task<IEnumerable<(TrackerBase Tracker, bool Success)>> UpdateTrackerPoses(
        IEnumerable<TrackerBase> trackerBases, bool wantReply = true, CancellationToken? token = null)
    {
        try
        {
        }
        catch (Exception e)
        {
            Host?.Log(e);
        }

        return wantReply ? trackerBases.Select(x => (x, true)) : null;
    }

    ~RelayService()
    {
        Instance = null;
        IsShuttingDown = true;
    }

    private Dictionary<string, ITrackingDevice> _trackingDevices = [];

    public Dictionary<string, ITrackingDevice> GetTrackingDevices()
    {
        if (ServiceStatus != 0 || Host is null) return [];

        var trackingDevicesProperty = Host!.GetType().GetProperty("TrackingDevices");
        if (trackingDevicesProperty is null || !trackingDevicesProperty.CanRead)
            // ServiceStatusString = "Cannot read tracking data!\nE_UNAVAILABLE\nYou're using an unsupported version on Amethyst." // TODO
            return [];

        // Return all tracking devices from the host, try not to create any inbred ones...
        return _trackingDevices = ((Dictionary<string, ITrackingDevice>)trackingDevicesProperty.GetValue(Host) ?? [])
            .Where(x => x.Value is not TrackingDevice && x.Key is not "K2VRTEAM-AME2-APII-DVCE-TRACKINGRELAY")
            .ToDictionary(x => x.Key, x => x.Value); // Re-compose the dictionary prior to return
    }

    public ITrackingDevice GetTrackingDevice(string guid)
    {
        if (ServiceStatus != 0 || Host is null) return null;
        if (!_trackingDevices.ContainsKey(guid)) GetTrackingDevices();
        return _trackingDevices.GetValueOrDefault(guid, null);
    }

    public Action<string, bool> RequestShutdown { get; set; }
    public Action RequestReload { get; set; }

    public bool IsBackfeed
    {
        get
        {
            if (ServiceStatus != 0 || Host is null) return false; // Not working
            if (RelayDevice.Instance is null) return false; // No receiver active
            return RelayDevice.Instance.ServerIp is "127.0.0.1" or "localhost" && 
                   RelayDevice.Instance.ServerPort == ServerPort; // Backfeed O.O
        }
    }
}

// ReSharper disable once UnusedMember.Global
public class DataService : StreamingHubBase<IRelayService, IRelayClient>, IRelayService
{
    private IGroup _group;

    protected override async ValueTask OnConnecting()
    {
        _group = await Group.AddAsync(Guid.NewGuid().ToString());
        if (RelayService.Instance is not null)
        {
            RelayService.Instance.RequestShutdown = (reason, fatal) => Broadcast(_group).OnRequestShutdown(reason, fatal);
            RelayService.Instance.RequestReload = () => Broadcast(_group).OnRefreshInterface();
        }

        await base.OnConnecting();
    }

    public async Task<long> PingService()
    {
        return DateTime.Now.Ticks;
    }

    public async Task<bool> RequestShutdown(string reason = "", bool fatal = false)
    {
        if (RelayService.Instance is null) return false;
        RelayService.Instance.Host?.RequestExit(reason, fatal);
        return true; // Should be fine (probably)
    }

    public async Task<List<TrackingDevice>> ListTrackingDevices()
    {
        if (RelayService.Instance is null) return null;
        return RelayService.Instance.GetTrackingDevices()
            .Select(x => new TrackingDevice(x.Value) { DeviceGuid = x.Key, DeviceName = GetName(x.Key) }).ToList();
    }

    public async Task<TrackingDevice> GetTrackingDevice(string guid)
    {
        if (RelayService.Instance is null) return null;
        return new TrackingDevice(RelayService.Instance
            .GetTrackingDevice(guid)) { DeviceGuid = guid, DeviceName = GetName(guid) };
    }

    public async Task<List<TrackedJoint>> GetTrackedJoints(string guid)
    {
        if (RelayService.Instance is null) return null;
        RelayService.Instance.DevicesToUpdate.Add(guid); // Mark the device as used
        return RelayService.Instance.GetTrackingDevice(guid)?.TrackedJoints.ToList() ?? [];
    }

    public async Task<TrackingDevice> DeviceInitialize(string guid)
    {
        if (RelayService.Instance is null) return null;
        var device = RelayService.Instance.GetTrackingDevice(guid);
        lock (RelayService.Instance.Host.UpdateThreadLock)
        {
            device?.Initialize(); // Try initializing the device
        }

        return new TrackingDevice(device) { DeviceGuid = guid, DeviceName = GetName(guid) };
    }

    public async Task<TrackingDevice> DeviceShutdown(string guid)
    {
        if (RelayService.Instance is null) return null;
        var device = RelayService.Instance.GetTrackingDevice(guid);
        lock (RelayService.Instance.Host.UpdateThreadLock)
        {
            device?.Shutdown(); // Try shutting down the device
        }

        return new TrackingDevice(device) { DeviceGuid = guid, DeviceName = GetName(guid) };
    }

    public async Task<TrackingDevice> DeviceSignalJoint(string guid, int jointId)
    {
        if (RelayService.Instance is null) return null;
        var device = RelayService.Instance.GetTrackingDevice(guid);

        device?.SignalJoint(jointId); // Try signaling the device
        return new TrackingDevice(device) { DeviceGuid = guid, DeviceName = GetName(guid) };
    }

    public async Task<string> GetRemoteHostname()
    {
        return $"{Environment.UserName}@{Environment.MachineName}";
    }

    private string GetName(string guid)
    {
        if (RelayService.Instance is null) return null;
        var info = RelayService.Instance.Host.GetType().GetMethod("GetDeviceName", [typeof(string)]);
        return info is null ? guid : info.Invoke(RelayService.Instance.Host, [guid])?.ToString() ?? guid;
    }
}