using Zombies.ContentJudge.SystemOne;

namespace Zombies.ContentJudge.Judging;

/// <summary>What one judge decided about one subject. <see cref="Error"/> is set when no usable answer came back.</summary>
public sealed record JudgeResult(string Judge, string Subject, string Model, Verdict Verdict, IReadOnlyList<Finding> Findings, string? Error = null, bool Cached = false)
{
    public bool IsError => Error is not null;
}

/// <summary>Process exit codes for a judging run.</summary>
public static class JudgeExitCode
{
    public const int Pass = 0;
    public const int DeterministicFail = 1;
    public const int ReviewNeeded = 2;
    public const int TransportError = 3;

    /// <summary>A usage or input error, such as a mods folder that cannot be read. Not a judging outcome.</summary>
    public const int UsageError = 64;

    /// <summary>
    /// 3 if any result could not be judged (the run is incomplete), else 1 if a deterministic check failed, else 2 if
    /// anything needs review, else 0.
    /// </summary>
    public static int For(IEnumerable<JudgeResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var list = results.ToList();
        if (list.Any(r => r.IsError))
        {
            return TransportError;
        }

        return list.Select(r => r.Verdict).DefaultIfEmpty(Verdict.Pass).Max() switch
        {
            Verdict.Fail => DeterministicFail,
            Verdict.Review => ReviewNeeded,
            _ => Pass,
        };
    }
}

/// <summary>
/// Runs judges over subjects: deterministic checks, then the request (from the cache when it holds one), then the
/// evaluation. A deterministic Fail skips the model call.
/// </summary>
public sealed class JudgeRunner(ISystemOneClient client, IReadOnlyDictionary<string, ModelEndpoint> models, Thresholds thresholds, ResponseCache? cache)
{
    public async Task<IReadOnlyList<JudgeResult>> RunAsync(IEnumerable<IJudge> judges, IEnumerable<JudgeSubject> subjects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(judges);
        ArgumentNullException.ThrowIfNull(subjects);
        var subjectList = subjects.ToList();
        var results = new List<JudgeResult>();
        foreach (var judge in judges)
        {
            foreach (var subject in subjectList.Where(judge.AppliesTo))
            {
                results.Add(await JudgeAsync(judge, subject, cancellationToken).ConfigureAwait(false));
            }
        }

        return results;
    }

    public async Task<JudgeResult> JudgeAsync(IJudge judge, JudgeSubject subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(subject);
        if (!models.TryGetValue(judge.ModelName, out var model))
        {
            return new JudgeResult(judge.Name, subject.Id, judge.ModelName, Verdict.Review, [], $"Model '{judge.ModelName}' is not in models.json.");
        }

        var checks = judge.Check(subject);
        if (checks.Any(f => f.Verdict == Verdict.Fail))
        {
            return new JudgeResult(judge.Name, subject.Id, model.Name, Verdict.Fail, checks);
        }

        var request = judge.BuildRequest(subject, model.Model);
        var problems = request.Validate(model.Images);
        if (problems.Count > 0)
        {
            return new JudgeResult(judge.Name, subject.Id, model.Name, Finding.Worst(checks), checks, $"Invalid request: {string.Join(" ", problems)}");
        }

        var key = ResponseCache.Key(request);
        var response = cache?.TryGet(key);
        var cached = response is not null && response.Mismatches(request).Count == 0;
        if (!cached)
        {
            try
            {
                response = await client.AskAsync(model, request, cancellationToken).ConfigureAwait(false);
            }
            catch (JudgeTransportException ex)
            {
                return new JudgeResult(judge.Name, subject.Id, model.Name, Finding.Worst(checks), checks, ex.Message);
            }

            cache?.Put(key, response);
        }

        var findings = checks.Concat(judge.Evaluate(subject, request, response!, thresholds)).ToList();
        return new JudgeResult(judge.Name, subject.Id, model.Name, Finding.Worst(findings), findings, Cached: cached);
    }
}
