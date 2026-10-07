using System.Security.Cryptography;
using System.Text;
using Zombies.ContentJudge.SystemOne;

namespace Zombies.ContentJudge.Judging;

/// <summary>
/// Model responses on disk, one JSON file per request under <c>artifacts/judge-cache/</c>, so an unchanged definition
/// is never sent twice. The key is a SHA-256 of the model, the state, the questions, and the image bytes: change any
/// of them and the request is sent again.
/// </summary>
public sealed class ResponseCache(string directory)
{
    public string Directory { get; } = directory;

    public static string Key(SystemOneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "model", Encoding.UTF8.GetBytes(request.Model));
        Append(hash, "state", Encoding.UTF8.GetBytes(request.State.ToJsonString()));
        foreach (var question in request.Questions)
        {
            Append(hash, "question", Encoding.UTF8.GetBytes(question.Id));
            Append(hash, "body", Encoding.UTF8.GetBytes(question.Question.ToJson().ToJsonString()));
        }

        foreach (var image in request.Images)
        {
            Append(hash, "image", Encoding.UTF8.GetBytes(image.ContentType));
            Append(hash, "bytes", image.Bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public string PathFor(string key) => Path.Combine(Directory, key + ".json");

    /// <summary>The cached response, or null on a miss. A file that cannot be read or parsed counts as a miss.</summary>
    public SystemOneResponse? TryGet(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return SystemOneResponse.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Stores a response, writing to a temporary file first so a crash never leaves half a file behind.</summary>
    public void Put(string key, SystemOneResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathFor(key);
            var temporary = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temporary, response.ToJsonString(indented: true));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written must not lose the run. Warn once, not once per judge.
            if (!_warned)
            {
                _warned = true;
                Console.Error.WriteLine($"ContentJudge: warning: could not write the response cache in '{Directory}' ({ex.GetType().Name}: {ex.Message}); continuing without caching.");
            }
        }
    }

    private bool _warned;

    private static void Append(IncrementalHash hash, string label, byte[] data)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(label));
        Span<byte> length = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(length, data.Length);
        hash.AppendData(length);
        hash.AppendData(data);
    }
}
