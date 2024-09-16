// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel.Composition;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Windows.UI.Text;
using Amethyst.Plugins.Contract;
using Grpc.Core;
using MagicOnion.Client;
using MessagePack;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT;
using MagicOnion.Server.Hubs;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace plugin_Relay;

[Export(typeof(ITrackingDevice))]
[ExportMetadata("Name", "Amethyst Tracking Relay")]
[ExportMetadata("Guid", "K2VRTEAM-AME2-APII-DVCE-TRACKINGRELAY")]
[ExportMetadata("Publisher", "K2VR Team")]
[ExportMetadata("Version", "1.0.0.1")]
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
    private Exception InitException { get; set; }
    private Channel ServiceChannel { get; set; }
    private IRelayService Service { get; set; }
    public List<TrackingDevice> TrackingDevices { get; set; } = [];

    public Dictionary<string, (string Name, ITrackingDevice Device)> RelayTrackingDevices
    {
        get
        {
            if (Host is null || !PluginLoaded) return []; // Completely give up for now
            return (DeviceStatus is not 0
                    ? Host.PluginSettings
                        .GetSetting("CachedRemoteDevices", new List<TrackingDevice>())
                    : TrackingDevices)
                .ToDictionary(x => $"TRACKINGRELAY:{x.DeviceGuid}", x => (x.DeviceName, x as ITrackingDevice));
        }
    }

    public bool IsPositionFilterBlockingEnabled => false;
    public bool IsPhysicsOverrideEnabled => false;
    public bool IsSelfUpdateEnabled => false;
    public bool IsFlipSupported => true;
    public bool IsAppOrientationSupported => true;
    public bool IsSettingsDaemonSupported => true;
    public object SettingsInterfaceRoot => InterfaceRoot;

    public bool IsInitialized { get; private set; }
    public bool IsSkeletonTracked => true;
    public int DeviceStatus => (int)Status;
    private TextBlock PortRefreshTextBlock { get; set; }
    private NumberBox ServerPortBox { get; set; }
    private TextBox ServerAddressBox { get; set; }
    private bool PluginLoaded { get; set; }
    private RelayDeviceStatus Status { get; set; } = RelayDeviceStatus.NotInitialized;

    public string DeviceStatusString => InitException is not null
        ? $"ERROR\n{InitException.GetType().Name}\n{InitException.Message}"
        : "Success!\nS_OK\nEverything's all fine!"; // TODO

    private string[] DeviceStatusStringSplit =>
        DeviceStatusString.Split('\n').Length is 3 ? DeviceStatusString.Split('\n') : ["Unknown", "S_UNKNWN", "Status unavailable."];

    public string RelayHostname { get; set; } = "Amethyst Tracking Relay";

    public ObservableCollection<TrackedJoint> TrackedJoints => [];

    public Uri ErrorDocsUri => null; // No dependencies anyway

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

    private TextBlock StatusHeaderText { get; set; }
    private TextBlock StatusContentText { get; set; }

    public void OnLoad()
    {
        if (!PluginLoaded) // Once
        {
            var lastRelayHostname = Host?.PluginSettings.GetSetting<string>("CachedRelayHostname");
            RelayHostname = string.IsNullOrEmpty(lastRelayHostname) ? RelayHostname : $"{lastRelayHostname} (Cached)"; // TODO LOCALIZE
        }

        Host?.Log("Loading Amethyst Tracking Relay now!");
        PluginLoaded = true;

        StatusHeaderText = new TextBlock
        {
            Text = DeviceStatusStringSplit[0],
            FontSize = 18
        };

        StatusContentText = new TextBlock
        {
            Text = DeviceStatusStringSplit[2],
            Opacity = 0.5, FontSize = 13
        };

        ServerAddressBox = new TextBox
        {
            Text = "127.0.0.1",
            PlaceholderText = "127.0.0.1",
            Header = new TextBlock
            {
                Text = "Web server address:"
            }
        };

        ServerPortBox = new NumberBox
        {
            Value = ServerPort,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 1,
            LargeChange = 1,
            Minimum = 0,
            Maximum = 65535,
            Header = new TextBlock
            {
                Text = "Web server port:"
            },
            Margin = new Thickness { Top = 15, Bottom = 3 }
        };

        PortRefreshTextBlock = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            FontSize = 12.0,
            Opacity = 0.5,
            Margin = new Thickness { Top = 3, Bottom = 3 }
        };

        var controlsStackPanel =
            new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children =
                {
                    ServerAddressBox,
                    ServerPortBox,
                    PortRefreshTextBlock
                }
            };

        var headerText =
            new TextBlock
            {
                Text = "Tracking Relay Settings",
                FontSize = 32.0,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness { Top = 3, Bottom = 25 }
            };

        var statusStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Status: ", FontWeight = FontWeights.SemiBold,
                            Padding = new Thickness { Right = 3 }, FontSize = 18
                        },
                        StatusHeaderText
                    }
                },
                StatusContentText
            }
        };

        var refreshButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE72C" },
            Padding = new Thickness(10, 6, 10, 6)
        };

        var statusGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children =
            {
                statusStack,
                refreshButton
            }
        };

        var devicesStackPanel = new StackPanel
        {
            Orientation = Orientation.Vertical
        };

        TrackingDevices.Select(x => new Grid
        {
            Background = Application.Current.Resources["ControlDisplayBackgroundBrush"].As<SolidColorBrush>(),
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"].As<SolidColorBrush>(),
            BorderThickness = Application.Current.Resources["ControlExampleDisplayBorderThickness"].As<Thickness>(),
            Padding = new Thickness(0, 3, 5, 13), Margin = new Thickness(0, 6, 0, 6),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(10, 0, 10, 0),
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = x.DeviceStatusStringSplit[0], HorizontalAlignment = HorizontalAlignment.Left,
                                    VerticalAlignment = VerticalAlignment.Top, FontWeight = FontWeights.SemiBold, Margin = new Thickness { Top = 5 },
                                    Foreground = Application.Current.Resources["SystemFillColorAttentionBrush"].As<SolidColorBrush>()
                                },
                                new TextBlock
                                {
                                    Text = x.DeviceStatusStringSplit[1], HorizontalAlignment = HorizontalAlignment.Left,
                                    VerticalAlignment = VerticalAlignment.Top, FontWeight = FontWeights.SemiBold,
                                    Margin = new Thickness { Left = 10, Top = 7 }, FontFamily = new FontFamily("Consolas"),
                                    Foreground = Application.Current.Resources["SystemFillColorNeutralBrush"].As<SolidColorBrush>()
                                }
                            }
                        },
                        new TextBlock
                        {
                            Text = x.DeviceGuid, HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top, FontWeight = FontWeights.SemiBold, Opacity = 0.7,
                            Margin = new Thickness { Right = 5, Top = 5 }, FontFamily = new FontFamily("Consolas"),
                            Foreground = Application.Current.Resources["SystemFillColorNeutralBrush"].As<SolidColorBrush>()
                        },
                        new TextBlock
                        {
                            Text = x.DeviceStatusStringSplit[2], HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top, FontWeight = FontWeights.SemiBold, Margin = new Thickness { Right = 5, Top = 8 },
                            Foreground = Application.Current.Resources["SystemFillColorNeutralBrush"].As<SolidColorBrush>()
                        }
                    }
                }
            }
        }).ToList().ForEach(devicesStackPanel.Children.Add);

        var devicesRepeater = new ScrollViewer
        {
            Content = devicesStackPanel
        };

        InterfaceRoot = new Page
        {
            Content =
                new Grid
                {
                    Padding = new Thickness(8, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Stretch,
                    RowDefinitions =
                    {
                        new RowDefinition { Height = GridLength.Auto },
                        new RowDefinition { Height = GridLength.Auto },
                        new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                        new RowDefinition { Height = GridLength.Auto }
                    },
                    Children =
                    {
                        headerText,
                        controlsStackPanel,
                        devicesRepeater,
                        statusGrid
                    }
                }
        };

        Grid.SetRow(headerText, 0);
        Grid.SetRow(controlsStackPanel, 1);
        Grid.SetRow(devicesRepeater, 2);
        Grid.SetRow(statusGrid, 3);

        Grid.SetColumn(statusStack, 0);
        Grid.SetColumn(refreshButton, 1);

        ServerAddressBox.LostFocus += (senderR, _) =>
        {
            if (senderR is not TextBox sender) return;
            if (!IPAddress.TryParse(sender.Text, out var _))
            {
                sender.Text = ServerIp;
                Host?.RefreshStatusInterface();
                return; // Don't do anything
            }

            ServerIp = sender.Text;
            Initialize(); // Re-init the client
            Host?.RefreshStatusInterface();
        };

        ServerPortBox.ValueChanged += (sender, _) =>
        {
            if (double.IsNaN(sender.Value))
            {
                sender.Value = ServerPort;
                Host?.RefreshStatusInterface();
                return; // Don't do anything
            }

            ServerPort = (int)sender.Value;
            sender.Value = ServerPort;
            Initialize(); // Re-init the client
            Host?.RefreshStatusInterface();
        };

        refreshButton.Click += (_, _) => Initialize();
    }

    public void Initialize()
    {
        // Mark as initialized
        IsInitialized = true;

        try
        {
            MessagePackSerializer.DefaultOptions =
                MessagePackSerializerOptions.Standard.WithResolver(new CustomResolver());

            var options = new[]
            {
                new ChannelOption(ChannelOptions.MaxReceiveMessageLength, -1),
                new ChannelOption(ChannelOptions.MaxSendMessageLength, -1)
            };

            ServiceChannel = new Channel(ServerIp is "127.0.0.1" ? "localhost" : ServerIp, ServerPort, ChannelCredentials.Insecure, options);
            Service = StreamingHubClient.Connect<IRelayService, IRelayClient>(ServiceChannel, new DataClient());

            // TODO DISCOVERY
            // USE void SetRelayInfoBarOverride((string Title, string Content, string Button, Action Click)? infoBarData)

            Task.Run(async () =>
            {
                PortRefreshTextBlock.DispatcherQueue.TryEnqueue(() =>
                {
                    PortRefreshTextBlock.Text = "Testing service connection...";
                    PortRefreshTextBlock.Foreground = Application.Current
                        .Resources["DefaultTextForegroundThemeBrush"].As<SolidColorBrush>();
                    PortRefreshTextBlock.Visibility = Visibility.Visible;
                });

                ServerAddressBox.DispatcherQueue.TryEnqueue(() => ServerAddressBox.IsEnabled = false);
                ServerPortBox.DispatcherQueue.TryEnqueue(() => ServerPortBox.IsEnabled = false);

                try
                {
                    var ping = await Service.PingService();
                    PortRefreshTextBlock.DispatcherQueue.TryEnqueue(() =>
                        PortRefreshTextBlock.Text = $"Tested ping time: {(DateTime.Now.Ticks - ping) / 10000} ms");

                    RelayHostname = await Service.GetRemoteHostname();
                    Host?.PluginSettings.SetSetting("CachedRelayHostname", RelayHostname);
                    await PullRemoteDevices(); // Finally do the thing! \^o^/ (no exceptions)
                }
                catch (Exception ex)
                {
                    InitException = ex;
                    Status = RelayDeviceStatus.ConnectionError;
                    PortRefreshTextBlock.DispatcherQueue.TryEnqueue(() =>
                    {
                        PortRefreshTextBlock.Text = $"Connection error: {ex.Message}";
                        PortRefreshTextBlock.Foreground = new SolidColorBrush(Colors.Red);
                    });
                }

                ServerAddressBox.DispatcherQueue.TryEnqueue(() => ServerAddressBox.IsEnabled = true);
                ServerPortBox.DispatcherQueue.TryEnqueue(() => ServerPortBox.IsEnabled = true);
            });
        }
        catch (Exception ex)
        {
            InitException = ex;
            Status = RelayDeviceStatus.ServiceError;
            return;
        }

        Host.Log($"Tried to initialize with status: {DeviceStatusString}");
        InitException = null;
    }

    private async Task PullRemoteDevices()
    {
        try
        {
            if (Host is null || !PluginLoaded) return; // Completely give up for now
            var updatedTrackingDevices = await Service.ListTrackingDevices();

            // Validate the new devices list
            if (updatedTrackingDevices.Count < 1)
            {
                // Invalidate the status
                InitException = new Exception("No valid devices.");
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
        }
    }

    public void Shutdown()
    {
        // Mark as not initialized
        IsInitialized = false;

        Host.Log($"Tried to shut down with status: {DeviceStatusString}");
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
    ServiceError, // Couldn't create the channel
    ConnectionError, // Ping test/pull failed
    ConnectionLost, // Device failed to update
    DevicesListEmpty, // Pulled list was empty

    //BackFeedDetected, // Detected backfeed config
    NotInitialized // Not initialized yet
}

// ReSharper disable once UnusedMember.Global
public class DataClient : IRelayClient
{
    public void OnRequestShutdown(string reason = "", bool fatal = false)
    {
        RelayDevice.Instance?.Host?.RequestExit(reason, fatal);
    }

    public void OnRefreshInterface(string reason = "", bool fatal = false)
    {
        RelayDevice.Instance?.Host?.RefreshStatusInterface();
    }
}