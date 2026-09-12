using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WrathComboTranslation.Services;

/// <summary>
///     The set of translated strings currently in effect, keyed by resource short name
///     (<c>MainWindowUI</c>) and then by resource key (<c>Button_About</c>).
/// </summary>
/// <remarks>
///     A pack is deliberately sparse: a key that is absent simply falls through to Wrath's
///     own resources, so a partial translation degrades to English (or to whatever Wrath
///     already ships for that language) instead of showing blanks.
/// </remarks>
internal sealed class TranslationPack
{
    /// <summary>Directory name under the plugin config folder holding editable packs.</summary>
    public const string LooseFolderName = "packs";

    private const string EmbeddedPrefix = "WrathComboTranslation.Translations.";

    private readonly Dictionary<string, Dictionary<string, string>> _entries;

    private TranslationPack(
        string language,
        Dictionary<string, Dictionary<string, string>> entries,
        IReadOnlyList<string> sources)
    {
        Language = language;
        _entries = entries;
        Sources = sources;

        ExactCultureMatchRequired = language.Contains('-', StringComparison.Ordinal);
        TwoLetterCode = language.Split('-')[0];
        StringCount = entries.Values.Sum(d => d.Count);
    }

    /// <summary>Language code of this pack, e.g. <c>de</c> or <c>zh-Hans</c>.</summary>
    public string Language { get; }

    /// <summary>Two-letter prefix of <see cref="Language" />.</summary>
    public string TwoLetterCode { get; }

    /// <summary>
    ///     True for script-qualified languages such as <c>zh-Hans</c>, where matching on the
    ///     two-letter code alone would wrongly also catch <c>zh-Hant</c>.
    /// </summary>
    private bool ExactCultureMatchRequired { get; }

    /// <summary>Total number of translated strings across all resource files.</summary>
    public int StringCount { get; }

    /// <summary>Per-file counts, for the status window.</summary>
    public IReadOnlyDictionary<string, int> FileCounts
        => _entries.ToDictionary(kv => kv.Key, kv => kv.Value.Count);

    /// <summary>Human-readable list of where the strings came from, for the status window.</summary>
    public IReadOnlyList<string> Sources { get; }

    /// <summary>
    ///     When true the pack answers for every requested culture, not just a matching one.
    ///     Set from the user's "force language" setting.
    /// </summary>
    public bool Forced { get; set; }

    /// <summary>An empty pack, used before loading and when the user disables the overlay.</summary>
    public static TranslationPack Empty { get; } = new("none", [], []);

    /// <summary>
    ///     Resolves a key, or returns false to let the caller fall back to Wrath's own resources.
    /// </summary>
    public bool TryGet(string shortName, string key, CultureInfo? culture, out string value)
    {
        value = string.Empty;

        if (StringCount == 0)
            return false;

        if (!Forced && !CultureMatches(culture))
            return false;

        return _entries.TryGetValue(shortName, out var file)
               && file.TryGetValue(key, out value!)
               && !string.IsNullOrEmpty(value);
    }

    private bool CultureMatches(CultureInfo? culture)
    {
        if (culture is null)
            return false;

        return ExactCultureMatchRequired
            ? string.Equals(culture.Name, Language, StringComparison.OrdinalIgnoreCase)
            : string.Equals(culture.TwoLetterISOLanguageName, TwoLetterCode, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Loads <paramref name="language" />, merging the embedded pack with loose JSON files
    ///     from <paramref name="looseRoot" />. Loose files win per key, so a translator can
    ///     correct a shipped string without rebuilding the plugin.
    /// </summary>
    public static TranslationPack Load(string language, string looseRoot)
    {
        var entries = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<string>();

        var embedded = LoadEmbedded(language, entries);
        if (embedded > 0)
            sources.Add($"embedded ({embedded} strings)");

        var loose = LoadLoose(language, looseRoot, entries);
        if (loose > 0)
            sources.Add($"{Path.Combine(looseRoot, language)} ({loose} strings)");

        return new TranslationPack(language, entries, sources);
    }

    /// <summary>
    ///     Every language that has either an embedded pack or a loose folder.
    /// </summary>
    public static IReadOnlyList<string> AvailableLanguages(string looseRoot)
    {
        var languages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in typeof(TranslationPack).Assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(EmbeddedPrefix, StringComparison.Ordinal))
                continue;

            // WrathComboTranslation.Translations.<lang>.<File>.json
            var rest = name[EmbeddedPrefix.Length..];
            var dot = rest.IndexOf('.');
            if (dot > 0)
                languages.Add(rest[..dot]);
        }

        if (Directory.Exists(looseRoot))
            foreach (var dir in Directory.EnumerateDirectories(looseRoot))
                languages.Add(Path.GetFileName(dir));

        return [.. languages];
    }

    private static int LoadEmbedded(string language, Dictionary<string, Dictionary<string, string>> into)
    {
        var assembly = typeof(TranslationPack).Assembly;
        var prefix = EmbeddedPrefix + language + ".";
        var loaded = 0;

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                !name.EndsWith(".json", StringComparison.Ordinal))
                continue;

            var shortName = name[prefix.Length..^".json".Length];

            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null)
                    continue;

                using var reader = new StreamReader(stream);
                loaded += Merge(into, shortName, reader.ReadToEnd(), name);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Could not read embedded translation '{name}'.");
            }
        }

        return loaded;
    }

    private static int LoadLoose(string language, string looseRoot, Dictionary<string, Dictionary<string, string>> into)
    {
        var dir = Path.Combine(looseRoot, language);
        if (!Directory.Exists(dir))
            return 0;

        var loaded = 0;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                loaded += Merge(into, Path.GetFileNameWithoutExtension(file), File.ReadAllText(file), file);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Could not read translation file '{file}'.");
            }
        }

        return loaded;
    }

    /// <summary>
    ///     Merges one flat <c>{ "Key": "Value" }</c> document into the pack, overwriting
    ///     any key already present.
    /// </summary>
    private static int Merge(
        Dictionary<string, Dictionary<string, string>> into,
        string shortName,
        string json,
        string origin)
    {
        Dictionary<string, string>? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json,
                new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            Log.Error($"'{origin}' is not valid JSON and was skipped: {ex.Message}");
            return 0;
        }

        if (parsed is null || parsed.Count == 0)
            return 0;

        if (!into.TryGetValue(shortName, out var file))
            into[shortName] = file = new Dictionary<string, string>(StringComparer.Ordinal);

        var added = 0;
        foreach (var (key, value) in parsed)
        {
            // Keys starting with '$' are reserved for metadata such as translator credits.
            // Not '_': Wrath's own resource keys are C# identifiers and some start with one,
            // such as Generics' _0Option, which this would otherwise silently discard.
            if (key.StartsWith('$') || string.IsNullOrWhiteSpace(value))
                continue;

            file[key] = value;
            added++;
        }

        return added;
    }
}
