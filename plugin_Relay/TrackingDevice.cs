using System;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Amethyst.Plugins.Contract;
using MessagePack;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// ReSharper disable AssignNullToNotNullAttribute
// ReSharper disable NotNullOrRequiredMemberIsNotInitialized

namespace plugin_Relay;

public class TrackingDevice : ITrackingDevice
{
    public TrackingDevice()
    {
    }

    public TrackingDevice(ITrackingDevice device)
    {
        UpdateFrom(device);
    }

    private void UpdateFrom(ITrackingDevice device)
    {
        if (device is null) return; // Don't continue
        TrackedJoints = device.TrackedJoints;
        IsInitialized = device.IsInitialized;
        IsSkeletonTracked = device.IsSkeletonTracked;
        IsPositionFilterBlockingEnabled = device.IsPositionFilterBlockingEnabled;
        IsPhysicsOverrideEnabled = device.IsPhysicsOverrideEnabled;
        IsFlipSupported = device.IsFlipSupported;
        IsAppOrientationSupported = device.IsAppOrientationSupported;
        IsSettingsDaemonSupported = device.IsSettingsDaemonSupported;
        RemoteDeviceStatus = (device as TrackingDevice)?.RemoteDeviceStatus ?? device.DeviceStatus;
        RemoteDeviceStatusString = (device as TrackingDevice)?.RemoteDeviceStatusString ?? device.DeviceStatusString;
        ErrorDocsUri = device.ErrorDocsUri;
    }

    [IgnoreMember] [JsonIgnore] public IRelayService HostService { get; set; } = null;
    [IgnoreMember] [JsonIgnore] public bool Loaded { get; set; }
    [IgnoreMember] [JsonIgnore] public Action<Exception> SetError { get; set; }
    [Key(0)] public string DeviceGuid { get; set; } = string.Empty;
    [Key(12)] public string DeviceName { get; set; }
    //[Key(13)] public Guid SessionGuid { get; set; }

    public void Initialize()
    {
        if (string.IsNullOrEmpty(DeviceGuid) || HostService is null) return;
        try
        {
            UpdateFrom(HostService.DeviceInitialize(DeviceGuid).GetAwaiter().GetResult()); // Call remote
        }
        catch (Exception e)
        {
            SetError?.Invoke(e);
        }
    }

    public void Shutdown()
    {
        if (string.IsNullOrEmpty(DeviceGuid) || HostService is null) return;
        try
        {
            UpdateFrom(HostService.DeviceShutdown(DeviceGuid).GetAwaiter().GetResult()); // Call remote
        }
        catch (Exception e)
        {
            SetError?.Invoke(e);
        }
    }

    public void SignalJoint(int jointId)
    {
        if (string.IsNullOrEmpty(DeviceGuid) || HostService is null) return;
        try
        {
            HostService.DeviceSignalJoint(DeviceGuid, jointId); // Call remote
        }
        catch (Exception e)
        {
            SetError?.Invoke(e);
        }
    }

    public void OnLoad()
    {
        Loaded = true;
    }

    public void Update()
    {
        // TODO pull tracked joints from the server
    }

    [Key(1)] public ObservableCollection<TrackedJoint> TrackedJoints { get; set; } = [];
    [Key(2)] public bool IsInitialized { get; set; }
    [Key(3)] public bool IsSkeletonTracked { get; set; }
    [Key(4)] public bool IsPositionFilterBlockingEnabled { get; set; }
    [Key(5)] public bool IsPhysicsOverrideEnabled { get; set; }
    [Key(6)] public bool IsFlipSupported { get; set; }
    [Key(7)] public bool IsAppOrientationSupported { get; set; }
    [Key(8)] public bool IsSettingsDaemonSupported { get; set; }
    [Key(9)] public int RemoteDeviceStatus { get; set; }
    [Key(10)] public string RemoteDeviceStatusString { get; set; } = string.Empty;
    [Key(11)] public Uri ErrorDocsUri { get; set; }

    [IgnoreMember] [JsonIgnore] public bool IsSelfUpdateEnabled => false;
    [IgnoreMember] [JsonIgnore] public int DeviceStatus => HostService is not null ? RemoteDeviceStatus : -1;

    [IgnoreMember]
    [JsonIgnore]
    public string DeviceStatusString => HostService is not null
        ? RemoteDeviceStatusString
        : "Remote device unavailable!\nE_NOT_INITIALIZED\nAmethyst Tracking Relay " +
          "is not available, this remote device is not going to work right now. " +
          "To fix this, try refreshing the Tracking Relay and checking its status."; // TODO LOCALIZE

    [IgnoreMember]
    [JsonIgnore]
    public string[] DeviceStatusStringSplit =>
        RemoteDeviceStatusString.Split('\n').Length is 3 ? RemoteDeviceStatusString.Split('\n') : ["Unknown", "S_UNKNWN", "Status unavailable."];


    [IgnoreMember]
    [JsonIgnore]
    public object SettingsInterfaceRoot => IsSettingsDaemonSupported && Loaded
        ? new Page
        {
            Content = new TextBlock
            {
                Text = "Forwarding UI elements is not supported. Head to the Amethyst Tracking Relay server instance and change your settings there, instead.",
                TextWrapping = TextWrapping.WrapWholeWords
            }
        }
        : null;
}