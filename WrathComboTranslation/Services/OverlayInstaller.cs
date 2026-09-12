using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace WrathComboTranslation.Services;

/// <summary>
///     Swaps Wrath Combo's generated <c>ResourceManager</c> instances for
///     <see cref="OverlayResourceManager" />s and puts the originals back on unload.
/// </summary>
/// <remarks>
///     Wrath is not modified on disk and needs no cooperation: its generated resource
///     classes hold their manager in a private static field, which reflection can replace
///     in place. Every string Wrath resolves afterwards — both the 180-odd direct
///     <c>MainWindowUI.Foo</c> property reads and the <c>Text.GetLocalizedString</c> path —
///     goes through the replacement.
/// </remarks>
internal sealed class OverlayInstaller : IDisposable
{
    private readonly List<WrathResourceClass> _patched = [];

    private Assembly? _wrath;
    private bool _disposed;

    /// <summary>Whether the overlay is currently installed into a loaded Wrath Combo.</summary>
    public bool Installed => _patched.Count > 0;

    /// <summary>Resource classes currently overlaid.</summary>
    public IReadOnlyList<WrathResourceClass> Patched => _patched;

    /// <summary>Resource short names Wrath exposes, whether or not they are translated.</summary>
    public IReadOnlyList<string> KnownResourceNames
        => [.. _patched.Select(p => p.ShortName).OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>How many lookups the pack has answered since installation.</summary>
    public int OverrideHits
        => _patched.Sum(p => (p.CurrentManager as OverlayResourceManager)?.OverrideHits ?? 0);

    /// <summary>
    ///     Whether the Wrath assembly we patched is still the one loaded. Reloading Wrath as a
    ///     dev plugin produces a fresh assembly, which needs a fresh install.
    /// </summary>
    public bool NeedsReinstall(Assembly? current)
        => Installed && !ReferenceEquals(_wrath, current);

    /// <summary>
    ///     Installs the overlay against <paramref name="wrath" />, replacing any previous
    ///     installation. Returns the number of resource classes patched.
    /// </summary>
    public int Install(Assembly wrath, TranslationPack pack, string uiLanguage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Uninstall();

        var classes = WrathLocator.DiscoverResourceClasses(wrath);
        if (classes.Count == 0)
        {
            Log.Warning("Wrath Combo is loaded but no localization classes were found; " +
                        "its internals may have changed.");
            return 0;
        }

        foreach (var cls in classes)
        {
            try
            {
                var overlay = new OverlayResourceManager(cls.BaseName, wrath, cls.ShortName, pack);
                cls.ManagerField.SetValue(null, overlay);
                _patched.Add(cls);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Failed to overlay {cls.BaseName}.");
            }
        }

        _wrath = wrath;

        // Wrath caches resolved strings (presets, settings, job names). Those caches were
        // filled with untranslated text before we got here, so they have to be dropped.
        WrathLocator.TryInvalidateWrathCaches(wrath, uiLanguage);

        Log.Information(
            $"Translation overlay installed on {_patched.Count} resource classes " +
            $"({pack.StringCount} strings, language '{pack.Language}').");

        return _patched.Count;
    }

    /// <summary>
    ///     Rebuilds the overlay with a different pack, keeping the same Wrath assembly.
    /// </summary>
    public void Reload(TranslationPack pack, string uiLanguage)
    {
        if (_wrath is null)
            return;

        Install(_wrath, pack, uiLanguage);
    }

    /// <summary>
    ///     Restores Wrath's original managers. Safe to call when nothing is installed.
    /// </summary>
    public void Uninstall()
    {
        if (_patched.Count == 0)
            return;

        foreach (var cls in _patched)
        {
            try
            {
                cls.ManagerField.SetValue(null, cls.OriginalManager);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Failed to restore {cls.BaseName}; " +
                              "Wrath may keep showing translated text until it reloads.");
            }
        }

        var wrath = _wrath;
        var count = _patched.Count;
        _patched.Clear();
        _wrath = null;

        // Drop the caches again so the original strings come back immediately.
        if (wrath is not null)
            WrathLocator.TryInvalidateWrathCaches(wrath, Plugin.PluginInterface?.UiLanguage ?? "en");

        Log.Information($"Translation overlay removed from {count} resource classes.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Uninstall();
        _disposed = true;
    }
}
