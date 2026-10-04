using System.Net;
using System.Net.Sockets;
using LiteNetLib;

namespace Zombies.Engine.Net;

/// <summary>
/// UDP transport on LiteNetLib for 2 to 4 players (ADR 0003). Events are queued by LiteNetLib's socket thread and
/// delivered on the simulation thread during <see cref="Poll"/>.
/// </summary>
public sealed class LiteNetTransport : ITransport, INetEventListener
{
    private readonly NetManager _manager;
    private readonly Dictionary<int, NetPeer> _peers = [];
    private readonly bool _isServer;
    private readonly string _connectionKey;
    private readonly int _maxConnections;
    private ITransportHandler? _handler;

    private LiteNetTransport(bool isServer, string connectionKey, int maxConnections)
    {
        _isServer = isServer;
        _connectionKey = connectionKey;
        _maxConnections = maxConnections;
        _manager = new NetManager(this) { AutoRecycle = true };
    }

    /// <summary>The UDP port the transport is bound to; useful when listening on port 0.</summary>
    public int LocalPort => _manager.LocalPort;

    /// <summary>Starts a Server listening on <paramref name="port"/> (0 picks a free port).</summary>
    public static LiteNetTransport Listen(int port, string connectionKey, int maxConnections)
    {
        var transport = new LiteNetTransport(isServer: true, connectionKey, maxConnections);
        if (!transport._manager.Start(port))
        {
            throw new SocketException((int)SocketError.AddressAlreadyInUse);
        }

        return transport;
    }

    /// <summary>Starts connecting to a Server. The handler sees <see cref="ConnectionId.Server"/> connect once the handshake completes.</summary>
    public static LiteNetTransport Connect(string host, int port, string connectionKey)
    {
        var transport = new LiteNetTransport(isServer: false, connectionKey, maxConnections: 1);
        transport._manager.Start();
        transport._manager.Connect(host, port, connectionKey);
        return transport;
    }

    public void Poll(ITransportHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        try
        {
            _manager.PollEvents();
        }
        finally
        {
            _handler = null;
        }
    }

    public void Send(ConnectionId connection, ReadOnlySpan<byte> payload, Delivery delivery)
    {
        if (_peers.TryGetValue(connection.Value, out var peer))
        {
            peer.Send(payload, delivery == Delivery.ReliableOrdered ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }
    }

    public void Disconnect(ConnectionId connection)
    {
        if (_peers.TryGetValue(connection.Value, out var peer))
        {
            peer.Disconnect();
        }
    }

    public void Dispose() => _manager.Stop();

    void INetEventListener.OnConnectionRequest(ConnectionRequest request)
    {
        if (_isServer && _manager.ConnectedPeersCount < _maxConnections)
        {
            request.AcceptIfKey(_connectionKey);
        }
        else
        {
            request.Reject();
        }
    }

    void INetEventListener.OnPeerConnected(NetPeer peer)
    {
        var id = IdOf(peer);
        _peers[id.Value] = peer;
        _handler?.OnConnected(id);
    }

    void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        var id = IdOf(peer);
        if (_peers.Remove(id.Value))
        {
            _handler?.OnDisconnected(id);
        }
    }

    void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        var delivery = deliveryMethod == DeliveryMethod.Unreliable ? Delivery.Unreliable : Delivery.ReliableOrdered;
        _handler?.OnReceived(IdOf(peer), reader.GetRemainingBytesSpan(), delivery);
    }

    void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
    }

    void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
    }

    void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }

    // Client side, the one peer is the Server. Server side, peer ids start at 0, so shift them past ConnectionId.Server.
    private ConnectionId IdOf(NetPeer peer) => _isServer ? new ConnectionId(peer.Id + 1) : ConnectionId.Server;
}
