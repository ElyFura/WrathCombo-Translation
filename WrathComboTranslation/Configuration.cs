using Dalamud.Configuration;

namespace WrathComboTranslation;

internal sealed class Configuration : IPluginConfiguration
{
    /// <summary>Whether the overlay should be applied at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Language pack to load, e.g. <c>de</c>.</summary>
    public string Language { get; set; } = "de";

    /// <summary>
    ///     Apply the pack even when Dalamud's UI language is something else. Useful for players
    ///     who run Dalamud in English but want Wrath in their own language.
    /// </summary>
    public bool ForceLanguage { get; set; }

    public int Version { get; set; } = 1;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
