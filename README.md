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

## Installing

Add this repository in Dalamud under **Settings → Experimental → Custom Plugin Repositories**:

```
https://raw.githubusercontent.com/ElyFura/WrathCombo-Translation/main/repo.json
```

Then install **Wrath Combo Translation** from the plugin installer. It needs Wrath Combo
itself, which it finds on its own once both are loaded - load order does not matter.

## Using it

Open the settings with `/wrathtl`. Its coverage section leads with the number that matters -
strings translated out of strings that exist - and lists each resource as translated-of-total.

Thirteen of Wrath's 33 resource files contain no strings at all: they are placeholders for
jobs whose options have not been written yet. Those are folded away by default, since listing
them made the table look like it was full of gaps. The checkbox brings them back.

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
| This plugin | 4535 |
| **Untranslated** | **34** |

Wrath does ship `AutoRotationUI.de.resx`, `SettingsUI.de.resx` and `BST_Config.de.resx`, but
all three are empty Crowdin placeholders, so the UI chrome is effectively untranslated upstream.

The bundled German pack covers the main window, the features pane, the settings pane
(`SettingsCfgUI`, 183 of 198), the auto-rotation pane (`AutoRotationUI`, 87 of 88) and the
shared building blocks reused across every job's config (`Generics`, 162 of 165).

The per-job config files (`AST_Config`, `BLM_Config`, `SAM_Config` and the rest), `SettingsUI`,
`OccultCrescent` and `MiscStrings` are done too - including the three text-to-speech lines in
`MiscStrings`, which are spoken aloud and so belong in the player's own language.

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

**The descriptions are done too**, all 1942 of them.

What is left is 34 strings, and the report lists every one. They fall into four groups:

- **Identical in German** (21): numeric defaults, a colour code, `Bozja`, `Normal`, `Tank`,
  `DPS`, `DOL`, `Soft Target`, `Wrath Combo`, `Auto-Rotation`, and ability names the client
  keeps as they are (Stotram, Exuviation, Tenri Jindo, Fuma Shuriken, Megaflare). An entry
  repeating the source would only count itself as translated.
- **Format-only** (3): `{0}`, `        - {0}`, and one empty string.
- **Unreachable** (0): the target-stack listings are now translated, but be aware that the
  stack widget shown beneath them in-game is not, and cannot be: Wrath builds it from
  hard-coded C# literals in `UserConfig.TargetDisplayNameFromPropertyName`, which no resource
  overlay reaches.
- **Unverifiable** (4): `Warden's Paeon`, whose ability no longer exists in the sheets; the
  phantom Oracle's `Cleansing`, which does not appear in that job's action list at all; and
  `Variant` / `Variant Dungeons`, for which the German client has no corresponding content
  name - it simply calls those dungeons "Die Unterstadt von Sil'dih" and so on.

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

`make-repo` writes the Dalamud repository listing from the manifest DalamudPackager produced,
so `repo.json` cannot fall behind the version in the csproj:

```
dotnet build WrathComboTranslation/WrathComboTranslation.csproj -c Release -p:Platform=x64
dotnet run --project tools/MakeRepo --     --manifest WrathComboTranslation/bin/Release/WrathComboTranslation/WrathComboTranslation.json     --zip WrathComboTranslation/bin/Release/WrathComboTranslation/latest.zip     --out repo.json --repo https://github.com/ElyFura/WrathCombo-Translation
```

Run it as part of cutting a release, and attach `latest.zip` to the GitHub release - the
listing points at `releases/latest/download/latest.zip`, so it keeps working across versions.
Dalamud offers an update only when `AssemblyVersion` in the listing is higher than the
installed one, which is exactly the field that rots when the listing is kept by hand.

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
