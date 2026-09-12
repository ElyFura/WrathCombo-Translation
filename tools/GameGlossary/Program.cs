using System.Text.Encodings.Web;
using System.Text.Json;
using Lumina;
using Lumina.Data;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;

namespace GameGlossary;

/// <summary>
///     Builds an English-to-German glossary of the game's own terminology by reading the
///     installed client's data files.
/// </summary>
/// <remarks>
///     Wrath's preset descriptions spell out ability and status names as literal English text
///     — its own sheet-lookup helper is still a stub — so translating them well means using
///     the exact wording the German client uses. That wording is sitting in the game files,
///     which ship every language in one install, so there is no need to transcribe it by hand.
/// </remarks>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int Main(string[] args)
    {
        var options = Options.Parse(args);

        string sqpack, outPath, target;
        try
        {
            sqpack = options.Require("sqpack");
            outPath = options.Require("out");
            target = options.Value("lang") ?? "German";
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                """
                usage: game-glossary --sqpack <path to game/sqpack> --out <file.json> [--lang German]

                Writes { "English name": "Translated name" } for every action, status, trait
                and job in the installed client.
                """);
            return 1;
        }

        if (!Enum.TryParse<Language>(target, ignoreCase: true, out var language))
        {
            Console.Error.WriteLine($"error: '{target}' is not a Lumina language. " +
                                    $"Known: {string.Join(", ", Enum.GetNames<Language>())}");
            return 1;
        }

        if (!Directory.Exists(sqpack))
        {
            Console.Error.WriteLine($"error: '{sqpack}' does not exist.");
            return 1;
        }

        var game = new GameData(sqpack);

        // One flat map, because the preset text these feed is flat prose: a name is looked up
        // without knowing whether it is an action, a status or a trait.
        var glossary = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var collisions = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        // Only player actions: the sheet is mostly enemy abilities, which Wrath never names
        // and which collide with player ones often enough to poison the glossary.
        var counts = new List<string>
        {
            Collect<Action>(game, language, glossary, collisions,
                r => (r.RowId, r.Name.ToString()), "actions", r => r.IsPlayerAction),
            Collect<Status>(game, language, glossary, collisions,
                r => (r.RowId, r.Name.ToString()), "statuses"),
            Collect<Trait>(game, language, glossary, collisions,
                r => (r.RowId, r.Name.ToString()), "traits"),
            Collect<ClassJob>(game, language, glossary, collisions,
                r => (r.RowId, r.Name.ToString()), "jobs"),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(glossary, Json));

        foreach (var line in counts)
            Console.WriteLine(line);

        Console.WriteLine();
        Console.WriteLine($"Wrote {glossary.Count} {language} terms to {outPath}");

        if (collisions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{collisions.Count} English name(s) map to more than one translation; " +
                              "these need a human decision and were left out:");
            foreach (var (english, translations) in collisions)
                Console.WriteLine($"  {english} -> {string.Join(" | ", translations)}");
        }

        return 0;
    }

    /// <summary>
    ///     Adds one sheet's names to the glossary, pairing each row's English name with its
    ///     translation by row id.
    /// </summary>
    /// <remarks>
    ///     An English name that resolves to two different translations is ambiguous without
    ///     context, so it is dropped rather than guessed at, and reported for a human to
    ///     settle.
    /// </remarks>
    private static string Collect<T>(
        GameData game,
        Language language,
        SortedDictionary<string, string> glossary,
        SortedDictionary<string, SortedSet<string>> collisions,
        Func<T, (uint RowId, string Name)> read,
        string label,
        Func<T, bool>? include = null)
        where T : struct, Lumina.Excel.IExcelRow<T>
    {
        var english = game.GetExcelSheet<T>(Language.English);
        var translated = game.GetExcelSheet<T>(language);

        if (english is null || translated is null)
            return $"{label,-10} unavailable";

        var added = 0;

        foreach (var row in english)
        {
            if (include is not null && !include(row))
                continue;

            var (rowId, englishName) = read(row);
            if (string.IsNullOrWhiteSpace(englishName))
                continue;

            if (!translated.TryGetRow(rowId, out var other))
                continue;

            // Terms that read the same in both languages are kept rather than skipped: to a
            // translator, "Kardia stays Kardia" and "no such term" are different answers, and
            // dropping them would make the two indistinguishable.
            var translatedName = read(other).Name;
            if (string.IsNullOrWhiteSpace(translatedName) || IsDeadRow(translatedName))
                continue;

            if (glossary.TryGetValue(englishName, out var existing))
            {
                if (existing == translatedName)
                    continue;

                // Ambiguous: remember both and take the entry back out.
                if (!collisions.TryGetValue(englishName, out var set))
                    collisions[englishName] = set = [existing];

                set.Add(translatedName);
                glossary.Remove(englishName);
                added--;
                continue;
            }

            if (collisions.ContainsKey(englishName))
            {
                collisions[englishName].Add(translatedName);
                continue;
            }

            glossary[englishName] = translatedName;
            added++;
        }

        return $"{label,-10} {added,6} terms";
    }

    /// <summary>
    ///     Whether a translated name is leftover data rather than a real term.
    /// </summary>
    /// <remarks>
    ///     Retired rows keep their Japanese placeholder text in every language's sheet
    ///     (typically marked for deletion), so CJK characters in a German name mean the row
    ///     was never translated because it is no longer used. Leaving them in turns real
    ///     entries into false collisions — it is what hid 'Regen' behind a dead row.
    /// </remarks>
    private static bool IsDeadRow(string name)
        => name.Any(c => c is >= '　' and <= '鿿' or >= '＀' and <= '￯');

    /// <summary>Minimal <c>--key value</c> parser.</summary>
    private sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(string[] args)
        {
            var o = new Options();

            for (var i = 0; i < args.Length - 1; i++)
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                    o._values[args[i][2..]] = args[++i];

            return o;
        }

        public string? Value(string key)
            => _values.GetValueOrDefault(key);

        public string Require(string key)
            => _values.TryGetValue(key, out var v)
                ? v
                : throw new ArgumentException($"missing required option --{key}");
    }
}
