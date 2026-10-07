using System.Globalization;
using Zombies.Domain.Mods;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Ecs;
using Zombies.Engine.Ui;

/// <summary>
/// Runs the Code mods of a mods folder as a dedicated Server, a client, and solo play would, and checks that each loads what its
/// side allows and that every message has the same number wherever it is registered. Also installs a mod the way the game
/// does, printing the trusted-code warning a Code mod needs.
/// </summary>
internal static class CodeModReport
{
    public static int Run(string[] args)
    {
        var directory = args.Length > 0 ? args[0] : "mods";
        var all = DirectoryModSource.Read(directory);
        var failures = new List<string>();
        var numbers = new Dictionary<string, ushort>(StringComparer.Ordinal);
        foreach (var role in new[] { ProcessRole.Server, ProcessRole.Client, ProcessRole.Solo })
        {
            var packages = ModSides.ForRole(all, role);
            var mods = ModLoader.Load(packages);
            if (!mods.IsSuccess)
            {
                failures.AddRange(mods.Errors.Select(e => $"{role}: {e}"));
                continue;
            }

            var code = CodeModLoader.Load(packages, mods, role);
            failures.AddRange(code.Problems.Select(p => $"{role}: {p}"));
            var traits = new TraitRegistry();
            BaseTraits.Register(traits);
            CodeModTraits.Register(traits, code);

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"SimHarness codemods ({role}): {mods.Mods.Count} mods, code running for [{string.Join(", ", code.Mods.Select(m => m.Manifest.Id))}], not here [{string.Join(", ", code.NotRunHere.Select(m => m.Id))}], {traits.Traits.Count()} traits"));
            foreach (var (id, number) in code.Messages.All)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  message {id} = {number}"));
                if (numbers.TryGetValue(id, out var seen) && seen != number)
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture, $"{role}: '{id}' is {number} here but {seen} elsewhere"));
                }

                numbers[id] = number;
            }

            if (role == ProcessRole.Server && mods.Mods.FirstOrDefault(m => m.Manifest.Side == ModSide.Client) is { } clientMod)
            {
                failures.Add($"the dedicated Server loaded the client-only mod '{clientMod.Manifest.Id}'");
            }
        }

        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"SimHarness codemods FAILED: {failure}");
        }

        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// <c>install &lt;mod&gt; &lt;mods-root&gt; [--trust] [--lang CODE]</c>: installs a mod into a mods folder. A Code mod prints the
    /// trusted-code warning, in the language chosen from the string tables of the mods folder (or the repository's), and is
    /// only installed with <c>--trust</c>. Exit code 0 installed, 2 not installed for want of trust, 1 anything else.
    /// </summary>
    public static int Install(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("SimHarness install: needs <mod folder> <mods root> [--trust] [--lang CODE].");
            return 1;
        }

        var (source, modsRoot) = (args[0], args[1]);
        var trust = args.Contains("--trust", StringComparer.Ordinal);
        var langAt = Array.IndexOf(args, "--lang");
        var language = langAt >= 0 && langAt + 1 < args.Length ? args[langAt + 1] : "en";

        if (ModInstaller.Review(source, out var error) is not { } review)
        {
            Console.Error.WriteLine($"SimHarness install: {error}");
            return 1;
        }

        if (CodeModWarning.IsNeeded(review))
        {
            var localizer = WarningLocalizer(Directory.Exists(modsRoot) && Directory.EnumerateFileSystemEntries(modsRoot).Any() ? modsRoot : "mods");
            localizer.TrySetLanguage(language);
            var dialog = CodeModWarning.Create(localizer, review);
            Console.WriteLine($"SimHarness install: '{review.Manifest.Id}' is a Code mod ({string.Join(", ", review.CodeFiles)}). The player sees:");
            Console.WriteLine($"  [{dialog.Title}]");
            foreach (var line in dialog.MessageLines)
            {
                Console.WriteLine($"  {line}");
            }

            Console.WriteLine($"  ({dialog.Caption})");
            Console.WriteLine($"  {string.Join("  ", dialog.Buttons.Select(b => $"<{b.Label}>"))}");
        }

        Directory.CreateDirectory(modsRoot);
        var result = ModInstaller.Install(source, modsRoot, trust);
        Console.WriteLine($"SimHarness install: {result.Status}. {result.Detail}");
        return result.Status switch
        {
            ModInstallStatus.Installed => 0,
            ModInstallStatus.NeedsTrust => 2,
            _ => 1,
        };
    }

    private static Localizer WarningLocalizer(string modsRoot)
    {
        var packages = DirectoryModSource.Read(modsRoot);
        var mods = ModLoader.Load(packages);
        return LocalizationLoader.Load(packages, mods).Localizer;
    }
}
