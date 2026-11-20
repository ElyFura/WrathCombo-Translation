using System.Text.Encodings.Web;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LiteralExtract;

/// <summary>
///     Lists the string literals Wrath Combo hard-codes into its UI, read straight from the
///     compiled assembly's IL. These never pass through a ResourceManager, so the overlay
///     cannot reach them; the plugin's LiteralPatcher rewrites them instead, and this tool
///     produces the catalogue it works from.
/// </summary>
/// <remarks>
///     A method counts as UI code when it calls into ImGui, Dalamud's interface helpers,
///     ECommons' ImGui helpers or Wrath's own window layer. Its <c>ldstr</c> operands are
///     the candidates.
///     <br /><br />
///     Keys are <c>Type::Method/literal</c>. The location belongs in the key because the same
///     text can be a visible label in one method and an ImGui id or a comparison in another.
///     The literal belongs in it (rather than an index) so adding a literal to a method does
///     not shift every other key.
/// </remarks>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] UiNamespaces =
    [
        "Dalamud.Bindings.ImGui",
        "Dalamud.Interface",
        "ECommons.ImGuiMethods",
        "WrathCombo.Window",
    ];

    /// <summary>
    ///     Types whose literals are deliberately left alone: the debug tab is developer
    ///     tooling, full of field names and raw values rather than prose.
    /// </summary>
    private static readonly string[] ExcludedTypes =
    [
        "WrathCombo.Window.Tabs.Debug",
        // Writes the plain-text debug report users attach to bug reports; it stays English.
        "WrathCombo.DebugFile",
        // Developer view of IPC action requests.
        "WrathCombo.Services.ActionRequestIPC.ActionRequestDebugUI",
        // Log lines, cache keys and culture handling - nothing a player reads.
        "WrathCombo.Data.Conflicts.ConflictingPluginsChecks",
        "WrathCombo.Window.Text",
        "WrathCombo.Window.Icons",
        "WrathCombo.Native.CustomActionManager",
    ];

    /// <summary>
    ///     Individual methods that live in UI types but whose literals are logic: config
    ///     property names, search keywords matched against what the player types, measuring
    ///     strings, and the plugin's own startup and logging.
    /// </summary>
    private static readonly string[] ExcludedOwnerPrefixes =
    [
        "WrathCombo.Window.MajorChangesWindow::get_WasUsingOld",
        "WrathCombo.Window.FeaturesWindow::TryFindKeywordsInSearch",
        "WrathCombo.Window.FeaturesWindow::PresetMatchesSearch",
        "WrathCombo.Window.FeaturesWindow::get_LetterWidth",
        "WrathCombo.WrathCombo::",
    ];

    private static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "pack")
            return Pack(args[1..]);

        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                """
                usage:
                  literal-extract <WrathCombo.dll> <source/Literals.json>
                      Writes the catalogue of hard-coded UI literals, keyed Type::Method/literal.
                  literal-extract pack <source/Literals.json> <pack/Literals.json> [text-map.json...]
                      Rebuilds a language's Literals.json against the current catalogue. Each key
                      keeps its translation; a key that is new (a Wrath update moved the text to
                      another method, or added it somewhere else) takes the translation of the same
                      English text from anywhere in the old pack or the text maps
                      ({ "English": "Translation" }). Keys gone from the catalogue are dropped.
                """);
            return 1;
        }

        var literals = Extract(args[0], out var methods, out var skipped);
        File.WriteAllText(args[1], JsonSerializer.Serialize(literals, Json) + Environment.NewLine);

        Console.WriteLine($"{methods} UI method(s), {literals.Count} literal(s) written to {args[1]}");
        Console.WriteLine($"skipped {skipped} literal(s): ImGui ids, no letters, or excluded types");
        return 0;
    }

    private static int Pack(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: literal-extract pack <source/Literals.json> <pack/Literals.json> [text-map.json...]");
            return 1;
        }

        var source = Read(args[0]);
        var old = File.Exists(args[1]) ? Read(args[1]) : new Dictionary<string, string>();

        // English text -> translation, from the old pack first, then the maps (later wins).
        var byText = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in old)
        {
            var slash = key.IndexOf('/');
            if (slash > 0 && !key.StartsWith('$'))
                byText.TryAdd(key[(slash + 1)..], value);
        }

        foreach (var map in args[2..])
            foreach (var (text, value) in Read(map))
                if (!string.IsNullOrWhiteSpace(value))
                    byText[text] = value;

        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        int kept = 0, carried = 0, missing = 0;

        foreach (var (key, text) in source)
        {
            if (old.TryGetValue(key, out var value))
            {
                result[key] = value;
                kept++;
            }
            else if (byText.TryGetValue(text, out value))
            {
                result[key] = value;
                carried++;
            }
            else
            {
                missing++;
            }
        }

        // Metadata ($note, $translator, ...) survives untouched, ahead of the entries.
        var output = old.Where(kv => kv.Key.StartsWith('$'))
            .Concat(result)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        File.WriteAllText(args[1], JsonSerializer.Serialize(output, Json) + Environment.NewLine);

        var dropped = old.Keys.Count(k => !k.StartsWith('$') && !source.ContainsKey(k));
        Console.WriteLine($"{result.Count} of {source.Count} literals translated: {kept} kept, " +
                          $"{carried} carried over by text; {missing} untranslated, {dropped} stale dropped.");
        return 0;

        static Dictionary<string, string> Read(string path)
            => JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
               ?? new Dictionary<string, string>();
    }

    private static SortedDictionary<string, string> Extract(string path, out int methods, out int skipped)
    {
        using var module = ModuleDefinition.ReadModule(path, new ReaderParameters
        {
            ReadingMode = ReadingMode.Deferred,
            // Only instruction operands are read; nothing needs resolving.
            AssemblyResolver = new NullResolver(),
        });

        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        methods = 0;
        skipped = 0;

        foreach (var type in AllTypes(module))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;

                var drawsUi = false;
                var found = new List<string>();

                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string literal)
                        found.Add(literal);
                    else if (!drawsUi && IsUiCall(instruction))
                        drawsUi = true;
                }

                if (!drawsUi || found.Count == 0)
                    continue;

                if (IsExcluded(type) ||
                    ExcludedOwnerPrefixes.Any(p => Describe(method).StartsWith(p, StringComparison.Ordinal)))
                {
                    skipped += found.Count;
                    continue;
                }

                methods++;
                var owner = Describe(method);

                foreach (var literal in found)
                {
                    // "##id" is an ImGui identity with no visible text; "label##id" is a label.
                    if (!literal.Any(char.IsLetter) || literal.StartsWith("##", StringComparison.Ordinal))
                    {
                        skipped++;
                        continue;
                    }

                    result[$"{owner}/{literal}"] = literal;
                }
            }
        }

        return result;
    }

    private static bool IsUiCall(Instruction instruction)
    {
        if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt &&
            instruction.OpCode != OpCodes.Newobj)
            return false;

        if (instruction.Operand is not MethodReference target)
            return false;

        var ns = Outermost(target.DeclaringType).Namespace ?? "";
        return UiNamespaces.Any(u => ns.StartsWith(u, StringComparison.Ordinal));
    }

    private static bool IsExcluded(TypeDefinition type)
    {
        var name = Outermost(type).FullName;
        return ExcludedTypes.Contains(name, StringComparer.Ordinal);
    }

    private static TypeReference Outermost(TypeReference type)
    {
        while (type.DeclaringType is not null)
            type = type.DeclaringType;
        return type;
    }

    /// <summary>
    ///     <c>Namespace.Type::Method</c>, with Cecil's nested-type '/' turned into the '+'
    ///     that <c>Assembly.GetType</c> understands. "::" rather than '.' because a
    ///     constructor's name is literally ".ctor".
    /// </summary>
    private static string Describe(MethodDefinition method)
        => method.DeclaringType.FullName.Replace('/', '+') + "::" + method.Name;

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        var stack = new Stack<TypeDefinition>(module.Types);
        while (stack.Count > 0)
        {
            var type = stack.Pop();
            yield return type;
            foreach (var nested in type.NestedTypes)
                stack.Push(nested);
        }
    }

    private sealed class NullResolver : IAssemblyResolver
    {
        public AssemblyDefinition? Resolve(AssemblyNameReference name) => null;
        public AssemblyDefinition? Resolve(AssemblyNameReference name, ReaderParameters parameters) => null;
        public void Dispose() { }
    }
}
