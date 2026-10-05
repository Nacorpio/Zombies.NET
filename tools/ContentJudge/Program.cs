using Zombies.ContentJudge;
using Zombies.ContentJudge.Judging;
using Zombies.ContentJudge.SystemOne;
using Zombies.Domain.Mods;
using Zombies.Engine.Core.Modding;

// Dev-time content review with decision models (Jev, Clef Flash). Nothing under src/ references this tool.
//   dotnet run --project tools/ContentJudge -- dry-run [mods] [--judge NAME]
//       print the request body every judge would send for every definition, one JSON object per line; calls no model
//   dotnet run --project tools/ContentJudge -- run [mods] [--judge NAME] [--out DIR] [--cache DIR] [--no-cache]
//       judge every definition and write content-judge.md and content-judge.json (default artifacts/judge-reports)
// Both take --thresholds PATH and --models PATH (default tools/ContentJudge/*.json).
// Keys come only from the environment: TYPESAFE_API_KEY (Jev), CLOUDFLARE_API_TOKEN and CLOUDFLARE_ACCOUNT_ID (Clef Flash).
// Exit codes: 0 pass, 1 deterministic fail, 2 review needed, 3 transport error.
if (args.Length > 0 && args[0] == "dry-run")
{
    return Cli.DryRun(args[1..]);
}

if (args.Length > 0 && args[0] == "run")
{
    return await Cli.RunAsync(args[1..]).ConfigureAwait(false);
}

Console.Error.WriteLine("usage: ContentJudge dry-run [mods] [--judge NAME] | run [mods] [--judge NAME] [--out DIR] [--cache DIR] [--no-cache]");
return 64;

namespace Zombies.ContentJudge
{
    internal static class Cli
    {
        public static int DryRun(string[] args)
        {
            var options = CliOptions.Parse(args);
            if (!TryLoad(options, out var subjects, out var models, out var judges))
            {
                return JudgeExitCode.DeterministicFail;
            }

            var count = 0;
            var invalid = 0;
            foreach (var judge in judges)
            {
                var model = models[judge.ModelName];
                foreach (var subject in subjects.Where(judge.AppliesTo))
                {
                    var request = judge.BuildRequest(subject, model.Model);
                    var problems = request.Validate(model.Images);
                    foreach (var problem in problems)
                    {
                        Console.Error.WriteLine($"{judge.Name} {subject.Id}: {problem}");
                    }

                    invalid += problems.Count > 0 ? 1 : 0;
                    Console.WriteLine(request.ToJsonString());
                    count++;
                }
            }

            Console.Error.WriteLine($"ContentJudge: {count} requests for {subjects.Count} definitions from {judges.Count} judges, {invalid} invalid; no model was called");
            return invalid == 0 ? JudgeExitCode.Pass : JudgeExitCode.TransportError;
        }

        public static async Task<int> RunAsync(string[] args)
        {
            var options = CliOptions.Parse(args);
            if (!TryLoad(options, out var subjects, out var models, out var judges))
            {
                return JudgeExitCode.DeterministicFail;
            }

            Thresholds thresholds;
            try
            {
                thresholds = Thresholds.Parse(File.ReadAllText(options.ThresholdsPath));
            }
            catch (Exception ex) when (ex is IOException or FormatException)
            {
                Console.Error.WriteLine($"ContentJudge: {ex.Message}");
                return JudgeExitCode.TransportError;
            }

            var cache = options.NoCache ? null : new ResponseCache(options.CacheDirectory);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var runner = new JudgeRunner(new SystemOneClient(http, Environment.GetEnvironmentVariable), models, thresholds, cache);
            var results = await runner.RunAsync(judges, subjects).ConfigureAwait(false);

            var (markdown, json) = ReportWriter.Write(options.OutputDirectory, results);
            var exitCode = JudgeExitCode.For(results);
            Console.WriteLine($"ContentJudge: {results.Count} results ({results.Count(r => r.Cached)} cached), exit code {exitCode}; wrote {markdown} and {json}");
            return exitCode;
        }

        private static bool TryLoad(CliOptions options, out IReadOnlyList<JudgeSubject> subjects, out IReadOnlyDictionary<string, ModelEndpoint> models, out IReadOnlyList<IJudge> judges)
        {
            subjects = [];
            models = new Dictionary<string, ModelEndpoint>();
            judges = [.. JudgeCatalog.All.Where(j => options.Judge is null || string.Equals(j.Name, options.Judge, StringComparison.Ordinal))];
            if (judges.Count == 0)
            {
                Console.Error.WriteLine($"ContentJudge: no judge named '{options.Judge}'. Judges: {string.Join(", ", JudgeCatalog.All.Select(j => j.Name))}");
                return false;
            }

            try
            {
                models = ModelEndpoint.Parse(File.ReadAllText(options.ModelsPath), Environment.GetEnvironmentVariable);
            }
            catch (Exception ex) when (ex is IOException or FormatException)
            {
                Console.Error.WriteLine($"ContentJudge: {ex.Message}");
                return false;
            }

            var known = models;
            var missing = judges.Where(j => !known.ContainsKey(j.ModelName)).Select(j => j.ModelName).ToList();
            if (missing.Count > 0)
            {
                Console.Error.WriteLine($"ContentJudge: models.json has no entry for {string.Join(", ", missing)}");
                return false;
            }

            var result = ModLoader.Load(DirectoryModSource.Read(options.ModsDirectory));
            foreach (var error in result.Errors)
            {
                Console.Error.WriteLine(error);
            }

            if (!result.IsSuccess)
            {
                return false;
            }

            subjects = [.. result.Registry.Definitions.Select(JudgeSubject.FromDefinition)];
            return true;
        }
    }

    /// <summary>Every judge the tool knows. Later tickets add the text, icon, and render judges here.</summary>
    internal static class JudgeCatalog
    {
        public static IReadOnlyList<IJudge> All { get; } = [new DefinitionJudge()];
    }

    internal sealed record CliOptions(string ModsDirectory, string? Judge, string OutputDirectory, string CacheDirectory, bool NoCache, string ThresholdsPath, string ModelsPath)
    {
        public static CliOptions Parse(string[] args)
        {
            var root = FindRepositoryRoot();
            string? mods = null, judge = null, output = null, cache = null, thresholds = null, modelsPath = null;
            var noCache = false;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--judge" when i + 1 < args.Length: judge = args[++i]; break;
                    case "--out" when i + 1 < args.Length: output = args[++i]; break;
                    case "--cache" when i + 1 < args.Length: cache = args[++i]; break;
                    case "--thresholds" when i + 1 < args.Length: thresholds = args[++i]; break;
                    case "--models" when i + 1 < args.Length: modelsPath = args[++i]; break;
                    case "--no-cache": noCache = true; break;
                    default: mods ??= args[i]; break;
                }
            }

            return new CliOptions(
                mods ?? "mods",
                judge,
                output ?? Path.Combine(root, "artifacts", "judge-reports"),
                cache ?? Path.Combine(root, "artifacts", "judge-cache"),
                noCache,
                thresholds ?? ConfigPath(root, "thresholds.json"),
                modelsPath ?? ConfigPath(root, "models.json"));
        }

        /// <summary>The checked-in file under <c>tools/ContentJudge/</c> when run from the repository, else the copy next to the binary.</summary>
        private static string ConfigPath(string root, string name)
        {
            var source = Path.Combine(root, "tools", "ContentJudge", name);
            return File.Exists(source) ? source : Path.Combine(AppContext.BaseDirectory, name);
        }

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Zombies.slnx")))
                {
                    return directory.FullName;
                }
            }

            return Directory.GetCurrentDirectory();
        }
    }
}
