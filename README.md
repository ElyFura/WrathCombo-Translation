# Wrath Combo Translation

A separate Dalamud plugin that translates [Wrath Combo](https://github.com/PunishXIV/WrathCombo)'s
interface. Wrath Combo itself is **not modified** — neither its source nor its installed files.

## How it works

Wrath keeps its UI strings in ~34 generated `.resx` resource classes. Each one holds its
`ResourceManager` in a private static field, and every string Wrath shows goes through that
manager — both the direct property reads (`MainWindowUI.Button_About`) and the
`Text.GetLocalizedString` path used for presets and settings.

This plugin finds Wrath's assembly in the process, replaces each of those managers with a
subclass that checks a translation pack first, and falls back to Wrath's own resources for
anything the pack does not cover. Unloading the plugin puts the original managers back.

Because the resource classes are discovered structurally rather than from a hard-coded list,
resource files added in future Wrath releases are picked up automatically.

### Why not the obvious alternatives

- **Replacing the satellite assembly** (`de/WrathCombo.resources.dll`) breaks on every Wrath
  update, since the install folder is version-stamped, and new languages cannot be resolved
  at all because they are absent from Wrath's `deps.json`.
- **Contributing upstream via Crowdin** is the right long-term path, but its config currently
  covers only `CustomComboPresets.resx`, and it ties releases to Wrath's own cycle.

## Using it

Open the settings with `/wrathtl`.

The overlay only applies when Dalamud's UI language matches the pack, so a `de` pack needs
Dalamud set to German. Players who run Dalamud in English can tick **Force language**.

Translations live in two places, merged with loose files winning per key:

- **Embedded** in the plugin — what ships by default.
- **Loose**, under `%appdata%\XIVLauncher\pluginConfigs\WrathComboTranslation\packs\<lang>\`.
  Edit the JSON there and press **Reload translations**; no restart needed.

Each file is flat `{ "ResourceKey": "Übersetzung" }`, named after the Wrath resource
(`MainWindowUI.json`, `CustomComboPresets.json`, ...). A key that is absent falls back to
Wrath's own string, so a partial translation degrades to English rather than showing blanks.
Keys starting with `_` are metadata (translator credits and notes) and are never displayed.

## Translation status

German, against 4570 translatable source strings:

| Source | Strings |
| --- | --- |
| Shipped by Wrath itself (`CustomComboPresets.de.resx`) | 474 |
| This plugin | 232 |
| **Untranslated** | **3864** |

Wrath does ship `AutoRotationUI.de.resx`, `SettingsUI.de.resx` and `BST_Config.de.resx`, but
all three are empty Crowdin placeholders, so the UI chrome is effectively untranslated upstream.

The bundled German pack covers the main window, the features pane, the generic labels and
all 45 entries of the settings pane (`SettingsCfgUI`, 183 of 198 strings).

Some strings are deliberately left to fall back rather than translated: in-game proper nouns
(Bozja, Occult Crescent, Variant Dungeons) until the official German client wording is
confirmed, purely numeric defaults and colour codes that read identically in German, and the
target-stack listings, whose entry names come from the still-untranslated `Generics.resx`.

The bulk of the remaining work is `CustomComboPresets` (3886 strings — every feature name and
description), followed by `AutoRotationUI` (88) and `Generics` (165).

## Development

```
dotnet build WrathComboTranslation/WrathComboTranslation.csproj -c Release -p:Platform=x64
```

A `Debug` build drops straight into `%appdata%\XIVLauncher\devPlugins\WrathComboTranslation\`.

### Tools

`resx-extract` reads a Wrath Combo checkout and maintains the translation files:

```
# Refresh source/ with the English strings translators work from
dotnet run --project tools/ResxExtract -- extract \
    --repo ../WrathCombo --out source

# Coverage, and keys in the pack that no longer exist upstream
dotnet run --project tools/ResxExtract -- report \
    --repo ../WrathCombo --pack WrathComboTranslation/Translations/de --lang de
```

Run `report` after every Wrath update: it lists stale keys that Wrath has since removed or
renamed.

`overlay-harness` verifies the overlay against a real build of Wrath without starting the
game. It loads both DLLs into separate `AssemblyLoadContext`s the way Dalamud does, then
checks that the overlay installs, translates, falls back, honours the culture setting, and
fully restores on uninstall:

```
dotnet run --project tools/OverlayHarness -c Release -p:Platform=x64 -- \
    ../WrathCombo/WrathCombo/bin/Release/WrathCombo.dll \
    WrathComboTranslation/bin/Release/WrathComboTranslation.dll
```

Run it after any change to the overlay, and after Wrath updates — it is what catches Wrath
changing its localization internals.

## Caveats

- The overlay depends on Wrath's generated resource classes keeping their standard shape. If
  a Wrath update changes that, the plugin logs a warning and stays inert rather than breaking
  Wrath; the harness will say so too.
- Strings Wrath resolved before this plugin loaded are refreshed by invoking its
  `Text.OnLanguageChanged`, which drops its internal caches. If that method is ever renamed,
  translations apply from the next language change instead of immediately.
