using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;

namespace WrathComboTranslation.Services;

/// <summary>
///     Puts 0Harmony into the default <see cref="AssemblyLoadContext" /> before anything in this
///     plugin touches a Harmony type.
/// </summary>
/// <remarks>
///     Dalamud loads every plugin, and everything next to it, into a <i>collectible</i> load
///     context. Harmony cannot live there: to build a patch, MonoMod emits a proxy type that
///     has to resolve Harmony's own types by name, and the runtime refuses to resolve a type
///     name to a collectible assembly, so every <c>Patch()</c> fails with "Resolving to a
///     collectible assembly is not supported".
///     <br /><br />
///     So 0Harmony is not shipped beside the plugin (<c>ExcludeAssets="runtime"</c>); it is
///     embedded, and loaded from the stream into the default context here. Nothing in the
///     plugin directory can then satisfy the reference, so the runtime falls through to the
///     default context and binds to this copy.
///     <br /><br />
///     The cost is that 0Harmony stays loaded after the plugin unloads: one assembly, and
///     unavoidable, since being non-collectible is the whole point.
/// </remarks>
internal static class HarmonyBootstrap
{
    private const string AssemblyName = "0Harmony";
    private const string ResourceName = "WrathComboTranslation.0Harmony.dll";

    private static bool _attempted;

    /// <summary>
    ///     True once Harmony is usable. Callers check this before naming a Harmony type, which
    ///     is why this class does not reference one.
    /// </summary>
    public static bool Ready { get; private set; }

    public static string? Error { get; private set; }

    public static bool Ensure()
    {
        if (_attempted)
            return Ready;

        _attempted = true;

        try
        {
            // A plugin reload arrives here with the previous copy still in the default context,
            // and LoadFromStream does not deduplicate, so reuse whatever is already there.
            var existing = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a =>
                string.Equals(a.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                using var stream = typeof(HarmonyBootstrap).Assembly.GetManifestResourceStream(ResourceName)
                                   ?? throw new FileNotFoundException($"Embedded resource {ResourceName} is missing.");
                existing = AssemblyLoadContext.Default.LoadFromStream(stream);
            }

            Ready = true;
            Log.Information($"{AssemblyName} {existing.GetName().Version} available in the default load context.");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error(ex, "Could not load Harmony; hard-coded Wrath strings stay English.");
        }

        return Ready;
    }
}
