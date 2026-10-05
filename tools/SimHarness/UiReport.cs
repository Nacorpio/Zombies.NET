using Zombies.Domain.Mods;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Ui;

/// <summary>
/// Loads the UI of the repository mods headlessly: every string table, every layout, and every key a layout needs.
/// A missing translation or a layout that does not fit the screen fails the run, so CI catches it without a window.
/// </summary>
internal static class UiReport
{
    private static readonly UiRect Screen = new(0, 0, 1280, 720);

    public static int Run(string[] args)
    {
        var directory = args.Length > 0 ? args[0] : "mods";
        var packages = DirectoryModSource.Read(directory);
        var mods = ModLoader.Load(packages);
        if (!mods.IsSuccess)
        {
            foreach (var error in mods.Errors)
            {
                Console.Error.WriteLine(error);
            }

            return 1;
        }

        var problems = 0;
        var localization = LocalizationLoader.Load(packages, mods);
        foreach (var problem in localization.Problems)
        {
            Console.Error.WriteLine(problem);
            problems++;
        }

        var layouts = LayoutLoader.Load(packages, mods);
        foreach (var problem in layouts.Problems)
        {
            Console.Error.WriteLine(problem);
            problems++;
        }

        var localizer = localization.Localizer;
        foreach (var language in localizer.Languages)
        {
            foreach (var key in localizer.MissingKeys(language))
            {
                Console.Error.WriteLine($"[Language] {language} is missing '{key}'.");
                problems++;
            }
        }

        foreach (var layout in layouts.Layouts.Values)
        {
            foreach (var key in layout.Keys())
            {
                if (string.Equals(localizer.Get(key), key, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine($"[Layout] {layout.Id} needs '{key}', which no language has.");
                    problems++;
                }
            }

            foreach (var scale in new[] { 1f, 2f, 3f })
            {
                layout.Arrange(Screen, new UiContext(localizer, scale, (int)scale));
                foreach (var widget in Walk(layout.Root))
                {
                    if (widget.Bounds.X < Screen.X || widget.Bounds.Right > Screen.Right || widget.Bounds.Y < Screen.Y || widget.Bounds.Bottom > Screen.Bottom)
                    {
                        Console.Error.WriteLine($"[Layout] {layout.Id} at scale {scale}: '{widget.Id}' is outside the screen ({widget.Bounds}).");
                        problems++;
                    }
                }
            }
        }

        Console.WriteLine($"SimHarness ui: {localizer.Languages.Count} languages, {layouts.Layouts.Count} layouts, {problems} problems");
        foreach (var language in localizer.Languages)
        {
            Console.WriteLine($"  {language}");
        }

        foreach (var layout in layouts.Layouts.Values.OrderBy(l => l.Id, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {layout.Id}: {layout.Keys().Count} keys");
        }

        return problems == 0 ? 0 : 1;
    }

    private static IEnumerable<Widget> Walk(Widget widget)
    {
        yield return widget;
        foreach (var child in widget.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }
}
