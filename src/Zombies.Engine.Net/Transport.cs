namespace Zombies.Engine.Net;

/// <summary>One peer as a transport sees it. A client always sees the Server as <see cref="Server"/>.</summary>
public readonly record struct ConnectionId(int Value)
{
    public static ConnectionId Server => new(0);
}

/// <summary>How a message travels: Domain commands and their answers are reliable and ordered; snapshots are unreliable.</summary>
public enum Delivery : byte
{
    ReliableOrdered,
    Unreliable,
}

/// <summary>Receives what a transport delivers during <see cref="ITransport.Poll"/>. The payload span is only valid during the call.</summary>
public interface ITransportHandler
{
    void OnConnected(ConnectionId connection);

    void OnReceived(ConnectionId connection, ReadOnlySpan<byte> payload, Delivery delivery);

    void OnDisconnected(ConnectionId connection);
}

/// <summary>
/// Moves bytes between the Server and its clients (ADR 0003). The same Server runs over the in-memory transport for solo play
/// and tests, and over LiteNetLib for multiplayer. Every call happens on the simulation thread.
/// </summary>
public interface ITransport : IDisposable
{
    /// <summary>Delivers everything that arrived since the last poll to <paramref name="handler"/>, in arrival order.</summary>
    void Poll(ITransportHandler handler);

    void Send(ConnectionId connection, ReadOnlySpan<byte> payload, Delivery delivery);

    void Disconnect(ConnectionId connection);
}
