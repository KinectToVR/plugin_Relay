using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.Resources.Core;
using Windows.Storage;
using Windows.System;
using Amethyst.Plugins.Contract;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using plugin_Relay.Beacon;
using plugin_Relay.Models;

namespace plugin_Relay.Pages;

internal class DeviceDataTuple
{
    public DeviceSettings Host { get; set; }

    public string Name { get; set; }
    public string Guid { get; set; }

    public int Status { get; set; }
    public string DeviceStatusString { get; set; }

    public string[] StatusSplit =>
        DeviceStatusString.Split('\n').Length is 3 ? DeviceStatusString.Split('\n') : ["Unknown", "S_UNKNWN", "Status unavailable."];

    public bool IsEnabled
    {
        get => !(Host?.Host?.PluginSettings.GetSetting("DevicesBlacklist", new SortedSet<string>()).Contains(Guid) ?? false);
        set
        {
            var blacklist = Host?.Host?.PluginSettings
                .GetSetting("DevicesBlacklist", new SortedSet<string>()) ?? [];

            if (value) blacklist.Remove(Guid);
            else blacklist.Add(Guid);

            Host?.Host?.PluginSettings.SetSetting("DevicesBlacklist", blacklist);
            Host?.Device?.TriggerDevicePull(); // Reload everything now!
        }
    }
}

public class InfoBarData
{
    public string Title { get; set; }
    public string Content { get; set; }
    public string Button { get; set; }
    public Action Click { get; set; }
    public bool Closable { get; set; }
    public bool IsOpen { get; set; }
    public IPEndPoint Address { get; set; }

    public (string Title, string Content, string Button, Action Click, bool Closable)? AsPackedData =>
        IsOpen ? (Title, Content, Button, Click, Closable) : null;

    public void ClickAction(object sender, object args)
    {
        Click();
    }
}

public sealed partial class DeviceSettings : UserControl, INotifyPropertyChanged
{
    public DeviceSettings()
    {
        var pluginDir = Directory.GetParent(Assembly.GetAssembly(GetType())!.Location);

        var priFile = StorageFile.GetFileFromPathAsync(
            Path.Join(pluginDir!.FullName, "resources.pri")).GetAwaiter().GetResult();

        ResourceManager.Current.LoadPriFiles([priFile]);
        ResourceManager.Current.LoadPriFiles([priFile]);

        Application.LoadComponent(this, new Uri($"ms-appx:///{
            Path.Join(pluginDir!.FullName, "Pages", $"{GetType().Name}.xaml")}"),
            ComponentResourceLocation.Application);
    }

    public RelayDevice Device { get; set; }
    public IAmethystHost Host { get; set; }
    private Probe DiscoveryProbe { get; set; }
    private bool Mounted { get; set; }

    private string DevicePingStatus { get; set; }
    public string DeviceStatusAppendix { get; set; } = string.Empty;
    public string DeviceStatusHeader => string.IsNullOrEmpty(DevicePingStatus) ? Device.StatusSplit[0] + DeviceStatusAppendix : DevicePingStatus;
    private double SettingsOpacity => RelayReceiverEnabled ? 1.0 : 0.5;

    private bool RelayReceiverEnabled
    {
        get => Device.RelayReceiverEnabled;
        set
        {
            if (Device.RelayReceiverEnabled == value) return;
            Device.RelayReceiverEnabled = value;
            OnPropertyChanged(); // Refresh
            Device.TriggerDevicePull(); // Pull
        }
    }

    private Dictionary<string, (string Name, ITrackingDevice Device)> RelayTrackingDevices
    {
        get
        {
            if (Host is null) return []; // Completely give up for now
            return (Device.DeviceStatus is not 0
                    ? Host.PluginSettings
                        .GetSetting("CachedRemoteDevices", new List<TrackingDevice>())
                    : Device.TrackingDevices)
                .ToDictionary(x => x.DeviceGuid, x => (x.DeviceName, x as ITrackingDevice));
        }
    }

    private IEnumerable<DeviceDataTuple> DevicesList => RelayTrackingDevices
        .Select(x => new DeviceDataTuple
        {
            Host = this,
            Name = x.Value.Name,
            Guid = x.Key,
            Status = x.Value.Device?.DeviceStatus ?? -1,
            DeviceStatusString = x.Value.Device?.DeviceStatusString
        });

    public InfoBarData DiscoveryBarData { get; set; }

    public event PropertyChangedEventHandler PropertyChanged;

    public async void StartConnectionTest()
    {
        DevicePingStatus = Host.RequestLocalizedString("/Refresh/Test");
        RefreshStatusInterface(); // Refresh without triggering a host refresh

        if (Mounted)
            DispatcherQueue.TryEnqueue(() =>
            {
                ServerAddressBox.IsEnabled = false;
                ServerPortBox.IsEnabled = false;
                ConnectionsButton.IsEnabled = false;
            });

        try
        {
            var token = new CancellationTokenSource();
            token.CancelAfter(5000);

            await Task.Run(async () =>
            {
                token.Token.ThrowIfCancellationRequested();
                var ping = await Device.Service.PingService();
                DevicePingStatus = $"{Host.RequestLocalizedString("/Refresh/Ping")} {(DateTime.Now.Ticks - ping) / 10000} ms";
                RefreshStatusInterface(); // Refresh without triggering a host refresh

                Device.RelayHostname = await Device.Service.GetRemoteHostname();
                Host.PluginSettings.SetSetting("CachedRelayHostname", Device.RelayHostname);
                await Device.PullRemoteDevices(); // Finally do the thing! \^o^/ (no exceptions)
                TryStopProbe(); // Stop the probe as we've connected successfully
            }, token.Token).WaitAsync(token.Token);
        }
        catch (Exception ex)
        {
            Device.InitException = ex;
            Device.Status = RelayDeviceStatus.ConnectionError;

            TryStopProbe(); // Stop other probe instances
            DiscoveryProbe = new Probe("AmethystRelay");
            DiscoveryProbe.BeaconsUpdated += beacons =>
            {
                var host = beacons.FirstOrDefault(
                    x => int.TryParse(x.Data, out var port) && port is >= 0 and <= 65535, null);
                if (host is null) return; // Don't do anything yet
                SetDiscoveryNotice(host.Address);
            };

            DiscoveryProbe.Start();
        }

        await Task.Delay(1000); // Wait one second to show the ping
        DevicePingStatus = null; // Reset the status altogether
        RefreshStatusInterface(); // Refresh without triggering a host refresh

        if (Mounted)
            DispatcherQueue.TryEnqueue(() =>
            {
                ServerAddressBox.IsEnabled = true;
                ServerPortBox.IsEnabled = true;
                ConnectionsButton.IsEnabled = true;
            });
    }

    private void TryStopProbe()
    {
        try
        {
            SetDiscoveryNotice();
            DiscoveryProbe?.Stop();
        }
        catch (Exception e)
        {
            Host?.Log(e);
        }
    }

    private void SetDiscoveryNotice(IPEndPoint address = null)
    {
        if (Equals(DiscoveryBarData?.Address, address)) return;
        DiscoveryBarData = address is null
            ? new InfoBarData()
            : new InfoBarData
            {
                Title = Host.RequestLocalizedString("/Settings/Discovery/Header").Replace("{}", $"{address.Address}:{address.Port}"),
                Content = Host.RequestLocalizedString("/Settings/Discovery/Subtitle"),
                Button = Host.RequestLocalizedString("/Statuses/Discovery/Connect"),
                Click = () =>
                {
                    Device.ServerPort = address.Port;
                    Device.ServerIp = address.Address.ToString();
                    RefreshStatusInterface(true);
                    Device.Initialize();
                    RefreshStatusInterface(true);
                    Host.GetType().GetMethod("SetRelayInfoBarOverride")!.Invoke(Host, [null]);
                },
                Closable = true,
                IsOpen = true,
                Address = address
            };

        Host.GetType().GetMethod("SetRelayInfoBarOverride")!.Invoke(Host, [DiscoveryBarData?.AsPackedData]);
        RefreshStatusInterface(); // Refresh without triggering a host refresh
    }

    private void OnPropertyChanged(string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void RefreshStatusInterface(bool host = false)
    {
        if (Mounted)
            DispatcherQueue.TryEnqueue(() =>
            {
                OnPropertyChanged(); // Refresh own status
                //if (host) Host?.RefreshStatusInterface(); // Not needed
            });
    }

    private void PortNumberBox_OnValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (Device.ServerPort == (int)sender.Value) return;
        if (double.IsNaN(sender.Value))
        {
            sender.Value = Device.ServerPort;
            RefreshStatusInterface(true);
            return; // Don't do anything
        }

        DeviceStatusAppendix = Host
            .RequestLocalizedString("/Refresh/Apply");

        Device.ServerPort = (int)sender.Value;
        RefreshStatusInterface(true);
    }

    private void AddressTextBox_OnLostFocus(object senderR, RoutedEventArgs e)
    {
        if (senderR is not TextBox sender) return;
        if (Device.ServerIp == sender.Text) return;
        if (!IPAddress.TryParse(sender.Text, out _))
        {
            sender.Text = Device.ServerIp;
            RefreshStatusInterface(true);
            return; // Don't do anything
        }

        DeviceStatusAppendix = Host
            .RequestLocalizedString("/Refresh/Apply");

        Device.ServerIp = sender.Text;
        RefreshStatusInterface(true);
    }

    private void CopyExceptionButton_OnClick(object sender, RoutedEventArgs e)
    {
        Host?.PlayAppSound(SoundType.Invoke);
        var dataPackage = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        dataPackage.SetText(Device.DeviceStatusString +
                            (Device.InitException is not null ? $"\n\n{Device.InitException?.Message}\n\nat:\n{Device.InitException?.StackTrace}" : ""));
        Clipboard.SetContent(dataPackage);
    }

    private async void OpenDiscordButton_OnClick(object sender, RoutedEventArgs e)
    {
        Host?.PlayAppSound(SoundType.Invoke);
        await Launcher.LaunchUriAsync(new Uri("https://discord.gg/YBQCRDG"));
    }

    private void DisconnectClientButton_OnClick(object sender, RoutedEventArgs e)
    {
        AlternativeConnectionOptionsFlyout.Hide();
        Device.Shutdown();
        RefreshStatusInterface(true);
    }

    private void ReconnectClientButton_OnClick(SplitButton sender, SplitButtonClickEventArgs args)
    {
        Device.Initialize();
        RefreshStatusInterface(true);
    }

    private void AlternativeConnectionOptionsFlyout_OnOpening(object sender, object e)
    {
        Host?.PlayAppSound(SoundType.Show);
    }

    private void AlternativeConnectionOptionsFlyout_OnClosing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
    {
        Host?.PlayAppSound(SoundType.Hide);
    }

    private void DeviceSettings_OnLoaded(object sender, RoutedEventArgs e)
    {
        OnPropertyChanged();
        Mounted = true;
    }

    private void DeviceSettings_OnUnloaded(object sender, RoutedEventArgs e)
    {
        Mounted = true;
    }
}