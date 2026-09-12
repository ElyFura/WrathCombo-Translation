using System;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace WrathComboTranslation.Services;

/// <summary>
///     Drop-in replacement for a generated class's <see cref="ResourceManager" />.
///     Consults the loaded translation pack first and otherwise behaves exactly like the
///     manager it replaced, so untranslated keys keep falling back through Wrath's own
///     resx chain (its partial <c>.de</c>/<c>.ja</c>/... files, then English).
/// </summary>
/// <remarks>
///     Deriving from <see cref="ResourceManager" /> with the same base name and assembly is
///     what makes this transparent: Wrath's generated properties call the virtual
///     <see cref="GetString(string, CultureInfo)" />, and every other member is inherited
///     unchanged.
/// </remarks>
internal sealed class OverlayResourceManager : ResourceManager
{
    private readonly TranslationPack _pack;

    /// <summary>Short resource name, e.g. <c>MainWindowUI</c>, used to index the pack.</summary>
    private readonly string _shortName;

    public OverlayResourceManager(string baseName, Assembly assembly, string shortName, TranslationPack pack)
        : base(baseName, assembly)
    {
        _shortName = shortName;
        _pack = pack;
    }

    /// <summary>Number of lookups this manager answered from the pack. Diagnostics only.</summary>
    public int OverrideHits { get; private set; }

    public override string? GetString(string name, CultureInfo? culture)
    {
        if (_pack.TryGet(_shortName, name, culture, out var translated))
        {
            OverrideHits++;
            return translated;
        }

        return base.GetString(name, culture);
    }

    public override string? GetString(string name)
        => GetString(name, null);

    public override object? GetObject(string name, CultureInfo? culture)
    {
        // Wrath only stores strings, but a caller going through GetObject should see
        // the same overridden value rather than the untranslated original.
        if (_pack.TryGet(_shortName, name, culture, out var translated))
        {
            OverrideHits++;
            return translated;
        }

        return base.GetObject(name, culture);
    }

    public override object? GetObject(string name)
        => GetObject(name, null);
}
