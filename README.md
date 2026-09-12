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
| This plugin | 3098 |
| **Untranslated** | **998** |

Wrath does ship `AutoRotationUI.de.resx`, `SettingsUI.de.resx` and `BST_Config.de.resx`, but
all three are empty Crowdin placeholders, so the UI chrome is effectively untranslated upstream.

The bundled German pack covers the whole interface outside of the feature list itself: the
main window, the features pane, the settings pane (`SettingsCfgUI`, 183 of 198), the
auto-rotation pane (`AutoRotationUI`, 87 of 88) and the shared building blocks reused across
every job's config (`Generics`, 164 of 165).

Some strings are deliberately left to fall back rather than translated: in-game proper nouns
(Bozja, Occult Crescent, Variant Dungeons) until the official German client wording is
confirmed, purely numeric defaults and colour codes that read identically in German, and the
target-stack listings, whose entry names come from the still-untranslated `Generics.resx`.

What remains is essentially one file: `CustomComboPresets`, 3886 strings covering every
feature name and description, of which Wrath itself already translates 474.

**Every preset name is now translated** - 1933 of 1942, worked through a role at a time so the
shared vocabulary stays consistent. Nine are left to fall back on purpose: six read identically
in German (Stotram, Exuviation, Tenri Jindo, Fuma Shuriken, Megaflare), and three cannot be
verified against the game's data - "Warden's Paeon", whose ability no longer exists in the
sheets, "Encore", and the phantom Oracle's "Cleansing", whose only row match translates to
something unrelated.

The descriptions are underway: the 327 outside the PvE job groups and the 359 for the four
tank jobs are done, leaving 1256.

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

Run `report` after every Wrath update and before shipping a pack. Besides coverage it lists
stale keys that Wrath has since removed, and validates each translation against its source:
it fails (exit 1) on a `{0}` placeholder that was added or dropped, which would throw a
FormatException in-game, and on a translated `###WidgetId` suffix, which would silently
detach an ImGui control from its state. It also points out strings left identical to the
English source.

`game-glossary` reads the installed client's data files with Lumina and writes an
English-to-German map of every player action, status, trait and job name:

```
dotnet run --project tools/GameGlossary -c Release -p:Platform=x64 --     --sqpack "<game>/game/sqpack" --out source/glossary.de.json --lang German
```

Wrath spells ability and status names out as literal English text in its preset descriptions
(its own sheet-lookup helper, `Text.ProcessSheetLookups`, is still a stub), and 2486 of the
3886 preset strings contain at least one such name. The glossary supplies the exact wording
the German client uses for them.

Use it as a reference while translating, never as a find-and-replace: the most frequent match
in the corpus is "Burst" (169 hits), which in Wrath's text nearly always means the rotational
burst window and not the status the game calls "Schub".

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

Point it at the **installed** Wrath as well, not only a repo build, since that is what players
actually run and the two differ:

```
dotnet run --project tools/OverlayHarness -c Release -p:Platform=x64 -- \
    "$APPDATA/XIVLauncher/installedPlugins/WrathCombo/<version>/WrathCombo.dll" \
    WrathComboTranslation/bin/Release/WrathComboTranslation.dll
```

How many resource classes exist depends on the build — the shipped 1.0.4.23 has 33, a current
repo build has 34 (`BST_Config`, added after that release was cut) — so the harness counts what
the assembly under test actually contains rather than expecting a fixed number.

## Caveats

- The overlay depends on Wrath's generated resource classes keeping their standard shape. If
  a Wrath update changes that, the plugin logs a warning and stays inert rather than breaking
  Wrath; the harness will say so too.
- Strings Wrath resolved before this plugin loaded are refreshed by invoking its
  `Text.OnLanguageChanged`, which drops its internal caches. If that method is ever renamed,
  translations apply from the next language change instead of immediately.
