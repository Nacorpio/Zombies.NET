namespace Zombies.Engine.Core.Debugging;

/// <summary>
/// What the badge knows right now: the latest good session (if any) and a short warning when something is wrong with the source.
/// A bad update never discards a good session, so a half-written file does not make the badge flicker away.
/// </summary>
public sealed record SessionState(DebugSession? Session, string? Warning, double FirstSeenSeconds)
{
    public static readonly SessionState None = new(null, null, 0);

    /// <summary>Seconds into the session. Uses the session's own start time when it states one, and otherwise the moment it first appeared.</summary>
    public double ElapsedSeconds(double nowSeconds, DateTimeOffset utcNow) =>
        Session?.StartedAt is { } started
            ? Math.Max(0, (utcNow - started).TotalSeconds)
            : Math.Max(0, nowSeconds - FirstSeenSeconds);
}

/// <summary>
/// Watches a JSON file that describes the current <see cref="DebugSession"/>. Whoever drives the game updates the file as work proceeds,
/// and the badge follows. Reading is rate limited, and a file that is missing, busy, or malformed is reported as a warning.
/// </summary>
public sealed class FileSessionSource(string path, double pollSeconds = 0.25)
{
    private double _nextPoll;
    private DateTime _lastWrite = DateTime.MinValue;
    private long _lastLength = -1;
    private SessionState _state = SessionState.None;

    /// <summary>How many times the file was actually read, which tests use to prove it is not re-read needlessly.</summary>
    public int ReadCount { get; private set; }

    public string Path => path;

    /// <summary>Returns the current state, re-reading the file when it changed and the poll interval has passed.</summary>
    public SessionState Poll(double nowSeconds)
    {
        if (nowSeconds < _nextPoll)
        {
            return _state;
        }

        _nextPoll = nowSeconds + pollSeconds;

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            _lastWrite = DateTime.MinValue;
            _lastLength = -1;
            return _state = _state with { Warning = "SESSION FILE NOT FOUND" };
        }

        if (info.LastWriteTimeUtc == _lastWrite && info.Length == _lastLength)
        {
            return _state;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            // Probably being written right now. Try again on the next poll without calling it a problem.
            return _state;
        }

        _lastWrite = info.LastWriteTimeUtc;
        _lastLength = info.Length;
        ReadCount++;

        if (!SessionJson.TryParse(text, out var session, out var error))
        {
            return _state = _state with { Warning = "SESSION FILE: " + error.ToUpperInvariant() };
        }

        var firstSeen = _state.Session is null ? nowSeconds : _state.FirstSeenSeconds;
        return _state = new SessionState(session, null, firstSeen);
    }
}
