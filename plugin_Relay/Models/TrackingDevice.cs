using System;
using System.Collections.ObjectModel;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Animation;
using Amethyst.Plugins.Contract;
using MemoryPack;
using MessagePack;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// ReSharper disable AssignNullToNotNullAttribute
// ReSharper disable NotNullOrRequiredMemberIsNotInitialized

namespace plugin_Relay.Models;

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

    [IgnoreMember] [JsonIgnore] public RelayDevice Host { get; set; } = null;
    [IgnoreMember] [JsonIgnore] public IRelayService HostService { get; set; } = null;
    [IgnoreMember] [JsonIgnore] public bool Loaded { get; set; }
    [IgnoreMember] [JsonIgnore] public Action<Exception> SetError { get; set; }
    [Key(0)] public string DeviceGuid { get; set; } = string.Empty;
    [Key(12)] public string DeviceName { get; set; }

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
        if (string.IsNullOrEmpty(DeviceGuid) || HostService is null || Host is null) return;
        try
        {
            var source = new CancellationTokenSource();
            source.CancelAfter(1000); // 1s

            Task.Run(async () =>
            {
                var joints = await HostService.GetTrackedJoints(DeviceGuid, source.Token);
                if (joints is null || joints.Count < 1)
                {
                    IsSkeletonTracked = false;
                    return; // Don't do anything
                }

                IsSkeletonTracked = true;
                if (joints.Count != TrackedJoints.Count)
                    lock (Host.Host.UpdateThreadLock)
                    {
                        Host.Host.Log("Emptying the tracked joints list...");
                        TrackedJoints.Clear(); // Delete literally everything

                        Host.Host.Log("Replacing the trackers with new ones...");
                        joints.ForEach(TrackedJoints.Add); // Add them back now
                    }
                else
                    // Since the number is the same, replace
                    for (var i = 0; i < joints.Count; i++)
                        TrackedJoints[i] = joints[i]; // Ugly, I know...
            }, source.Token).Wait(source.Token);
        }
        catch (Exception e)
        {
            SetError?.Invoke(e);
        }
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
        : Host?.Host?.RequestLocalizedString("/DeviceStatuses/Placeholder") ??
          "Remote device unavailable!\nE_NOT_INITIALIZED\nAmethyst Tracking Relay is not available, this remote device is not going to work right now. To fix this, try refreshing the Tracking Relay and checking its status.";

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

[MemoryPackable]
public readonly partial struct SerializableTrackingDevice
{
    [MemoryPackIgnore] public readonly TrackingDevice TrackingDevice;

    [MemoryPackInclude] public string DeviceGuid => TrackingDevice.DeviceGuid;
    [MemoryPackInclude] public ObservableCollection<TrackedJoint> TrackedJoints => TrackingDevice.TrackedJoints;
    [MemoryPackInclude] public bool IsInitialized => TrackingDevice.IsInitialized;
    [MemoryPackInclude] public bool IsSkeletonTracked => TrackingDevice.IsSkeletonTracked;
    [MemoryPackInclude] public bool IsPositionFilterBlockingEnabled => TrackingDevice.IsPositionFilterBlockingEnabled;
    [MemoryPackInclude] public bool IsPhysicsOverrideEnabled => TrackingDevice.IsPhysicsOverrideEnabled;
    [MemoryPackInclude] public bool IsFlipSupported => TrackingDevice.IsFlipSupported;
    [MemoryPackInclude] public bool IsAppOrientationSupported => TrackingDevice.IsAppOrientationSupported;
    [MemoryPackInclude] public bool IsSettingsDaemonSupported => TrackingDevice.IsSettingsDaemonSupported;
    [MemoryPackInclude] public int RemoteDeviceStatus => TrackingDevice.RemoteDeviceStatus;
    [MemoryPackInclude] public string RemoteDeviceStatusString => TrackingDevice.RemoteDeviceStatusString;
    [MemoryPackInclude] public Uri ErrorDocsUri => TrackingDevice.ErrorDocsUri;
    [MemoryPackInclude] public string DeviceName => TrackingDevice.DeviceName;

    [MemoryPackConstructor]
    private SerializableTrackingDevice(
        string deviceGuid,
        ObservableCollection<TrackedJoint> trackedJoints,
        bool isInitialized,
        bool isSkeletonTracked,
        bool isPositionFilterBlockingEnabled,
        bool isPhysicsOverrideEnabled,
        bool isFlipSupported,
        bool isAppOrientationSupported,
        bool isSettingsDaemonSupported,
        int remoteDeviceStatus,
        string remoteDeviceStatusString,
        Uri errorDocsUri,
        string deviceName
    )
    {
        TrackingDevice = new TrackingDevice
        {
            DeviceGuid = deviceGuid,
            TrackedJoints = trackedJoints,
            IsInitialized = isInitialized,
            IsSkeletonTracked = isSkeletonTracked,
            IsPositionFilterBlockingEnabled = isPositionFilterBlockingEnabled,
            IsPhysicsOverrideEnabled = isPhysicsOverrideEnabled,
            IsFlipSupported = isFlipSupported,
            IsAppOrientationSupported = isAppOrientationSupported,
            IsSettingsDaemonSupported = isSettingsDaemonSupported,
            RemoteDeviceStatus = remoteDeviceStatus,
            RemoteDeviceStatusString = remoteDeviceStatusString,
            ErrorDocsUri = errorDocsUri,
            DeviceName = deviceName
        };
    }

    public SerializableTrackingDevice(TrackingDevice trackingDevice)
    {
        TrackingDevice = trackingDevice;
    }
}

[MemoryPackable]
public readonly partial struct SerializableTrackedJoint
{
    [MemoryPackIgnore] public readonly TrackedJoint TrackedJoint;

    [MemoryPackInclude] public string Name => TrackedJoint.Name;
    [MemoryPackInclude] public TrackedJointType Role => TrackedJoint.Role;
    [MemoryPackInclude] public Vector3 Position => TrackedJoint.Position;
    [MemoryPackInclude] public Quaternion Orientation => TrackedJoint.Orientation;
    [MemoryPackInclude] public TrackedJointState TrackingState => TrackedJoint.TrackingState;

    [MemoryPackConstructor]
    private SerializableTrackedJoint(
        string name,
        TrackedJointType role,
        Vector3 position,
        Quaternion orientation,
        TrackedJointState trackingState
    )
    {
        TrackedJoint = new TrackedJoint
        {
            Name = name,
            Role = role,
            Position = position,
            Orientation = orientation,
            TrackingState = trackingState
        };
    }

    public SerializableTrackedJoint(TrackedJoint trackedJoint)
    {
        TrackedJoint = trackedJoint;
    }
}