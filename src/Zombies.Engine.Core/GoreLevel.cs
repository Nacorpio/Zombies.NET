namespace Zombies.Engine.Core;

/// <summary>How much blood and gore the client draws. A visuals-only setting: it never reaches the Server and never changes what a hit does.</summary>
public enum GoreLevel
{
    Off,
    Low,
    High,
}
