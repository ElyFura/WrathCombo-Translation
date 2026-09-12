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

    /// <summary>
    ///     Locates the loaded Wrath Combo assembly, or null if it is not (yet) loaded.
    /// </summary>
    /// <remarks>
    ///     Dalamud loads every plugin into its own <see cref="AssemblyLoadContext" />, but all of
    ///     them live in the same process, so both lookups below can see across plugin boundaries.
    /// </remarks>
    public static Assembly? FindWrathAssembly()
    {
        var fromDomain = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(IsWrath);
        if (fromDomain is not null)
            return fromDomain;

        // Fallback: walk the load contexts directly, in case the domain view is stale.
        return AssemblyLoadContext.All
            .SelectMany(ctx =>
            {
                try { return ctx.Assemblies; }
                catch { return []; }
            })
            .FirstOrDefault(IsWrath);

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
    ///     Discovery is structural rather than a hard-coded list, so resource files added to
    ///     Wrath in future releases are picked up without a change here.
    /// </remarks>
    public static IReadOnlyList<WrathResourceClass> DiscoverResourceClasses(Assembly wrath)
    {
        var found = new List<WrathResourceClass>();

        foreach (var type in SafeGetTypes(wrath))
        {
            // Generated resource classes are non-generic, non-public and carry a private
            // static ResourceManager field named 'resourceMan'.
            if (type.IsGenericTypeDefinition)
                continue;

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
    ///     resource class at the current culture and drops Wrath's own string caches.
    ///     Without this, strings resolved before the overlay was installed stay stale.
    /// </summary>
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

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not enumerate Wrath Combo's types.");
            return [];
        }
    }
}
