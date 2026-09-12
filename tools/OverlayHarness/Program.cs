using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Runtime.Loader;

namespace OverlayHarness;

/// <summary>
///     Offline check that the translation overlay actually takes effect against a real
///     build of Wrath Combo, without starting the game.
/// </summary>
/// <remarks>
///     The two DLLs are loaded into separate <see cref="AssemblyLoadContext" />s, which is
///     how Dalamud isolates plugins from each other. That isolation is the whole reason this
///     harness exists: the overlay only works if the plugin can find and patch types living
///     in a different load context, and that is easy to get wrong in a way no unit test on a
///     single assembly would catch.
/// </remarks>
internal static class Program
{
    private static int _failures;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: overlay-harness <WrathCombo.dll> <WrathComboTranslation.dll> [dalamudLibDir]");
            return 1;
        }

        var wrathPath = Path.GetFullPath(args[0]);
        var translationPath = Path.GetFullPath(args[1]);
        var dalamudDir = args.Length > 2
            ? Path.GetFullPath(args[2])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncher", "addon", "Hooks", "dev");

        var wrathAlc = new ProbingLoadContext("wrath", wrathPath, dalamudDir);
        var transAlc = new ProbingLoadContext("translation", translationPath, dalamudDir);

        var wrath = wrathAlc.LoadFromAssemblyPath(wrathPath);
        var translation = transAlc.LoadFromAssemblyPath(translationPath);

        Console.WriteLine($"Loaded {wrath.GetName().Name} into ALC '{wrathAlc.Name}'");
        Console.WriteLine($"Loaded {translation.GetName().Name} into ALC '{transAlc.Name}'");
        Console.WriteLine();

        var locator = Internal(translation, "WrathComboTranslation.Services.WrathLocator");
        var packType = Internal(translation, "WrathComboTranslation.Services.TranslationPack");
        var installerType = Internal(translation, "WrathComboTranslation.Services.OverlayInstaller");

        // 1. Cross-context discovery.
        var found = (Assembly?)Invoke(locator, "FindWrathAssembly", null, []);
        Check("locator finds Wrath across load contexts", ReferenceEquals(found, wrath));

        // 2. Pack loading from embedded resources.
        var pack = Invoke(packType, "Load", null, ["de", Path.Combine(Path.GetTempPath(), "wctl-harness-empty")])!;
        var stringCount = (int)Get(packType, "StringCount", pack)!;
        Check($"embedded 'de' pack loaded ({stringCount} strings)", stringCount > 0);

        // 3. Wrath's own resource lookup, before anything is patched.
        var mainWindowUi = Internal(wrath, "WrathCombo.Resources.Localization.UI.MainWindow.MainWindowUI");
        SetCulture(mainWindowUi, new CultureInfo("de"));

        var english = ReadString(mainWindowUi, "Button_About");
        Check($"baseline reads Wrath's own string (\"{english}\")", english == "About");

        // 4. Install the overlay and read again.
        // invalidateWrathCaches stays false: making Wrath drop its caches needs a live game,
        // so that path cannot be covered here — which is exactly why it is opt-in.
        var installer = Activator.CreateInstance(installerType)!;
        var patched = (int)Invoke(installerType, "Install", installer, [wrath, pack, "de", false])!;
        Check($"overlay patched {patched} resource classes", patched > 0);
        Check("all 34 of Wrath's resource classes were found", patched == 34);

        var translated = ReadString(mainWindowUi, "Button_About");
        Check($"translated string served (\"{translated}\")", translated == "Über");

        // 5. A key the pack does not cover must still fall through to Wrath.
        var untouched = ReadString(mainWindowUi, "Wrath_Combo");
        Check($"untranslated key falls back (\"{untouched}\")", untouched == "Wrath Combo");

        // 6. Wrath's Text.GetLocalizedString path, used for presets and settings.
        var manager = (ResourceManager?)Get(mainWindowUi, "ResourceManager", null);
        var viaManager = manager?.GetString("Button_Settings", new CultureInfo("de"));
        Check($"ResourceManager path served (\"{viaManager}\")", viaManager == "Einstellungen");

        // 7. A non-matching culture must not be translated while 'force' is off.
        var french = manager?.GetString("Button_About", new CultureInfo("fr"));
        Check($"non-matching culture untouched (\"{french}\")", french == "About");

        // 8. ... unless the user forces the language.
        Set(packType, "Forced", pack, true);
        var forced = manager?.GetString("Button_About", new CultureInfo("fr"));
        Check($"forced language overrides any culture (\"{forced}\")", forced == "Über");
        Set(packType, "Forced", pack, false);

        // 9. The settings pane is reached through Text.GetLocalizedString rather than a
        //    generated property, so exercise that resource class separately.
        var settingsCfg = Internal(wrath, "WrathCombo.Resources.Localization.UI.Settings.SettingsCfgUI");
        var settingsManager = (ResourceManager?)Get(settingsCfg, "ResourceManager", null);
        var de = new CultureInfo("de");

        var settingName = settingsManager?.GetString("ActionChanging_Name", de);
        Check($"settings name translated (\"{settingName}\")", settingName == "Aktionen ersetzen");

        var category = settingsManager?.GetString("TargetingOptions_Category", de);
        Check($"settings category translated (\"{category}\")", category == "Ziel-Optionen");

        // Numeric defaults are deliberately left out of the pack and must fall through.
        var numericDefault = settingsManager?.GetString("Throttle_defaultValue", de);
        Check($"numeric default falls back (\"{numericDefault}\")", numericDefault == "50");

        // 10. Uninstalling must leave Wrath exactly as it was.
        Invoke(installerType, "Uninstall", installer, []);
        var restored = ReadString(mainWindowUi, "Button_About");
        Check($"uninstall restores the original ({restored})", restored == "About");

        var restoredManager = Get(mainWindowUi, "ResourceManager", null);
        Check("uninstall restores the original ResourceManager type",
            restoredManager?.GetType().FullName == typeof(ResourceManager).FullName);

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "All checks passed."
            : $"{_failures} check(s) FAILED.");

        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Reads a generated resource property, which is what Wrath's UI code does.</summary>
    private static string? ReadString(Type resourceClass, string property)
        => Get(resourceClass, property, null) as string;

    /// <summary>Sets the generated class's culture field, as Wrath's Text.OnLanguageChanged would.</summary>
    private static void SetCulture(Type resourceClass, CultureInfo culture)
        => resourceClass
            .GetField("resourceCulture", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, culture);

    private static Type Internal(Assembly assembly, string fullName)
        => assembly.GetType(fullName, throwOnError: true)!;

    private static object? Invoke(Type type, string method, object? instance, object?[] args)
    {
        var mi = type.GetMethod(method,
                     BindingFlags.Public | BindingFlags.NonPublic |
                     BindingFlags.Static | BindingFlags.Instance)
                 ?? throw new MissingMethodException(type.FullName, method);

        try
        {
            return mi.Invoke(instance, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static object? Get(Type type, string property, object? instance)
        => type.GetProperty(property,
                   BindingFlags.Public | BindingFlags.NonPublic |
                   BindingFlags.Static | BindingFlags.Instance)
               ?.GetValue(instance)
           ?? throw new MissingMemberException(type.FullName, property);

    private static void Set(Type type, string property, object? instance, object? value)
        => type.GetProperty(property,
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance)!
            .SetValue(instance, value);

    private static void Check(string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok)
            _failures++;
    }

    /// <summary>
    ///     Load context that resolves a plugin's own dependencies first and then falls back to
    ///     Dalamud's runtime folder, mimicking how Dalamud hosts plugin assemblies.
    /// </summary>
    private sealed class ProbingLoadContext(string name, string mainAssembly, string dalamudDir)
        : AssemblyLoadContext(name, isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);
        private readonly string _ownDir = Path.GetDirectoryName(mainAssembly)!;

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
            if (resolved is not null && File.Exists(resolved))
                return LoadFromAssemblyPath(resolved);

            foreach (var dir in new[] { _ownDir, dalamudDir })
            {
                var candidate = Path.Combine(dir, assemblyName.Name + ".dll");
                if (File.Exists(candidate))
                    return LoadFromAssemblyPath(candidate);
            }

            // Let the default context supply framework assemblies.
            return null;
        }
    }
}
