// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amethyst.Plugins.Contract;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using plugin_Relay.Models;
using plugin_Relay.Pages;
using Microsoft.Extensions.Logging;
using ActualLab.Rpc;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace plugin_Relay;

[Export(typeof(ITrackingDevice))]
[ExportMetadata("Name", "Amethyst Tracking Relay")]
[ExportMetadata("Guid", "K2VRTEAM-AME2-APII-DVCE-TRACKINGRELAY")]
[ExportMetadata("Publisher", "K2VR Team")]
[ExportMetadata("Version", "1.0.0.5")]
[ExportMetadata("Website", "https://github.com/KimihikoAkayasaki/plugin_Relay")]
public class RelayDevice : ITrackingDevice
{
    public RelayDevice()
    {
        Instance = this;
    }

    [Import(typeof(IAmethystHost))] public IAmethystHost Host { get; set; }
    public static RelayDevice Instance { get; private set; } // That's me!!

    private Page InterfaceRoot { get; set; }
    public Exception InitException { get; set; }
    private ServiceProvider ServiceChannel { get; set; }
    public IRelayService Service { get; set; }
    public List<TrackingDevice> TrackingDevices { get; set; } = [];

    public Dictionary<string, (string Name, ITrackingDevice Device)> RelayTrackingDevices
    {
        get
        {
            if (Host is null || !PluginLoaded || !RelayReceiverEnabled) return []; // Completely give up for now
            var blacklist = Host.PluginSettings.GetSetting("DevicesBlacklist", new SortedSet<string>());

            return (DeviceStatus is not 0
                    ? Host.PluginSettings
                        .GetSetting("CachedRemoteDevices", new List<TrackingDevice>())
                    : TrackingDevices)
                .Where(x => !blacklist.Contains(x.DeviceGuid))
                .ToDictionary(x => $"TRACKINGRELAY:{x.DeviceGuid}", x => (x.DeviceName, x as ITrackingDevice));
        }
    }

    private DeviceSettings SettingsPage { get; set; }
    public bool PluginLoaded { get; set; }
    public RelayDeviceStatus Status { get; set; } = RelayDeviceStatus.NotInitialized;

    public bool StatusError => DeviceStatus is not 0;

    public string[] StatusSplit =>
        DeviceStatusString.Split('\n').Length is 3 ? DeviceStatusString.Split('\n') : ["Unknown", "S_UNKNWN", "Status unavailable."];

    public string RelayHostname { get; set; } = "Amethyst Tracking Relay";

    public string ServerIp
    {
        get => PluginLoaded ? Host?.PluginSettings.GetSetting("ClientIP", "127.0.0.1") ?? "127.0.0.1" : "127.0.0.1";
        set
        {
            if (PluginLoaded) Host?.PluginSettings.SetSetting("ClientIP", value);
        }
    }

    public int ServerPort
    {
        get => PluginLoaded ? Host?.PluginSettings.GetSetting("ClientPort", 10042) ?? 10042 : 10042;
        set
        {
            if (PluginLoaded) Host?.PluginSettings.SetSetting("ClientPort", value);
        }
    }

    public bool RelayReceiverEnabled
    {
        get => PluginLoaded && (Host?.PluginSettings.GetSetting("RelayReceiverEnabled", false) ?? false);
        set
        {
            if (PluginLoaded) Host?.PluginSettings.SetSetting("RelayReceiverEnabled", value);
        }
    }

    public bool IsPositionFilterBlockingEnabled => false;
    public bool IsPhysicsOverrideEnabled => false;
    public bool IsSelfUpdateEnabled => true;
    public bool IsFlipSupported => true;
    public bool IsAppOrientationSupported => true;
    public bool IsSettingsDaemonSupported => true;
    public object SettingsInterfaceRoot => InterfaceRoot;

    public bool IsInitialized { get; private set; }
    public bool IsSkeletonTracked => true;
    public int DeviceStatus => (int)Status;

    public string DeviceStatusString =>
        Status switch
        {
            RelayDeviceStatus.Success => Host.RequestLocalizedString("/RelayStatuses/Success"), // Everything's ready to go!
            RelayDeviceStatus.ServiceError => Host.RequestLocalizedString("/RelayStatuses/ServiceError").Replace("{}",
                InitException is not null ? $"{InitException?.GetType().Name} - {InitException?.Message}" : ""), // Couldn't create the channel - w/EX
            RelayDeviceStatus.ConnectionError => Host.RequestLocalizedString("/RelayStatuses/ConnectionError").Replace("{}",
                InitException is not null ? $"{InitException?.GetType().Name} - {InitException?.Message}" : ""), // Ping test/pull failed - w/EX
            RelayDeviceStatus.ConnectionLost => Host.RequestLocalizedString("/RelayStatuses/ConnectionLost").Replace("{}",
                InitException is not null ? $"{InitException?.GetType().Name} - {InitException?.Message}" : ""), // Device failed to update - w/EX
            RelayDeviceStatus.DevicesListEmpty => Host.RequestLocalizedString("/RelayStatuses/DevicesListEmpty"), // Pulled list was empty
            RelayDeviceStatus.BackFeedDetected => Host.RequestLocalizedString("/RelayStatuses/BackFeedDetected"), // Detected backfeed config
            RelayDeviceStatus.NotInitialized => Host.RequestLocalizedString("/RelayStatuses/NotInitialized"), // Not initialized yet
            RelayDeviceStatus.Disconnected => Host.RequestLocalizedString("/RelayStatuses/Disconnected"), // Disconnected by user
            _ when InitException is not null => Host.RequestLocalizedString("/RelayStatuses/Other")
                .Replace("{}", InitException is not null ? $"{InitException?.GetType().Name} - {InitException?.Message}" : ""), // Show the attached exception
            _ => $"Undefined: {Status}\nE_UNDEFINED\nSomething weird has happened, though we can't tell what."
        };

    public ObservableCollection<TrackedJoint> TrackedJoints => [];

    // ReSharper disable once AssignNullToNotNullAttribute
    public Uri ErrorDocsUri => null; // No dependencies anyway

    public void OnLoad()
    {
        if (!PluginLoaded) // Once
        {
            var lastRelayHostname = Host?.PluginSettings.GetSetting<string>("CachedRelayHostname");
            RelayHostname = string.IsNullOrEmpty(lastRelayHostname)
                ? RelayHostname
                : $"{lastRelayHostname} {Host?.RequestLocalizedString("/Cached") ?? "(Cached)"}";
        }

        Host?.Log("Loading Amethyst Tracking Relay now!");
        PluginLoaded = true;

        SettingsPage ??= new DeviceSettings { Device = this, Host = Host };
        InterfaceRoot ??= new Page
        {
            Content = SettingsPage,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        if (RelayReceiverEnabled)
            Task.Delay(3000).ContinueWith(_ =>
            {
                Host?.Log("Trying to connect to the cached remote server...");
                if (!(RelayService.Instance?.IsBackfeed ?? false)) Initialize();
            });
    }

    public void Initialize()
    {
        // Mark as initialized
        IsInitialized = true;

        MemoryPackFormatterProvider.Register(new TrackingDeviceFormatter());
        MemoryPackFormatterProvider.Register(new TrackedJointFormatter());

        if (RelayService.Instance?.IsBackfeed ?? false)
        {
            Status = RelayDeviceStatus.BackFeedDetected;
            SettingsPage.DeviceStatusAppendix = string.Empty;
            InitException = null;
            return; // Don't proceed further
        }

        var services = new ServiceCollection()
            .AddLogging();

        services.AddRpc()
            .AddWebSocketClient($"http://{ServerIp}:{ServerPort}/")
            .AddClient<IRelayService>();

        ServiceChannel = services.BuildServiceProvider();
        Service = ServiceChannel.GetRequiredService<IRelayService>();

        SettingsPage.DeviceStatusAppendix = string.Empty;
        SettingsPage.StartConnectionTest();

        Host.Log($"Tried to initialize with status: {DeviceStatusString}");
        InitException = null;
    }

    public void Shutdown()
    {
        // Mark as not initialized
        IsInitialized = false;

        try
        {
            Service = null;
            ServiceChannel = null;
        }
        catch (Exception ex)
        {
            Host?.Log(ex);
        }

        Host?.Log($"Tried to shut down with status: {DeviceStatusString}");
        Status = RelayDeviceStatus.Disconnected;
        InitException = null;
    }

    public void Update()
    {
        // ignored
    }

    public void SignalJoint(int jointId)
    {
        // ignored
    }

    public async Task PullRemoteDevices()
    {
        try
        {
            if (Host is null || !PluginLoaded) return; // Completely give up for now
            var updatedTrackingDevices = await Service.ListTrackingDevices();

            // Validate the new devices list
            if (updatedTrackingDevices.Count < 1)
            {
                // Invalidate the status
                InitException = null;
                Status = RelayDeviceStatus.DevicesListEmpty;
                Host.RefreshStatusInterface();
            }

            //updatedTrackingDevices.Clear();
            //for (var i = 0; i < 3; i++)
            //    updatedTrackingDevices.Add(new TrackingDevice
            //    {
            //        DeviceGuid = $"SAMPLE-FORWARDED-DEVICE-GUID-{i}",
            //        DeviceName = $"Sample Forwarded Device {i}",
            //        RemoteDeviceStatus = 0,
            //        RemoteDeviceStatusString = "Success!\nS_OK\nEverything's all right!",
            //        TrackedJoints = new ObservableCollection<TrackedJoint>(Enum.GetValues<TrackedJointType>()
            //            .Take(i + 5).Select(x => new TrackedJoint { Name = x.ToString(), Role = x }))
            //    });

            // Set up the devices so they can communicate
            Host.PluginSettings.SetSetting("CachedRemoteDevices", updatedTrackingDevices);
            foreach (var trackingDevice in updatedTrackingDevices)
            {
                var trackedJointsList = await Service.GetTrackedJoints(trackingDevice.DeviceGuid);
                trackingDevice.TrackedJoints = new ObservableCollection<TrackedJoint>(trackedJointsList ?? []);
                trackingDevice.IsSkeletonTracked = trackedJointsList is not null;

                trackingDevice.HostService = Service;
                trackingDevice.Host = this;
                trackingDevice.Loaded = true;
                trackingDevice.SetError = ex =>
                {
                    // Invalidate the status
                    InitException = ex;
                    Status = RelayDeviceStatus.ConnectionLost;
                    Host.RefreshStatusInterface();
                };
            }

            // Replace the devices in a synchronous context
            lock (Host.UpdateThreadLock)
            {
                TrackingDevices = updatedTrackingDevices;
            }

            Status = RelayDeviceStatus.Success; // Update the status only when everything's alright
            Host.GetType().GetMethod("ReloadRemoteDevices", Type.EmptyTypes)!.Invoke(Host, null);
            Host.RefreshStatusInterface(); // Tell amethyst to reload all remote devices from this plugin
        }
        catch (Exception e)
        {
            InitException = e;
            Status = RelayDeviceStatus.Other;
        }
    }

    public void TriggerDevicePull()
    {
        if (Host is null || !PluginLoaded) return; // Completely give up for now
        Host.GetType().GetMethod("ReloadRemoteDevices", Type.EmptyTypes)!.Invoke(Host, null);
        Host.RefreshStatusInterface(); // Tell amethyst to reload all remote devices from this plugin
    }

    ~RelayDevice()
    {
        if (Service is not null)
            Task.Run(async () => await Service.RequestShutdown("Client shutting down!")).Wait();
    }

    //public void OnRequestShutdown(string reason = "", bool fatal = false)
    //{
    //    if (PluginLoaded) Host?.RequestExit(reason, fatal);
    //}

    //public void OnRefreshInterface(string reason = "", bool fatal = false)
    //{
    //    if (PluginLoaded) Host?.RefreshStatusInterface();
    //}
}

public enum RelayDeviceStatus
{
    Success, // Everything's ready to go!
    ServiceError, // Couldn't create the channel - w/EX
    ConnectionError, // Ping test/pull failed - w/EX
    ConnectionLost, // Device failed to update - w/EX
    DevicesListEmpty, // Pulled list was empty
    BackFeedDetected, // Detected backfeed config
    NotInitialized, // Not initialized yet
    Disconnected, // Disconnected by user
    Other // Show the attached exception
}

// ReSharper disable once UnusedMember.Global
public class DataClient : IRelayClient
{
    public Task OnRequestShutdown(string reason = "", bool fatal = false, CancellationToken cancellationToken = default)
    {
        RelayDevice.Instance?.Host?.RequestExit(reason, fatal);
        return Task.CompletedTask;
    }

    public Task OnRefreshInterface(CancellationToken cancellationToken = default)
    {
        RelayDevice.Instance?.Host?.RefreshStatusInterface();
        return Task.CompletedTask;
    }
}