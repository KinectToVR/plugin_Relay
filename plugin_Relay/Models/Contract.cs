using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Amethyst.Plugins.Contract;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Stl.Rpc;

namespace plugin_Relay.Models;

#nullable enable

public interface IRelayClient : IRpcService
{
    Task OnRequestShutdown(string reason = "", bool fatal = false, CancellationToken cancellationToken = default); // Push to the client
    Task OnRefreshInterface(CancellationToken cancellationToken = default); // Push to the client
}

public interface IRelayService : IRpcService
{
    Task<long> PingService(CancellationToken cancellationToken = default); // Test service connection -> receive time
    Task<bool> RequestShutdown(string reason = "", bool fatal = false, CancellationToken cancellationToken = default);

    Task<string> GetRemoteHostname(CancellationToken cancellationToken = default); // Check remote host's name
    Task<List<TrackingDevice>> ListTrackingDevices(CancellationToken cancellationToken = default); // List available devices
    Task<TrackingDevice?> GetTrackingDevice(string guid, CancellationToken cancellationToken = default); // Null for not found
    Task<List<TrackedJoint>?> GetTrackedJoints(string guid, CancellationToken cancellationToken = default); // Null if invalid

    Task<TrackingDevice?> DeviceInitialize(string guid, CancellationToken cancellationToken = default); // Init remote device
    Task<TrackingDevice?> DeviceShutdown(string guid, CancellationToken cancellationToken = default); // Shutdown remote device
    Task<TrackingDevice?> DeviceSignalJoint(string guid, int jointId, CancellationToken cancellationToken = default); // Joints
}

#nullable disable

public class TrackedJointFormatter : MemoryPackFormatter<TrackedJoint>
{
    // Unity does not support scoped and TBufferWriter so change signature to `Serialize(ref MemoryPackWriter writer, ref AnimationCurve value)`
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref TrackedJoint value)
    {
        if (value == null)
        {
            writer.WriteNullObjectHeader();
            return;
        }
        
        writer.WritePackable(new SerializableTrackedJoint(value));
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref TrackedJoint value)
    {
        if (reader.PeekIsNull())
        {
            reader.Advance(1); // Skip null block
            value = null;
            return;
        }

        var wrapped = reader.ReadPackable<SerializableTrackedJoint>();
        value = wrapped.TrackedJoint;
    }
}

public class TrackingDeviceFormatter : MemoryPackFormatter<TrackingDevice>
{
    // Unity does not support scoped and TBufferWriter so change signature to `Serialize(ref MemoryPackWriter writer, ref AnimationCurve value)`
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref TrackingDevice value)
    {
        if (value == null)
        {
            writer.WriteNullObjectHeader();
            return;
        }

        writer.WritePackable(new SerializableTrackingDevice(value));
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref TrackingDevice value)
    {
        if (reader.PeekIsNull())
        {
            reader.Advance(1); // Skip null block
            value = null;
            return;
        }

        var wrapped = reader.ReadPackable<SerializableTrackingDevice>();
        value = wrapped.TrackingDevice;
    }
}
