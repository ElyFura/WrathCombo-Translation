using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;

namespace ResxExtract;

/// <summary>
///     Maintenance tool for the translation packs. It reads Wrath Combo's .resx files
///     straight from a checkout and turns them into the flat JSON the plugin consumes.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Keep German umlauts and CJK readable in the files translators actually edit.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "extract" => Extract(Options.Parse(args)),
                "report" => Report(Options.Parse(args)),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    ///     Writes one JSON file per Wrath resource file containing the English source strings,
    ///     which is the basis translators work from.
    /// </summary>
    private static int Extract(Options o)
    {
        var repo = o.Require("repo");
        var outDir = o.Require("out");

        Directory.CreateDirectory(outDir);

        var total = 0;
        var files = 0;

        foreach (var (shortName, path) in FindBaseResx(repo))
        {
            var entries = ReadResx(path);
            if (entries.Count == 0)
                continue;

            var target = Path.Combine(outDir, shortName + ".json");
            File.WriteAllText(target, JsonSerializer.Serialize(Sorted(entries), Json));

            total += entries.Count;
            files++;
            Console.WriteLine($"{shortName,-24} {entries.Count,6} strings");
        }

        Console.WriteLine();
        Console.WriteLine($"Wrote {files} files, {total} source strings to {outDir}");
        return 0;
    }

    /// <summary>
    ///     Compares a translation pack against the source strings and against what Wrath
    ///     already ships for that language, so effort goes to the keys that actually need it.
    /// </summary>
    private static int Report(Options o)
    {
        var repo = o.Require("repo");
        var packDir = o.Require("pack");
        var lang = o.Require("lang");

        var sourceTotal = 0;
        var upstreamTotal = 0;
        var packTotal = 0;
        var staleTotal = 0;

        Console.WriteLine($"{"Resource",-24} {"source",6} {"upstream",8} {"pack",8} {"missing",9} {"stale",7}");
        Console.WriteLine(new string('-', 68));

        foreach (var (shortName, path) in FindBaseResx(repo))
        {
            var source = ReadResx(path);
            if (source.Count == 0)
                continue;

            var upstreamPath = path[..^".resx".Length] + $".{lang}.resx";
            var upstream = File.Exists(upstreamPath) ? ReadResx(upstreamPath) : [];

            var packPath = Path.Combine(packDir, shortName + ".json");
            var pack = File.Exists(packPath) ? ReadJson(packPath) : [];

            // A key still needs work if neither our pack nor Wrath itself translates it.
            var missing = source.Keys.Count(k => !pack.ContainsKey(k) && !upstream.ContainsKey(k));

            // Keys we translate that no longer exist upstream, i.e. left over after a Wrath update.
            var stale = pack.Keys.Count(k => !source.ContainsKey(k));

            sourceTotal += source.Count;
            upstreamTotal += upstream.Count;
            packTotal += pack.Count;
            staleTotal += stale;

            Console.WriteLine(
                $"{shortName,-24} {source.Count,6} {upstream.Count,8} {pack.Count,8} {missing,9} {stale,7}");

            foreach (var key in pack.Keys.Where(k => !source.ContainsKey(k)).OrderBy(k => k))
                Console.WriteLine($"    stale key: {key}");
        }

        Console.WriteLine(new string('-', 68));
        Console.WriteLine($"{"TOTAL",-24} {sourceTotal,6} {upstreamTotal,8} {packTotal,8} " +
                          $"{sourceTotal - packTotal - upstreamTotal,9} {staleTotal,7}");

        var covered = Math.Min(sourceTotal, packTotal + upstreamTotal);
        Console.WriteLine();
        Console.WriteLine($"Coverage for '{lang}': {covered}/{sourceTotal} " +
                          $"({(sourceTotal == 0 ? 0 : covered * 100.0 / sourceTotal):F1}%)");
        return 0;
    }

    /// <summary>
    ///     All culture-neutral .resx files under the checkout's localization folder,
    ///     keyed by the short name the plugin uses for the matching JSON file.
    /// </summary>
    private static IEnumerable<(string ShortName, string Path)> FindBaseResx(string repo)
    {
        var root = Path.Combine(repo, "WrathCombo", "Resources", "Localization");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(
                $"'{root}' does not exist - is --repo pointing at a Wrath Combo checkout?");

        return Directory
            .EnumerateFiles(root, "*.resx", SearchOption.AllDirectories)
            // Culture-specific files carry a second extension (Foo.de.resx); skip those.
            .Where(p => !Path.GetFileNameWithoutExtension(p).Contains('.'))
            .Select(p => (Path.GetFileNameWithoutExtension(p), p))
            .OrderBy(t => t.Item1, StringComparer.Ordinal);
    }

    /// <summary>
    ///     Reads the string entries of a .resx, skipping the schema preamble and any
    ///     non-string resource.
    /// </summary>
    private static Dictionary<string, string> ReadResx(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var doc = XDocument.Load(path);

        foreach (var data in doc.Root?.Elements("data") ?? [])
        {
            var name = data.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(name))
                continue;

            // Typed or binary resources are not translatable text.
            if (data.Attribute("type") is not null || data.Attribute("mimetype") is not null)
                continue;

            var value = data.Element("value")?.Value;
            if (value is null)
                continue;

            result[name] = value;
        }

        return result;
    }

    /// <summary>
    ///     Reads a translation pack file. Underscore-prefixed keys hold metadata such as
    ///     translator credits and are dropped here exactly as the plugin drops them at runtime,
    ///     so they neither inflate the coverage count nor show up as stale keys.
    /// </summary>
    private static Dictionary<string, string> ReadJson(string path)
        => (JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? [])
            .Where(kv => !kv.Key.StartsWith('_') && !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    private static SortedDictionary<string, string> Sorted(Dictionary<string, string> d)
        => new(d, StringComparer.Ordinal);

    private static int Usage()
    {
        Console.WriteLine("""
            resx-extract - translation maintenance for Wrath Combo

              extract --repo <wrath checkout> --out <dir>
                  Write the English source strings as one JSON file per resource.

              report --repo <wrath checkout> --pack <dir> --lang <code>
                  Show how much of each resource is covered, and list keys in the
                  pack that no longer exist upstream.
            """);
        return 1;
    }

    /// <summary>Minimal <c>--key value</c> parser.</summary>
    private sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(string[] args)
        {
            var o = new Options();

            for (var i = 1; i < args.Length - 1; i++)
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                    o._values[args[i][2..]] = args[++i];

            return o;
        }

        public string Require(string key)
            => _values.TryGetValue(key, out var v)
                ? v
                : throw new ArgumentException($"missing required option --{key}");
    }
}
