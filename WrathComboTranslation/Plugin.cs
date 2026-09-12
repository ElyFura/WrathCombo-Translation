using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
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

    /// <summary>
    ///     Installs to attempt before giving up for good. A repeatedly failing install would
    ///     otherwise retry for the whole session, and whatever made it fail would be doing
    ///     its damage on every attempt.
    /// </summary>
    private const int MaxInstallAttempts = 3;

    private readonly OverlayInstaller _installer = new();
    private readonly WindowSystem _windows = new("WrathComboTranslation");
    private readonly ConfigWindow _configWindow;

    private DateTime _nextCheck = DateTime.MinValue;
    private int _installAttempts;

    /// <remarks>
    ///     Services arrive as constructor parameters, which Dalamud injects when it builds the
    ///     plugin. Note that <c>IDalamudPluginInterface.Create&lt;T&gt;()</c> must never be
    ///     called with this class as its type argument: it constructs a new instance of the
    ///     type given, so calling it here would build another Plugin, which would call it
    ///     again, recursing until the process runs out of memory.
    /// </remarks>
    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        IFramework framework,
        ICommandManager commands)
    {
        PluginInterface = pluginInterface;
        Log = log;
        Framework = framework;
        Commands = commands;

        if (Interlocked.Increment(ref _instanceCount) > 1)
            Log.Error($"Plugin has been constructed {_instanceCount} times; " +
                      "something is building it recursively.");

        Log.Information("Starting up.");

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

        Log.Information($"Ready. {Pack.StringCount} strings loaded for '{Config.Language}'.");
    }

    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    internal static IPluginLog Log { get; private set; } = null!;
    internal static IFramework Framework { get; private set; } = null!;
    internal static ICommandManager Commands { get; private set; } = null!;

    /// <summary>Guards against the plugin being constructed more than once.</summary>
    private static int _instanceCount;

    internal Configuration Config { get; }

    /// <summary>Directory holding user-editable translation files, one folder per language.</summary>
    internal string LooseRoot { get; }

    /// <summary>Currently loaded strings. Replaced wholesale by <see cref="ReloadPack" />.</summary>
    internal TranslationPack Pack { get; private set; }

    /// <summary>Set when Wrath Combo could not be found, for display in the status window.</summary>
    internal bool WrathFound { get; private set; }

    /// <summary>
    ///     How many WrathCombo assemblies are loaded. More than one means Wrath was re-enabled
    ///     and its previous copy has not been collected yet, which is worth showing because
    ///     patching the wrong copy looks identical to the overlay simply not working.
    /// </summary>
    internal int WrathCopies { get; private set; }

    /// <summary>Set once installation has failed too often to keep retrying.</summary>
    internal bool GaveUp { get; private set; }

    public void Dispose()
    {
        Commands.RemoveHandler(CommandName);
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= _windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfig;

        _windows.RemoveAllWindows();
        Interlocked.Decrement(ref _instanceCount);

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
            ResumeSearching();

        Config.Save();
    }

    /// <summary>
    ///     Clears the give-up latch and puts the search back on the framework tick, so the
    ///     Reload button can recover a plugin that stopped trying.
    /// </summary>
    private void ResumeSearching()
    {
        _installAttempts = 0;
        GaveUp = false;
        _nextCheck = DateTime.MinValue;

        // Subscribing twice would double the work per tick; removing first is a no-op when
        // the handler is not attached.
        Framework.Update -= OnFrameworkUpdate;
        Framework.Update += OnFrameworkUpdate;
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
            ResumeSearching();
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
            WrathCopies = WrathLocator.FindWrathAssemblies().Count;
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

        if (_installAttempts >= MaxInstallAttempts)
        {
            GaveUp = true;
            Framework.Update -= OnFrameworkUpdate;
            Log.Error($"Giving up after {_installAttempts} failed attempts to install the " +
                      "translation overlay. Use the Reload button to try again.");
            return;
        }

        _installAttempts++;

        try
        {
            _installer.Install(wrath, Pack, PluginInterface.UiLanguage);
            _installAttempts = 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to install the translation overlay.");
        }

        _nextCheck = now + HealthInterval;
    }

    /// <summary>
    ///     Makes Wrath drop its cached strings so text it already displayed is re-resolved
    ///     through the overlay. Deliberately a manual action: it forces Wrath to rebuild every
    ///     job, action and status name, which is slow and reaches deep into its internals.
    /// </summary>
    internal void RefreshWrathStrings()
    {
        if (!_installer.Installed)
            return;

        try
        {
            _installer.Reload(Pack, PluginInterface.UiLanguage, invalidateWrathCaches: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh Wrath's cached strings.");
        }
    }

    private void OnCommand(string command, string args) => OpenConfig();

    private void OpenConfig() => _configWindow.Toggle();
}
