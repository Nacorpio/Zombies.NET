using Zombies.Engine.Core;

namespace Zombies.Engine.Net;

/// <summary>
/// Solo play: the same <see cref="GameServer"/> a dedicated server runs, in-process over the in-memory transport,
/// with the local <see cref="GameClient"/> joined to it. Call <see cref="Advance"/> once per frame.
/// </summary>
public sealed class EmbeddedServer : IDisposable
{
    private readonly ITransport _serverTransport;
    private readonly ITransport _clientTransport;
    private readonly FixedStepClock _clock = new();
    private readonly List<ITickable> _extra = [];
    private long _tick;

    /// <summary>Starts the Server and joins it. Throws when the join is refused, which only a bug can cause here.</summary>
    public EmbeddedServer(ServerOptions options, string playerName)
    {
        ArgumentNullException.ThrowIfNull(options);
        var network = new InMemoryNetwork();
        _serverTransport = network.CreateServer();
        Server = new GameServer(_serverTransport, options);
        _clientTransport = network.Connect();
        Client = new GameClient(_clientTransport, options.Identity, playerName);

        for (var i = 0; i < 4 && Client.State == ClientState.Connecting; i++)
        {
            Step();
        }

        if (Client.State != ClientState.Joined)
        {
            throw new InvalidOperationException($"Could not join the embedded Server: {Client.Refusal} {Client.RefusalDetail}");
        }
    }

    public GameServer Server { get; }

    public GameClient Client { get; }

    /// <summary>
    /// Adds a system the Server ticks alongside itself, such as the world's zombies and their AI. Add it before the first
    /// <see cref="Advance"/>; a system added later starts from the tick it was added.
    /// </summary>
    public void Add(ITickable tickable)
    {
        ArgumentNullException.ThrowIfNull(tickable);
        _extra.Add(tickable);
    }

    /// <summary>Runs the Server ticks that <paramref name="elapsed"/> real time owes, then lets the client read what they sent.</summary>
    public void Advance(TimeSpan elapsed)
    {
        for (var ticks = _clock.Advance(elapsed); ticks > 0; ticks--)
        {
            Step();
        }
    }

    public void Dispose()
    {
        _clientTransport.Dispose();
        _serverTransport.Dispose();
    }

    private void Step()
    {
        Server.Tick(_tick);
        for (var i = 0; i < _extra.Count; i++)
        {
            _extra[i].Tick(_tick);
        }

        _tick++;
        Client.Poll();
    }
}
