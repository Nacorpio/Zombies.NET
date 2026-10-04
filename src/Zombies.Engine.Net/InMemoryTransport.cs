using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Zombies.Engine.Net;

/// <summary>
/// An in-process network: one Server endpoint and any number of client endpoints that exchange copies of each message.
/// Solo play and the SimHarness use it. Messages arrive at the receiver's next <see cref="ITransport.Poll"/>.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "Endpoints belong to whoever called CreateServer or Connect, and they dispose them.")]
public sealed class InMemoryNetwork
{
    private readonly Dictionary<int, Endpoint> _clients = [];
    private Endpoint? _server;
    private int _nextConnection = 1;
    private long _unreliableSent;

    /// <summary>When above zero, every Nth unreliable message is dropped, to test that snapshots survive loss.</summary>
    public int DropEveryNthUnreliable { get; set; }

    public ITransport CreateServer()
    {
        if (_server is not null)
        {
            throw new InvalidOperationException("This network already has a Server.");
        }

        _server = new Endpoint(this, ConnectionId.Server, isServer: true);
        return _server;
    }

    /// <summary>Connects a new client. Both sides see the connection at their next poll.</summary>
    public ITransport Connect()
    {
        var server = _server ?? throw new InvalidOperationException("Create the Server before connecting clients.");
        var id = new ConnectionId(_nextConnection++);
        var client = new Endpoint(this, id, isServer: false);
        _clients.Add(id.Value, client);
        server.Enqueue(EventKind.Connected, id, [], Delivery.ReliableOrdered);
        client.Enqueue(EventKind.Connected, ConnectionId.Server, [], Delivery.ReliableOrdered);
        return client;
    }

    private void Route(Endpoint from, ConnectionId to, ReadOnlySpan<byte> payload, Delivery delivery)
    {
        if (delivery == Delivery.Unreliable && DropEveryNthUnreliable > 0 && ++_unreliableSent % DropEveryNthUnreliable == 0)
        {
            return;
        }

        if (from.IsServer)
        {
            if (_clients.TryGetValue(to.Value, out var client))
            {
                client.Enqueue(EventKind.Data, ConnectionId.Server, payload, delivery);
            }
        }
        else if (_clients.ContainsKey(from.Id.Value))
        {
            _server?.Enqueue(EventKind.Data, from.Id, payload, delivery);
        }
    }

    private void Drop(ConnectionId client)
    {
        if (_clients.Remove(client.Value, out var endpoint))
        {
            endpoint.Enqueue(EventKind.Disconnected, ConnectionId.Server, [], Delivery.ReliableOrdered);
            _server?.Enqueue(EventKind.Disconnected, client, [], Delivery.ReliableOrdered);
        }
    }

    private enum EventKind : byte
    {
        Connected,
        Data,
        Disconnected,
    }

    private readonly record struct Envelope(EventKind Kind, ConnectionId From, byte[] Buffer, int Length, Delivery Delivery);

    private sealed class Endpoint(InMemoryNetwork network, ConnectionId id, bool isServer) : ITransport
    {
        private readonly Queue<Envelope> _inbox = new(64);

        public ConnectionId Id { get; } = id;

        public bool IsServer { get; } = isServer;

        public void Enqueue(EventKind kind, ConnectionId from, ReadOnlySpan<byte> payload, Delivery delivery)
        {
            var buffer = payload.IsEmpty ? [] : ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.CopyTo(buffer);
            _inbox.Enqueue(new Envelope(kind, from, buffer, payload.Length, delivery));
        }

        public void Poll(ITransportHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            // Only what is queued now: messages sent while handling arrive at the next poll, like a real network.
            for (var count = _inbox.Count; count > 0 && _inbox.TryDequeue(out var envelope); count--)
            {
                try
                {
                    switch (envelope.Kind)
                    {
                        case EventKind.Connected:
                            handler.OnConnected(envelope.From);
                            break;
                        case EventKind.Data:
                            handler.OnReceived(envelope.From, envelope.Buffer.AsSpan(0, envelope.Length), envelope.Delivery);
                            break;
                        case EventKind.Disconnected:
                            handler.OnDisconnected(envelope.From);
                            break;
                    }
                }
                finally
                {
                    if (envelope.Buffer.Length > 0)
                    {
                        ArrayPool<byte>.Shared.Return(envelope.Buffer);
                    }
                }
            }
        }

        public void Send(ConnectionId connection, ReadOnlySpan<byte> payload, Delivery delivery) => network.Route(this, connection, payload, delivery);

        public void Disconnect(ConnectionId connection) => network.Drop(IsServer ? connection : Id);

        public void Dispose()
        {
            if (!IsServer)
            {
                network.Drop(Id);
            }
        }
    }
}
