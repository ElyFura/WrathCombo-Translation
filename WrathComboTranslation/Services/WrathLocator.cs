using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Runtime.Loader;

namespace WrathComboTranslation.Services;

/// <summary>
///     A single generated resource class inside Wrath Combo
///     (e.g. <c>WrathCombo.Resources.Localization.UI.MainWindow.MainWindowUI</c>),
///     together with the reflection handles needed to swap its ResourceManager.
/// </summary>
internal sealed class WrathResourceClass
{
    /// <summary>Short name used as the translation file name, e.g. <c>MainWindowUI</c>.</summary>
    public required string ShortName { get; init; }

    /// <summary>Resource base name, e.g. <c>WrathCombo.Resources.Localization.UI.MainWindow.MainWindowUI</c>.</summary>
    public required string BaseName { get; init; }

    public required Type Type { get; init; }

    /// <summary>The private static <c>resourceMan</c> field of the generated class.</summary>
    public required FieldInfo ManagerField { get; init; }

    /// <summary>The private static <c>resourceCulture</c> field, used to read back the culture Wrath selected.</summary>
    public required FieldInfo? CultureField { get; init; }

    /// <summary>The manager that was in place before we touched anything.</summary>
    public required ResourceManager OriginalManager { get; init; }

    public ResourceManager? CurrentManager => ManagerField.GetValue(null) as ResourceManager;

    public CultureInfo? CurrentCulture => CultureField?.GetValue(null) as CultureInfo;
}

/// <summary>
///     Finds Wrath Combo inside the current process and reflects over its generated
///     localization classes. Nothing here mutates Wrath — see <see cref="OverlayInstaller" />.
/// </summary>
internal static class WrathLocator
{
    private const string WrathAssemblyName = "WrathCombo";
    private const string ManagerFieldName = "resourceMan";
    private const string CultureFieldName = "resourceCulture";
    private const string ManagerPropertyName = "ResourceManager";
    private const string ResourceSuffix = ".resources";

    /// <summary>
    ///     Locates the live Wrath Combo assembly, or null if it is not (yet) loaded.
    /// </summary>
    /// <remarks>
    ///     Dalamud loads every plugin into its own <see cref="AssemblyLoadContext" />, but all
    ///     of them live in the same process, so the lookups below see across plugin boundaries.
    ///     <br /><br />
    ///     Disabling and re-enabling Wrath gives it a brand new load context while the previous
    ///     one lingers until the GC gets to it, so more than one WrathCombo can be present at
    ///     once. Patching the dead one installs cleanly and then serves nothing, which is
    ///     exactly what it looks like when the overlay silently stops working, so the most
    ///     recently loaded assembly wins. That ordering is a heuristic rather than a guarantee:
    ///     the runtime appends newly loaded assemblies, but does not promise to.
    /// </remarks>
    public static Assembly? FindWrathAssembly()
    {
        var candidates = FindWrathAssemblies();

        if (candidates.Count > 1)
            Log.Warning($"{candidates.Count} copies of Wrath Combo are loaded, which happens " +
                        "after it is disabled and re-enabled. Using the most recent one.");

        return candidates.Count == 0 ? null : candidates[^1];
    }

    /// <summary>
    ///     Every loaded assembly named WrathCombo, in load order.
    /// </summary>
    public static IReadOnlyList<Assembly> FindWrathAssemblies()
    {
        var found = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(IsWrath)
            .ToList();

        if (found.Count > 0)
            return found;

        // Fallback: walk the load contexts directly, in case the domain view is stale.
        return AssemblyLoadContext.All
            .SelectMany(ctx =>
            {
                try { return ctx.Assemblies; }
                catch { return []; }
            })
            .Where(IsWrath)
            .ToList();

        static bool IsWrath(Assembly a)
        {
            try { return string.Equals(a.GetName().Name, WrathAssemblyName, StringComparison.Ordinal); }
            catch { return false; }
        }
    }

    /// <summary>
    ///     Discovers every generated resource class in <paramref name="wrath" />.
    /// </summary>
    /// <remarks>
    ///     Candidates come from the assembly's embedded resource names rather than from
    ///     <c>GetTypes()</c>: the resx generator names each class exactly after its resource,
    ///     so <c>....MainWindowUI.resources</c> yields the type directly. Discovery stays
    ///     structural — resource files added in future Wrath releases are still picked up —
    ///     without forcing the runtime to load all several thousand of Wrath's combo types
    ///     just to find thirty-odd of them.
    /// </remarks>
    public static IReadOnlyList<WrathResourceClass> DiscoverResourceClasses(Assembly wrath)
    {
        var found = new List<WrathResourceClass>();

        foreach (var type in CandidateTypes(wrath))
        {
            // Generated resource classes carry a private static ResourceManager field
            // named 'resourceMan'.
            var managerField = type.GetField(ManagerFieldName,
                BindingFlags.NonPublic | BindingFlags.Static);
            if (managerField is null || managerField.FieldType != typeof(ResourceManager))
                continue;

            var managerProperty = type.GetProperty(ManagerPropertyName,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

            // Reading the property forces Wrath's own lazy initialisation, which is the only
            // reliable way to learn the base name the generator baked in.
            ResourceManager? original;
            try
            {
                original = managerProperty?.GetValue(null) as ResourceManager
                           ?? managerField.GetValue(null) as ResourceManager;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"Could not read ResourceManager of {type.FullName}.");
                continue;
            }

            if (original is null)
                continue;

            var baseName = original.BaseName;
            if (string.IsNullOrEmpty(baseName))
                continue;

            found.Add(new WrathResourceClass
            {
                ShortName = ShortNameOf(baseName),
                BaseName = baseName,
                Type = type,
                ManagerField = managerField,
                CultureField = type.GetField(CultureFieldName,
                    BindingFlags.NonPublic | BindingFlags.Static),
                OriginalManager = original,
            });
        }

        return found;
    }

    /// <summary>
    ///     Invokes <c>WrathCombo.Window.Text.OnLanguageChanged</c>, which re-points every
    ///     resource class at the current culture and drops Wrath's own string caches, so that
    ///     strings resolved before the overlay was installed are re-resolved.
    /// </summary>
    /// <remarks>
    ///     This is expensive and reaches deep into Wrath's internals, so it is never called
    ///     automatically — see <see cref="OverlayInstaller.Install" />. Clearing those caches
    ///     makes Wrath rebuild every job, action and status name, and each of those rebuilds
    ///     re-fetches a Lumina Excel sheet (Wrath passes an eagerly evaluated value to
    ///     <c>ConcurrentDictionary.GetOrAdd</c>, so the sheet is fetched even on a cache hit).
    ///     Triggering that from a plugin is not worth the risk by default; without it,
    ///     translations simply apply to strings resolved from here on, and fully on the next
    ///     language change.
    /// </remarks>
    public static bool TryInvalidateWrathCaches(Assembly wrath, string uiLanguage)
    {
        try
        {
            var textType = wrath.GetType("WrathCombo.Window.Text");
            var method = textType?.GetMethod("OnLanguageChanged",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
                [typeof(string)]);

            if (method is null)
            {
                Log.Warning("Wrath's Text.OnLanguageChanged was not found; " +
                            "already-cached strings will only update on the next language change.");
                return false;
            }

            method.Invoke(null, [uiLanguage]);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to invalidate Wrath's string caches.");
            return false;
        }
    }

    /// <summary>Last dotted segment of a resource base name.</summary>
    public static string ShortNameOf(string baseName)
    {
        var idx = baseName.LastIndexOf('.');
        return idx >= 0 && idx < baseName.Length - 1
            ? baseName[(idx + 1)..]
            : baseName;
    }

    /// <summary>
    ///     The types that could be generated resource classes, derived from the embedded
    ///     resource names so that only those few types are loaded.
    /// </summary>
    private static IEnumerable<Type> CandidateTypes(Assembly assembly)
    {
        string[] resourceNames;
        try
        {
            resourceNames = assembly.GetManifestResourceNames();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not read Wrath Combo's embedded resource names.");
            yield break;
        }

        foreach (var resourceName in resourceNames)
        {
            if (!resourceName.EndsWith(ResourceSuffix, StringComparison.Ordinal))
                continue;

            Type? type;
            try
            {
                type = assembly.GetType(resourceName[..^ResourceSuffix.Length],
                    throwOnError: false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"Could not resolve the type behind '{resourceName}'.");
                continue;
            }

            if (type is null)
            {
                // A resource whose class will not resolve means this assembly is not in a
                // healthy state - typically a leftover copy from a previous load whose context
                // is on its way out.
                Log.Warning($"'{resourceName}' has no matching type in the Wrath Combo assembly.");
                continue;
            }

            yield return type;
        }
    }
}
