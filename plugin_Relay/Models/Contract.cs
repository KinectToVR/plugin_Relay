using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Amethyst.Plugins.Contract;
using MagicOnion;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace plugin_Relay.Models;

#nullable enable

public interface IRelayClient
{
    void OnRequestShutdown(string reason = "", bool fatal = false); // Push to the client
    void OnRefreshInterface(string reason = "", bool fatal = false); // Push to the client
}

public interface IRelayService : IStreamingHub<IRelayService, IRelayClient>
{
    Task<long> PingService(); // Test service connection -> receive time
    Task<bool> RequestShutdown(string reason = "", bool fatal = false);

    public Task<string> GetRemoteHostname(); // Check remote host's name
    public Task<List<TrackingDevice>> ListTrackingDevices(); // List available devices
    public Task<TrackingDevice?> GetTrackingDevice(string guid); // Null for not found
    public Task<List<TrackedJoint>?> GetTrackedJoints(string guid); // Null if invalid

    public Task<TrackingDevice?> DeviceInitialize(string guid); // Init remote device
    public Task<TrackingDevice?> DeviceShutdown(string guid); // Shutdown remote device
    public Task<TrackingDevice?> DeviceSignalJoint(string guid, int jointId); // Joints
}

public class CustomResolver : IFormatterResolver
{
    public static readonly IFormatterResolver Instance = new CustomResolver();

    // Resolve the formatter for the ImportedClass
    public IMessagePackFormatter<T> GetFormatter<T>()
    {
        if (typeof(T) == typeof(TrackedJoint)) return (IMessagePackFormatter<T>)new TrackedJointFormatter();
        if (typeof(T) == typeof(TrackingDevice)) return (IMessagePackFormatter<T>)new TrackingDeviceFormatter();

        // Fallback to other resolvers
        return StandardResolver.Instance.GetFormatter<T>();
    }
}

public class TrackedJointFormatter : IMessagePackFormatter<TrackedJoint>
{
    public void Serialize(ref MessagePackWriter writer, TrackedJoint value, MessagePackSerializerOptions options)
    {
        writer.WriteArrayHeader(22);

        writer.Write(value.Name);
        writer.Write((int)value.Role);
        writer.Write(value.Position.X);
        writer.Write(value.Position.Y);
        writer.Write(value.Position.Z);
        writer.Write(value.Orientation.X);
        writer.Write(value.Orientation.Y);
        writer.Write(value.Orientation.Z);
        writer.Write(value.Orientation.W);
        writer.Write(value.Velocity.X);
        writer.Write(value.Velocity.Y);
        writer.Write(value.Velocity.Z);
        writer.Write(value.Acceleration.X);
        writer.Write(value.Acceleration.Y);
        writer.Write(value.Acceleration.Z);
        writer.Write(value.AngularVelocity.X);
        writer.Write(value.AngularVelocity.Y);
        writer.Write(value.AngularVelocity.Z);
        writer.Write(value.AngularAcceleration.X);
        writer.Write(value.AngularAcceleration.Y);
        writer.Write(value.AngularAcceleration.Z);
        writer.Write((int)value.TrackingState);
    }

    public TrackedJoint Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        reader.ReadArrayHeader();
        return new TrackedJoint
        {
            Name = reader.ReadString(),
            Role = (TrackedJointType)reader.ReadInt32(),
            Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            Orientation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            Velocity = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            Acceleration = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            AngularVelocity = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            AngularAcceleration = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            TrackingState = (TrackedJointState)reader.ReadInt32()
        };
    }
}

public class TrackingDeviceFormatter : IMessagePackFormatter<TrackingDevice>
{
    public void Serialize(ref MessagePackWriter writer, TrackingDevice value, MessagePackSerializerOptions options)
    {
        writer.WriteArrayHeader(12);

        writer.Write(value.DeviceGuid);
        //writer.Write(value.SessionGuid.ToString());
        writer.Write(value.DeviceName);
        writer.Write(value.RemoteDeviceStatusString);
        writer.Write(value.RemoteDeviceStatus);
        writer.Write(value.IsInitialized);
        writer.Write(value.IsSkeletonTracked);
        writer.Write(value.IsPositionFilterBlockingEnabled);
        writer.Write(value.IsPhysicsOverrideEnabled);
        writer.Write(value.IsFlipSupported);
        writer.Write(value.IsAppOrientationSupported);
        writer.Write(value.IsSettingsDaemonSupported);
        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
        writer.Write(value.ErrorDocsUri?.ToString());
    }

    public TrackingDevice Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        reader.ReadArrayHeader();
        return new TrackingDevice
        {
            DeviceGuid = reader.ReadString(),
            //SessionGuid = Guid.TryParse(reader.ReadString(), out var guid) ? guid : Guid.NewGuid(),
            DeviceName = reader.ReadString(),
            RemoteDeviceStatusString = reader.ReadString(),
            RemoteDeviceStatus = reader.ReadInt32(),
            IsInitialized = reader.ReadBoolean(),
            IsSkeletonTracked = reader.ReadBoolean(),
            IsPositionFilterBlockingEnabled = reader.ReadBoolean(),
            IsPhysicsOverrideEnabled = reader.ReadBoolean(),
            IsFlipSupported = reader.ReadBoolean(),
            IsAppOrientationSupported = reader.ReadBoolean(),
            IsSettingsDaemonSupported = reader.ReadBoolean(),
            // ReSharper disable once AssignNullToNotNullAttribute
            ErrorDocsUri = Uri.TryCreate(reader.ReadString(), UriKind.RelativeOrAbsolute, out var uri) ? uri : null
        };
    }
}