// Order in which a game plays its scenario files and the choices between them. The engine
// keeps this in SRC_MAIN.SCN; the flows here were read from its disassembly
// (docs/engine-notes.md, section 5).

namespace GrandCrossExtractor.Player;

/// <summary>A step of a route: play a scenario file, or ask a question that picks the next files.</summary>
public abstract record FlowStep;

public sealed record PlayStep(string File) : FlowStep;

/// <summary>A choice; option i plays <see cref="Branches"/>[i], then the route goes on.</summary>
public sealed record ChoiceStep(string[] Options, string[][] Branches) : FlowStep;

public sealed record Route(string Title, IReadOnlyList<FlowStep> Steps);

/// <summary>
/// Opening, then a menu offering every route not played yet, then the ending once all are played.
/// </summary>
public sealed class StoryFlow
{
    public string Title { get; init; } = "";
    public IReadOnlyList<string> Opening { get; init; } = Array.Empty<string>();

    /// <summary>The route menu; option i starts <see cref="Routes"/>[i].</summary>
    public IReadOnlyList<Route> Routes { get; init; } = Array.Empty<Route>();

    /// <summary>SYSTEM.S25 slot of the first menu button (normal; +1 = highlighted, +10 = next option), or -1.</summary>
    public int MenuButtonSlot { get; init; } = -1;

    public IReadOnlyList<string> Ending { get; init; } = Array.Empty<string>();

    /// <summary>A chapter that can be started from the chapter list.</summary>
    public sealed record Chapter(string File, string Title, string Group);

    /// <summary>
    /// Every scenario file in playing order, with the route it belongs to. <paramref name="describe"/>
    /// gives a file's scene title, if it has one.
    /// </summary>
    public IEnumerable<Chapter> GetChapters(Func<string, string?>? describe = null)
    {
        string Title(string file, string? option = null)
        {
            string title = file;
            if (option != null)
                title += $"  [{option}]";
            if (describe?.Invoke(file) is { Length: > 0 } scene)
                title += "  " + scene;
            return title;
        }

        foreach (var file in Opening)
            yield return new Chapter(file, Title(file), "Opening");
        foreach (var route in Routes)
        {
            foreach (var step in route.Steps)
            {
                if (step is PlayStep play)
                    yield return new Chapter(play.File, Title(play.File), route.Title);
                else if (step is ChoiceStep choice)
                    for (int i = 0; i < choice.Options.Length; i++)
                        foreach (var file in choice.Branches[i])
                            yield return new Chapter(file, Title(file, choice.Options[i]), route.Title);
            }
        }
        foreach (var file in Ending)
            yield return new Chapter(file, Title(file), "Ending");
    }

    /// <summary>The flow of a game by its scheme name, or null if Play Story does not know it yet.</summary>
    public static StoryFlow? For(string schemeName) =>
        schemeName.Equals("Oreimo Plus", StringComparison.OrdinalIgnoreCase) ? OreimoPlus : null;

    // SRC_MAIN.SCN: ore01, then "callmod 203" with eight buttons (a[780] = routes played),
    // switch to the route; routes 3, 5 and 6 ask 中に出す / 外に出す after -02 (-02b / -02c),
    // then all go on with -02d. When a[780] == 255: ore10 (ending and staff roll).
    private static readonly StoryFlow OreimoPlus = new()
    {
        Title = "俺妹プラス",
        Opening = new[] { "ORE01" },
        MenuButtonSlot = 400,
        Routes = new[]
        {
            Simple("頑張って考える", "ORE02"),
            Simple("公園に行く", "ORE03"),
            WithChoice("コンビニに行く", "ORE04"),
            Simple("桐乃の学校を見学", "ORE05"),
            WithChoice("桐乃の部活を見学", "ORE06"),
            WithChoice("ラブホに行きたい", "ORE07"),
            Simple("レンタルルームへ", "ORE08"),
            Simple("アキバへ行く", "ORE09"),
        },
        Ending = new[] { "ORE10" },
    };

    private static Route Simple(string title, string stem) =>
        new(title, new FlowStep[] { new PlayStep(stem + "-01"), new PlayStep(stem + "-02"), new PlayStep(stem + "-03") });

    private static Route WithChoice(string title, string stem) =>
        new(title, new FlowStep[]
        {
            new PlayStep(stem + "-01"),
            new PlayStep(stem + "-02"),
            new ChoiceStep(new[] { "中に出す", "外に出す" }, new[] { new[] { stem + "-02B" }, new[] { stem + "-02C" } }),
            new PlayStep(stem + "-02D"),
            new PlayStep(stem + "-03"),
        });
}
