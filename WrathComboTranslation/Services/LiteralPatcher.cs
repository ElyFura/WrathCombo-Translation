using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace WrathComboTranslation.Services;

/// <summary>
///     Translates the string literals Wrath hard-codes into its UI - opener choices, job
///     option labels, the content-type radio buttons and the like. These never pass through a
///     ResourceManager, so the resource overlay cannot reach them.
/// </summary>
/// <remarks>
///     A Harmony transpiler swaps the <c>ldstr</c> operand for the translation at patch time.
///     It emits no lookup call: the literal is already known while transpiling, so there is no
///     per-frame cost and no helper that could misbehave inside Wrath's draw loop. Changing
///     the pack simply unpatches and patches again.
///     <br /><br />
///     Only methods that have at least one translation are patched, which keeps both the JIT
///     cost and the blast radius small.
///     <br /><br />
///     Keys are <c>Type::Method/literal</c>, as produced by <c>tools/LiteralExtract</c>. The
///     location is part of the key because the same text can be a visible label in one method
///     and an ImGui id or a string comparison in another, and only the first may change.
/// </remarks>
internal sealed class LiteralPatcher : IDisposable
{
    private const string HarmonyId = "de.elyfura.wrathcombotranslation.literals";

    private const BindingFlags AllMembers =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
        BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    // Static, because the transpiler must be a static method Harmony can call.
    private static readonly Dictionary<string, string> Replacements = new(StringComparer.Ordinal);
    private static int _replaced;

    // object, not Harmony: Dalamud enumerates this assembly's types to find the IDalamudPlugin
    // before constructing anything, and loading a type resolves its field types. A
    // Harmony-typed field would demand 0Harmony before HarmonyBootstrap has run.
    private object? _harmony;

    /// <summary>Methods currently patched.</summary>
    public int PatchedMethods { get; private set; }

    /// <summary>Literals replaced across all patched methods.</summary>
    public int ReplacedLiterals => _replaced;

    /// <summary>
    ///     Translations whose method no longer exists in the loaded Wrath, usually because a
    ///     Wrath update renamed or reshuffled it. They fall back to English.
    /// </summary>
    public int StaleKeys { get; private set; }

    /// <summary>Methods Harmony refused to patch.</summary>
    public List<string> Failures { get; } = [];

    public bool Active => _harmony is not null;

    public void Apply(System.Reflection.Assembly wrath, TranslationPack pack, string uiLanguage)
    {
        Remove();

        if (!pack.AppliesTo(uiLanguage))
            return;

        if (!HarmonyBootstrap.Ensure())
        {
            Failures.Add($"Harmony unavailable: {HarmonyBootstrap.Error}");
            return;
        }

        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in pack.FileEntries(TranslationPack.LiteralsFile))
        {
            // The owner never contains '/', so the first slash ends it, which keeps literals
            // that contain slashes themselves unambiguous.
            var slash = key.IndexOf('/');
            if (slash <= 0 || slash == key.Length - 1)
            {
                Failures.Add($"Malformed key: {key}");
                continue;
            }

            // An identical "translation" needs no patch.
            if (string.Equals(key[(slash + 1)..], value, StringComparison.Ordinal))
                continue;

            owners.Add(key[..slash]);
            Replacements[key] = value;
        }

        if (owners.Count == 0)
            return;

        var clock = Stopwatch.StartNew();
        Patch(wrath, owners);
        Log.Information(
            $"Literal patches: {PatchedMethods} method(s), {_replaced} literal(s) replaced, " +
            $"{StaleKeys} stale key(s), {Failures.Count} failure(s), in {clock.ElapsedMilliseconds} ms.");
    }

    /// <summary>
    ///     Everything that names a Harmony type lives here rather than in <see cref="Apply" />:
    ///     compiling a method makes the JIT resolve every type it names, so a Harmony reference
    ///     in Apply would demand 0Harmony before Apply had a chance to bootstrap it.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void Patch(System.Reflection.Assembly wrath, HashSet<string> owners)
    {
        var harmony = new Harmony(HarmonyId);
        _harmony = harmony;

        var transpiler = new HarmonyMethod(typeof(LiteralPatcher).GetMethod(nameof(Transpile),
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));

        foreach (var owner in owners)
        {
            var methods = Resolve(wrath, owner);
            if (methods.Count == 0)
            {
                StaleKeys += Replacements.Keys.Count(k => k.StartsWith(owner + "/", StringComparison.Ordinal));
                continue;
            }

            foreach (var method in methods)
            {
                try
                {
                    harmony.Patch(method, transpiler: transpiler);
                    PatchedMethods++;
                }
                catch (Exception ex)
                {
                    Failures.Add($"{owner}: {ex.Message}");
                    Log.Warning(ex, $"Could not patch {owner}.");
                }
            }
        }
    }

    /// <summary>
    ///     Every method on the named type with the named name. Overloads are all returned: the
    ///     literal exists in only one of them, and patching a sibling without it changes nothing.
    /// </summary>
    private static List<MethodBase> Resolve(System.Reflection.Assembly wrath, string owner)
    {
        var result = new List<MethodBase>();

        var split = owner.LastIndexOf("::", StringComparison.Ordinal);
        if (split <= 0)
            return result;

        Type? type;
        try
        {
            type = wrath.GetType(owner[..split], throwOnError: false);
        }
        catch
        {
            return result;
        }

        // Open generics have no compiled code to patch; Wrath's UI has none worth the trouble.
        if (type is null || type.ContainsGenericParameters)
            return result;

        var name = owner[(split + 2)..];

        if (name is ".ctor" or ".cctor")
        {
            // A static constructor has already run by the time Wrath draws anything, and
            // patching it would never take effect; only instance constructors are worth it.
            if (name == ".ctor")
                result.AddRange(type.GetConstructors(AllMembers & ~BindingFlags.Static));
            return result;
        }

        result.AddRange(type.GetMethods(AllMembers)
            .Where(m => m.Name == name && !m.IsAbstract && !m.ContainsGenericParameters));
        return result;
    }

    /// <summary>
    ///     Replaces each translated <c>ldstr</c> operand. Builds a list rather than yielding:
    ///     an iterator would compile to a state machine with a CodeInstruction-typed field,
    ///     whose type would be resolved when Dalamud scans this assembly - before Harmony is
    ///     loaded.
    /// </summary>
    public static IEnumerable<CodeInstruction> Transpile(
        IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var owner = (__originalMethod.DeclaringType?.FullName ?? "?") + "::" + __originalMethod.Name + "/";
        var result = new List<CodeInstruction>();

        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ldstr &&
                instruction.operand is string literal &&
                Replacements.TryGetValue(owner + literal, out var translated))
            {
                instruction.operand = translated;
                _replaced++;
            }

            result.Add(instruction);
        }

        return result;
    }

    /// <summary>Removes every patch, putting Wrath's original code back.</summary>
    public void Remove()
    {
        if (_harmony is not null)
        {
            try
            {
                Unpatch(_harmony);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to remove the literal patches.");
            }

            Log.Information($"Literal patches removed from {PatchedMethods} method(s).");
        }

        _harmony = null;
        Replacements.Clear();
        _replaced = 0;
        PatchedMethods = 0;
        StaleKeys = 0;
        Failures.Clear();
    }

    /// <summary>
    ///     Kept out of <see cref="Remove" /> so that compiling Remove, which also runs before
    ///     Harmony is ever needed, does not make the JIT resolve 0Harmony.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Unpatch(object harmony) => ((Harmony)harmony).UnpatchAll(HarmonyId);

    public void Dispose() => Remove();
}
