using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace plugin_Relay.Beacon;

public class Beacon : IDisposable
{
    internal const int DiscoveryPort = 35891;
    private readonly UdpClient _udp;

    public Beacon(string beaconType, ushort advertisedPort)
    {
        BeaconType = beaconType;
        AdvertisedPort = advertisedPort;
        BeaconData = "";

        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

        try
        {
            _udp.AllowNatTraversal(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error switching on NAT traversal: " + ex.Message);
        }
    }

    public string BeaconType { get; }
    public ushort AdvertisedPort { get; }
    public bool Stopped { get; private set; }

    public string BeaconData { get; set; }

    public void Dispose()
    {
        Stop();
    }

    public void Start()
    {
        Stopped = false;
        _udp.BeginReceive(ProbeReceived, null);
    }

    public void Stop()
    {
        Stopped = true;
    }

    private void ProbeReceived(IAsyncResult ar)
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        var bytes = _udp.EndReceive(ar, ref remote);

        // Compare beacon type to probe type
        var typeBytes = Encode(BeaconType);
        if (HasPrefix(bytes, typeBytes))
        {
            // If true, respond again with our type, port and payload
            var responseData = Encode(BeaconType)
                .Concat(BitConverter.GetBytes((ushort)IPAddress.HostToNetworkOrder((short)AdvertisedPort)))
                .Concat(Encode(BeaconData)).ToArray();
            _udp.Send(responseData, responseData.Length, remote);
        }

        if (!Stopped) _udp.BeginReceive(ProbeReceived, null);
    }

    internal static bool HasPrefix<T>(IEnumerable<T> haystack, IEnumerable<T> prefix)
    {
        var enumerable = haystack.ToList();
        var second = prefix.ToList();

        return enumerable.Count >= second.Count &&
               enumerable.Zip(second, (a, b) => a.Equals(b)).All(x => x);
    }

    /// <summary>
    ///     Convert a string to network bytes
    /// </summary>
    internal static IEnumerable<byte> Encode(string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        var len = IPAddress.HostToNetworkOrder((short)bytes.Length);

        return BitConverter.GetBytes(len).Concat(bytes);
    }

    /// <summary>
    ///     Convert network bytes to a string
    /// </summary>
    internal static string Decode(IEnumerable<byte> data)
    {
        var listData = data as IList<byte> ?? data.ToList();

        var len = IPAddress.NetworkToHostOrder(BitConverter.ToInt16(listData.Take(2).ToArray(), 0));
        if (listData.Count < 2 + len) throw new ArgumentException("Too few bytes in packet");

        return Encoding.UTF8.GetString(listData.Skip(2).Take(len).ToArray());
    }
}