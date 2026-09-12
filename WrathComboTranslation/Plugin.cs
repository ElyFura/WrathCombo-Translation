using System;
using System.IO;
using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using WrathComboTranslation.Services;
using WrathComboTranslation.Windows;

namespace WrathComboTranslation;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/wrathtl";

    /// <summary>How often to look for Wrath Combo while waiting for it to load.</summary>
    private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(2);

    /// <summary>How often to re-check that the Wrath we patched is still the loaded one.</summary>
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(10);

    private readonly OverlayInstaller _installer = new();
    private readonly WindowSystem _windows = new("WrathComboTranslation");
    private readonly ConfigWindow _configWindow;

    private DateTime _nextCheck = DateTime.MinValue;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Plugin>();

        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        LooseRoot = Path.Combine(PluginInterface.GetPluginConfigDirectory(), TranslationPack.LooseFolderName);
        EnsureLooseRoot();

        Pack = LoadPack();

        _configWindow = new ConfigWindow(this, _installer);
        _windows.AddWindow(_configWindow);

        PluginInterface.UiBuilder.Draw += _windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfig;
        Framework.Update += OnFrameworkUpdate;

        Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Wrath Combo translation settings.",
        });
    }

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;

    internal Configuration Config { get; }

    /// <summary>Directory holding user-editable translation files, one folder per language.</summary>
    internal string LooseRoot { get; }

    /// <summary>Currently loaded strings. Replaced wholesale by <see cref="ReloadPack" />.</summary>
    internal TranslationPack Pack { get; private set; }

    /// <summary>Set when Wrath Combo could not be found, for display in the status window.</summary>
    internal bool WrathFound { get; private set; }

    public void Dispose()
    {
        Commands.RemoveHandler(CommandName);
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= _windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfig;

        _windows.RemoveAllWindows();

        // Hands Wrath its own resource managers back, so unloading this plugin leaves no trace.
        _installer.Dispose();
    }

    /// <summary>
    ///     Re-reads translation files from disk and re-applies them. Called from the config
    ///     window after the user edits a pack or switches language.
    /// </summary>
    internal void ReloadPack()
    {
        Pack = LoadPack();

        if (!Config.Enabled)
        {
            _installer.Uninstall();
            return;
        }

        if (_installer.Installed)
            _installer.Reload(Pack, PluginInterface.UiLanguage);
        else
            _nextCheck = DateTime.MinValue; // try to install on the next tick

        Config.Save();
    }

    /// <summary>
    ///     Applies the current <see cref="Configuration.ForceLanguage" /> setting without
    ///     rebuilding the pack, and drops Wrath's caches so the change is visible at once.
    /// </summary>
    internal void ApplyForceSetting()
    {
        Pack.Forced = Config.ForceLanguage;

        if (_installer.Installed)
            _installer.Reload(Pack, PluginInterface.UiLanguage);

        Config.Save();
    }

    internal void SetEnabled(bool enabled)
    {
        Config.Enabled = enabled;
        Config.Save();

        if (enabled)
            _nextCheck = DateTime.MinValue;
        else
            _installer.Uninstall();
    }

    internal void OpenLooseFolder()
    {
        try
        {
            var dir = Path.Combine(LooseRoot, Config.Language);
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not open the translation folder.");
        }
    }

    private TranslationPack LoadPack()
    {
        var pack = TranslationPack.Load(Config.Language, LooseRoot);
        pack.Forced = Config.ForceLanguage;
        return pack;
    }

    private void EnsureLooseRoot()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(LooseRoot, Config.Language));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, $"Could not create the translation folder at '{LooseRoot}'.");
        }
    }

    /// <summary>
    ///     Waits for Wrath Combo to appear, installs the overlay, and afterwards keeps an eye
    ///     on whether the assembly we patched is still the loaded one.
    /// </summary>
    /// <remarks>
    ///     Plugin load order is not something we control, so this polls instead of assuming
    ///     Wrath is already present when we start.
    /// </remarks>
    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now < _nextCheck)
            return;

        if (!Config.Enabled)
        {
            _nextCheck = now + HealthInterval;
            return;
        }

        Assembly? wrath;
        try
        {
            wrath = WrathLocator.FindWrathAssembly();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed while looking for Wrath Combo.");
            _nextCheck = now + HealthInterval;
            return;
        }

        WrathFound = wrath is not null;

        if (wrath is null)
        {
            _installer.Uninstall();
            _nextCheck = now + SearchInterval;
            return;
        }

        if (_installer.Installed && !_installer.NeedsReinstall(wrath))
        {
            _nextCheck = now + HealthInterval;
            return;
        }

        try
        {
            _installer.Install(wrath, Pack, PluginInterface.UiLanguage);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to install the translation overlay.");
        }

        _nextCheck = now + HealthInterval;
    }

    private void OnCommand(string command, string args) => OpenConfig();

    private void OpenConfig() => _configWindow.Toggle();
}
