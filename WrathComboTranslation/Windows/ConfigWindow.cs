using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using WrathComboTranslation.Services;

namespace WrathComboTranslation.Windows;

internal sealed class ConfigWindow : Window
{
    private static readonly Vector4 Good = new(0.4f, 0.85f, 0.4f, 1f);
    private static readonly Vector4 Bad = new(0.9f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Muted = new(0.65f, 0.65f, 0.65f, 1f);

    private readonly OverlayInstaller _installer;
    private readonly Plugin _plugin;

    private IReadOnlyDictionary<string, int> _counts = new Dictionary<string, int>();
    private TranslationPack? _countsFor;

    public ConfigWindow(Plugin plugin, OverlayInstaller installer)
        : base("Wrath Combo Translation###WrathComboTranslationConfig")
    {
        _plugin = plugin;
        _installer = installer;

        Size = new Vector2(480, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        DrawStatus();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawSettings();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawCoverage();
    }

    private void DrawStatus()
    {
        ImGui.TextUnformatted("Status");
        ImGui.Spacing();

        Row("Wrath Combo", _plugin.WrathFound, _plugin.WrathFound ? "loaded" : "not loaded");
        Row("Overlay", _installer.Installed,
            _installer.Installed
                ? $"active on {_installer.Patched.Count} resource files"
                : "inactive");

        ImGui.TextUnformatted("Strings loaded:");
        ImGui.SameLine();
        ImGui.TextColored(_plugin.Pack.StringCount > 0 ? Good : Bad,
            _plugin.Pack.StringCount.ToString());

        if (_installer.Installed)
        {
            ImGui.TextUnformatted("Lookups served:");
            ImGui.SameLine();
            ImGui.TextColored(Muted, _installer.OverrideHits.ToString());
        }

        ImGui.TextUnformatted("Dalamud UI language:");
        ImGui.SameLine();
        ImGui.TextColored(Muted, Plugin.PluginInterface.UiLanguage);

        if (!_plugin.Config.ForceLanguage &&
            !string.Equals(Plugin.PluginInterface.UiLanguage, _plugin.Pack.TwoLetterCode,
                StringComparison.OrdinalIgnoreCase))
        {
            ImGui.Spacing();
            ImGui.TextColored(Bad,
                $"Dalamud is set to '{Plugin.PluginInterface.UiLanguage}', so the " +
                $"'{_plugin.Config.Language}' pack is not being applied.");
            ImGui.TextColored(Muted, "Change Dalamud's language, or enable \"Force language\" below.");
        }

        foreach (var source in _plugin.Pack.Sources)
            ImGui.TextColored(Muted, $"Source: {source}");

        return;

        static void Row(string label, bool ok, string value)
        {
            ImGui.TextUnformatted($"{label}:");
            ImGui.SameLine();
            ImGui.TextColored(ok ? Good : Bad, value);
        }
    }

    private void DrawSettings()
    {
        ImGui.TextUnformatted("Settings");
        ImGui.Spacing();

        var enabled = _plugin.Config.Enabled;
        if (ImGui.Checkbox("Enable translation overlay", ref enabled))
            _plugin.SetEnabled(enabled);

        var languages = TranslationPack.AvailableLanguages(_plugin.LooseRoot);
        var current = _plugin.Config.Language;

        ImGui.SetNextItemWidth(160);
        if (ImGui.BeginCombo("Language pack", current))
        {
            foreach (var language in languages)
                if (ImGui.Selectable(language, language == current))
                {
                    _plugin.Config.Language = language;
                    _plugin.ReloadPack();
                }

            ImGui.EndCombo();
        }

        var force = _plugin.Config.ForceLanguage;
        if (ImGui.Checkbox("Force language (ignore Dalamud's UI language)", ref force))
        {
            _plugin.Config.ForceLanguage = force;
            _plugin.ApplyForceSetting();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Applies the pack no matter which language Dalamud is set to.\n" +
                "Use this if you run Dalamud in English but want Wrath translated.");

        ImGui.Spacing();

        if (ImGui.Button("Reload translations"))
            _plugin.ReloadPack();

        ImGui.SameLine();
        if (ImGui.Button("Open translation folder"))
            _plugin.OpenLooseFolder();

        ImGui.TextColored(Muted,
            "Edit the JSON files in that folder and hit Reload - no restart needed.");

        ImGui.Spacing();

        if (ImGui.Button("Refresh Wrath's cached strings"))
            _plugin.RefreshWrathStrings();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Text Wrath had already drawn before this plugin loaded keeps its original\n" +
                "wording until Wrath rebuilds its caches. This forces that rebuild.\n\n" +
                "It is not done automatically: Wrath re-reads every job, action and status\n" +
                "name from the game's data files, which can stall for a moment.");
    }

    private void DrawCoverage()
    {
        ImGui.TextUnformatted("Coverage");
        ImGui.Spacing();

        if (!_installer.Installed)
        {
            ImGui.TextColored(Muted, "Waiting for Wrath Combo to load.");
            return;
        }

        // Rebuilt only when the pack changes: this runs every frame the window is open.
        if (!ReferenceEquals(_countsFor, _plugin.Pack))
        {
            _countsFor = _plugin.Pack;
            _counts = _plugin.Pack.FileCounts;
        }

        var counts = _counts;

        if (!ImGui.BeginTable("##coverage", 2,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
            return;

        ImGui.TableSetupColumn("Resource file", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Strings", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var name in _installer.KnownResourceNames)
        {
            var translated = counts.TryGetValue(name, out var n) ? n : 0;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(name);
            ImGui.TableNextColumn();
            ImGui.TextColored(translated > 0 ? Good : Muted, translated.ToString());
        }

        ImGui.EndTable();
    }
}
