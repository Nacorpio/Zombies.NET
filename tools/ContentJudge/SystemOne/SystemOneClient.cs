using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Zombies.ContentJudge.SystemOne;

/// <summary>Sends a System One request and returns the parsed response. The seam judges and the runner are tested through.</summary>
public interface ISystemOneClient
{
    /// <exception cref="JudgeTransportException">The request could not be sent, the API refused it, or the response could not be read.</exception>
    Task<SystemOneResponse> AskAsync(ModelEndpoint model, SystemOneRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A failure to get a usable answer from a model: missing configuration, network, HTTP status, or an unreadable body.</summary>
public sealed class JudgeTransportException : Exception
{
    public JudgeTransportException()
    {
    }

    public JudgeTransportException(string message)
        : base(message)
    {
    }

    public JudgeTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public HttpStatusCode? StatusCode { get; init; }
}

/// <summary>
/// The one HTTP client for every System One model: Jev at TypeSafe (or OpenRouter) and Clef Flash through the
/// Cloudflare Workers AI REST endpoint. The model's <see cref="ModelEndpoint"/> decides the URL, model name, and which
/// environment variable holds the Bearer token. 429, 529, and 5xx gateway errors are retried with backoff.
/// </summary>
public sealed class SystemOneClient(HttpClient http, Func<string, string?> environment, IReadOnlyList<TimeSpan>? retryDelays = null) : ISystemOneClient
{
    public static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    private readonly IReadOnlyList<TimeSpan> _retryDelays = retryDelays ?? DefaultRetryDelays;

    public async Task<SystemOneResponse> AskAsync(ModelEndpoint model, SystemOneRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(request);
        var problems = request.Validate(model.Images);
        if (problems.Count > 0)
        {
            throw new ArgumentException($"The request is not valid: {string.Join(" ", problems)}", nameof(request));
        }

        var uri = model.ResolveEndpoint(environment);
        var key = model.ResolveApiKey(environment);
        var body = request.ToJsonString();

        for (var attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new JudgeTransportException($"Could not reach the '{model.Name}' endpoint {uri.Host}: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new JudgeTransportException($"The '{model.Name}' request timed out.", ex);
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    SystemOneResponse parsed;
                    try
                    {
                        parsed = SystemOneResponse.Parse(text);
                    }
                    catch (FormatException ex)
                    {
                        throw new JudgeTransportException($"The '{model.Name}' response could not be read: {ex.Message}", ex);
                    }

                    var mismatches = parsed.Mismatches(request);
                    return mismatches.Count == 0
                        ? parsed
                        : throw new JudgeTransportException($"The '{model.Name}' response does not answer the request: {string.Join(" ", mismatches)}");
                }

                if (IsRetryable(response.StatusCode) && attempt < _retryDelays.Count)
                {
                    await Task.Delay(_retryDelays[attempt], cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new JudgeTransportException($"The '{model.Name}' endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(text, 300)}")
                {
                    StatusCode = response.StatusCode,
                };
            }
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout || (int)status == 529;

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "…";
}
