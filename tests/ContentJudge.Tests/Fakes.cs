using System.Buffers.Binary;
using System.Net;
using Zombies.ContentJudge.Judging;
using Zombies.ContentJudge.SystemOne;

namespace Zombies.ContentJudge.Tests;

internal static class Fakes
{
    public static readonly ModelEndpoint Jev = new("jev", "jev-latest", "https://api.typesafe.ai/v1/systemone", "TYPESAFE_API_KEY", Images: false);

    public static readonly ModelEndpoint Clef = new(
        "clef-flash", "clef-flash", "https://api.cloudflare.com/client/v4/accounts/{CLOUDFLARE_ACCOUNT_ID}/ai/run/@cf/cloudflare/clef-flash", "CLOUDFLARE_API_TOKEN", Images: true);

    public static readonly IReadOnlyDictionary<string, ModelEndpoint> Models = new Dictionary<string, ModelEndpoint> { ["jev"] = Jev, ["clef-flash"] = Clef };

    public static Func<string, string?> Environment(params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }

    /// <summary>A PNG signature and IHDR claiming <paramref name="width"/> by <paramref name="height"/>, padded to <paramref name="length"/> bytes. Only the header is real.</summary>
    public static byte[] Png(int width = 32, int height = 32, int length = 64)
    {
        var bytes = new byte[Math.Max(length, 24)];
        byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
        header.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    public static JudgeSubject Item(string id = "base:item/canned_beans", string? jsonId = null) =>
        new(id, "item", System.Text.Json.Nodes.JsonNode.Parse($$"""{ "id": "{{jsonId ?? id}}", "mass": "400 g", "volume": "450 ml", "maxStack": 10 }""")!, "base");

    public static string NoulResponse(string id, double noul) =>
        $$"""{ "model": "jev-1.13.0", "answers": { "{{id}}": { "type": "noul", "noul": {{noul.ToString(System.Globalization.CultureInfo.InvariantCulture)}} } }, "usage": { "input_tokens": 10, "output_tokens": 2 } }""";
}

/// <summary>An HTTP handler that answers from a queue and records every request. Nothing leaves the process.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<(HttpMethod Method, Uri? Uri, string? Authorization, string Body)> Requests { get; } = [];

    public FakeHandler Respond(HttpStatusCode status, string body)
    {
        _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body) });
        return this;
    }

    public FakeHandler Throw(Exception exception)
    {
        _responses.Enqueue(() => throw exception);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri, request.Headers.Authorization?.ToString(), body));
        return _responses.Count > 0 ? _responses.Dequeue()() : throw new InvalidOperationException("No canned response left.");
    }
}

/// <summary>A client that answers from a function and counts calls.</summary>
internal sealed class FakeClient(Func<SystemOneRequest, SystemOneResponse> answer) : ISystemOneClient
{
    public int Calls { get; private set; }

    public Task<SystemOneResponse> AskAsync(ModelEndpoint model, SystemOneRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(answer(request));
    }
}
