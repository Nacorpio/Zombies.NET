using System.Net;
using System.Text.Json.Nodes;
using Zombies.ContentJudge.Judging;
using Zombies.ContentJudge.SystemOne;

namespace Zombies.ContentJudge.Tests;

public sealed class ClientTests
{
    private static readonly Func<string, string?> Keys = Fakes.Environment(
        ("TYPESAFE_API_KEY", "test-typesafe-key"), ("CLOUDFLARE_API_TOKEN", "test-cloudflare-token"), ("CLOUDFLARE_ACCOUNT_ID", "acct123"));

    private static SystemOneRequest NoulRequest(string model = "jev-latest") =>
        new(model, "Help! My payouts have been failing for 3 days.", [new NamedQuestion("is_urgent", new NoulQuestion("Does this convey urgency?"))]);

    private static (SystemOneClient Client, FakeHandler Handler, HttpClient Http) Create(Func<string, string?>? environment = null)
    {
        var handler = new FakeHandler();
        var http = new HttpClient(handler);
        return (new SystemOneClient(http, environment ?? Keys, [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]), handler, http);
    }

    [Fact]
    public async Task Jev_PostsTheBodyWithBearerTokenToTypeSafe()
    {
        var (client, handler, http) = Create();
        using var _ = http;
        handler.Respond(HttpStatusCode.OK, Samples.NoulResponse);

        var response = await client.AskAsync(Fakes.Jev, NoulRequest(), TestContext.Current.CancellationToken);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", sent.Uri!.ToString());
        Assert.Equal("Bearer test-typesafe-key", sent.Authorization);
        Assert.True(JsonNode.DeepEquals(NoulRequest().ToJson(), JsonNode.Parse(sent.Body)));
        Assert.Equal(0.95, Assert.IsType<NoulAnswer>(response.Answers["is_urgent"]).Noul);
    }

    [Fact]
    public async Task Clef_UsesTheWorkersAiEndpointAndUnwrapsTheEnvelope()
    {
        var (client, handler, http) = Create();
        using var _ = http;
        handler.Respond(HttpStatusCode.OK, """{ "result": { "model": "clef-flash", "answers": { "is_urgent": { "type": "noul", "noul": 0.7 } } }, "success": true, "errors": [], "messages": [] }""");

        var response = await client.AskAsync(Fakes.Clef, NoulRequest("clef-flash"), TestContext.Current.CancellationToken);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://api.cloudflare.com/client/v4/accounts/acct123/ai/run/@cf/cloudflare/clef-flash", sent.Uri!.ToString());
        Assert.Equal("Bearer test-cloudflare-token", sent.Authorization);
        Assert.Equal(0.7, Assert.IsType<NoulAnswer>(response.Answers["is_urgent"]).Noul);
    }

    [Fact]
    public async Task MissingKey_FailsBeforeAnyRequest()
    {
        var (client, handler, http) = Create(Fakes.Environment());
        using var _ = http;

        var ex = await Assert.ThrowsAsync<JudgeTransportException>(() => client.AskAsync(Fakes.Jev, NoulRequest(), TestContext.Current.CancellationToken));

        Assert.Contains("TYPESAFE_API_KEY is not set", ex.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MissingAccountId_FailsBeforeAnyRequest()
    {
        var (client, handler, http) = Create(Fakes.Environment(("CLOUDFLARE_API_TOKEN", "t")));
        using var _ = http;

        await Assert.ThrowsAsync<JudgeTransportException>(() => client.AskAsync(Fakes.Clef, NoulRequest("clef-flash"), TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData((HttpStatusCode)529)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RateLimitAndOverload_AreRetried(HttpStatusCode status)
    {
        var (client, handler, http) = Create();
        using var _ = http;
        handler.Respond(status, "{}").Respond(HttpStatusCode.OK, Samples.NoulResponse);

        await client.AskAsync(Fakes.Jev, NoulRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Retries_GiveUpAfterTheLastDelay()
    {
        var (client, handler, http) = Create();
        using var _ = http;
        for (var i = 0; i < 4; i++)
        {
            handler.Respond(HttpStatusCode.TooManyRequests, "slow down");
        }

        var ex = await Assert.ThrowsAsync<JudgeTransportException>(() => client.AskAsync(Fakes.Jev, NoulRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ErrorStatus_IsATransportErrorWithoutRetry(HttpStatusCode status)
    {
        var (client, handler, http) = Create();
        using var _ = http;
        handler.Respond(status, """{ "detail": "nope" }""");

        var ex = await Assert.ThrowsAsync<JudgeTransportException>(() => client.AskAsync(Fakes.Jev, NoulRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(status, ex.StatusCode);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("test-typesafe-key", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<html>bad gateway</html>")]
    [InlineData("""{ "model": "jev", "answers": {} }""")]
    [InlineData("""{ "model": "jev", "answers": { "is_urgent": { "type": "score", "score": 1, "legend": {}, "probabilities": {}, "confidence": 1 } } }""")]
    public async Task UnusableBody_IsATransportError(string body)
    {
        var (client, handler, http) = Create();
        using var _ = http;
        handler.Respond(HttpStatusCode.OK, body);

        await Assert.ThrowsAsync<JudgeTransportException>(() => client.AskAsync(Fakes.Jev, NoulRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NetworkFailure_IsATransportError()
    {
        var (client, handler, http) = Create();
        using var _ = http;
        handler.Throw(new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<JudgeTransportException>(() => client.AskAsync(Fakes.Jev, NoulRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidRequest_IsNeverSent()
    {
        var (client, handler, http) = Create();
        using var _ = http;
        var request = new SystemOneRequest("jev-latest", "s", [new NamedQuestion("bad id", new NoulQuestion("x"))]);

        await Assert.ThrowsAsync<ArgumentException>(() => client.AskAsync(Fakes.Jev, request, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Runner_TransportErrorBecomesAnErrorResultAndExitCode3()
    {
        var (client, handler, http) = Create();
        using var _ = http;
        handler.Respond(HttpStatusCode.InternalServerError, "boom");
        var runner = new JudgeRunner(client, Fakes.Models, Thresholds.Empty, cache: null);

        var results = await runner.RunAsync([new DefinitionJudge()], [Fakes.Item()], TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(results).IsError);
        Assert.Equal(JudgeExitCode.TransportError, JudgeExitCode.For(results));
    }

    [Fact]
    public async Task Runner_DeterministicFailSkipsTheModel()
    {
        var client = new FakeClient(_ => throw new InvalidOperationException("should not be called"));
        var runner = new JudgeRunner(client, Fakes.Models, Thresholds.Empty, cache: null);

        var results = await runner.RunAsync([new DefinitionJudge()], [Fakes.Item(jsonId: "base:item/wrong")], TestContext.Current.CancellationToken);

        Assert.Equal(Verdict.Fail, Assert.Single(results).Verdict);
        Assert.Equal(0, client.Calls);
        Assert.Equal(JudgeExitCode.DeterministicFail, JudgeExitCode.For(results));
    }

    [Fact]
    public void Models_ParseCheckedInFileAndApplyEnvironmentOverrides()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "models.json"));

        var defaults = ModelEndpoint.Parse(json, Fakes.Environment());
        Assert.Equal("https://api.typesafe.ai/v1/systemone", defaults["jev"].Endpoint);
        Assert.Equal("TYPESAFE_API_KEY", defaults["jev"].ApiKeyVariable);
        Assert.False(defaults["jev"].Images);
        Assert.Equal("CLOUDFLARE_API_TOKEN", defaults["clef-flash"].ApiKeyVariable);
        Assert.True(defaults["clef-flash"].Images);

        var openRouter = ModelEndpoint.Parse(json, Fakes.Environment(
            ("CONTENTJUDGE_JEV_ENDPOINT", "https://openrouter.ai/api/v1/systemone"),
            ("CONTENTJUDGE_JEV_MODEL", "typesafe/jev-1.13"),
            ("CONTENTJUDGE_JEV_API_KEY_VARIABLE", "OPENROUTER_API_KEY")));
        Assert.Equal(new ModelEndpoint("jev", "typesafe/jev-1.13", "https://openrouter.ai/api/v1/systemone", "OPENROUTER_API_KEY", false), openRouter["jev"]);
    }

    [Fact]
    public void Models_FileWithUnknownFieldIsRejected() =>
        Assert.Throws<FormatException>(() => ModelEndpoint.Parse("""{ "jev": { "model": "m", "endpoint": "https://x", "apiKeyVariable": "K", "images": false, "apiKey": "secret" } }""", Fakes.Environment()));
}
