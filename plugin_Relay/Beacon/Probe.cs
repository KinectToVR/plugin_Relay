using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace plugin_Relay.Beacon;

public class Probe : IDisposable
{
    /// <summary>
    ///     Remove beacons older than this
    /// </summary>
    private static readonly TimeSpan BeaconTimeout = new(0, 0, 0, 5); // seconds

    private readonly Thread _thread;
    private readonly UdpClient _udp = new();
    private readonly EventWaitHandle _waitHandle = new(false, EventResetMode.AutoReset);
    private IEnumerable<BeaconLocation> _currentBeacons = Enumerable.Empty<BeaconLocation>();

    private bool _running = true;

    public Probe(string beaconType)
    {
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        BeaconType = beaconType;
        _thread = new Thread(BackgroundLoop) { IsBackground = true };
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        try
        {
            _udp.AllowNatTraversal(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error switching on NAT traversal: " + ex.Message);
        }

        _udp.BeginReceive(ResponseReceived, null);
    }

    public string BeaconType { get; }

    public void Dispose()
    {
        try
        {
            Stop();
            GC.SuppressFinalize(this);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public event Action<IEnumerable<BeaconLocation>> BeaconsUpdated;

    public void Start()
    {
        _thread.Start();
    }

    private void ResponseReceived(IAsyncResult ar)
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        var bytes = _udp.EndReceive(ar, ref remote);

        var typeBytes = Beacon.Encode(BeaconType).ToList();
        Debug.WriteLine(string.Join(", ", typeBytes.Select(b => (char)b)));
        if (Beacon.HasPrefix(bytes, typeBytes))
            try
            {
                var portBytes = bytes.Skip(typeBytes.Count).Take(2).ToArray();
                var port = (ushort)IPAddress.NetworkToHostOrder((short)BitConverter.ToUInt16(portBytes, 0));
                var payload = Beacon.Decode(bytes.Skip(typeBytes.Count + 2));
                NewBeacon(new BeaconLocation(new IPEndPoint(remote!.Address, port), payload, DateTime.Now));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

        _udp.BeginReceive(ResponseReceived, null);
    }

    private void BackgroundLoop()
    {
        while (_running)
        {
            try
            {
                BroadcastProbe();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            _waitHandle.WaitOne(2000);
            PruneBeacons();
        }
    }

    private void BroadcastProbe()
    {
        var probe = Beacon.Encode(BeaconType).ToArray();
        _udp.Send(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, Beacon.DiscoveryPort));
    }

    private void PruneBeacons()
    {
        var cutOff = DateTime.Now - BeaconTimeout;
        var oldBeacons = _currentBeacons.ToList();
        var newBeacons = oldBeacons.Where(l => l.LastAdvertised >= cutOff).ToList();
        if (EnumsEqual(oldBeacons, newBeacons)) return;

        var u = BeaconsUpdated;
        u?.Invoke(newBeacons);
        _currentBeacons = newBeacons;
    }

    private void NewBeacon(BeaconLocation newBeacon)
    {
        var newBeacons = _currentBeacons
            .Where(l => !l.Equals(newBeacon))
            .Concat(new[] { newBeacon })
            .OrderBy(l => l.Data)
            .ThenBy(l => l.Address, IpEndPointComparer.Instance)
            .ToList();
        var u = BeaconsUpdated;
        u?.Invoke(newBeacons);
        _currentBeacons = newBeacons;
    }

    private static bool EnumsEqual<T>(IEnumerable<T> xs, IEnumerable<T> ys)
    {
        var enumerable = xs.ToList();
        return enumerable.Zip(ys, (x, y) => x.Equals(y)).Count() == enumerable.Count;
    }

    public void Stop()
    {
        _running = false;
        _waitHandle.Set();
        _thread.Join();
    }
}

public class BeaconLocation(IPEndPoint address, string data, DateTime lastAdvertised)
{
    public IPEndPoint Address { get; } = address;
    public string Data { get; } = data;
    public DateTime LastAdvertised { get; } = lastAdvertised;

    public override string ToString()
    {
        return Data;
    }

    protected bool Equals(BeaconLocation other)
    {
        return Equals(Address, other.Address);
    }

    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        return obj.GetType() == GetType() && Equals((BeaconLocation)obj);
    }

    public override int GetHashCode()
    {
        return Address != null ? Address.GetHashCode() : 0;
    }
}

internal class IpEndPointComparer : IComparer<IPEndPoint>
{
    public static readonly IpEndPointComparer Instance = new();

    public int Compare(IPEndPoint x, IPEndPoint y)
    {
        if (x is null && y is null) return 0;
        var c = string.Compare(x!.Address.ToString(), y!.Address.ToString(), StringComparison.Ordinal);
        return c != 0 ? c : y.Port - x.Port;
    }
}