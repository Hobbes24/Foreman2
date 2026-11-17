# Working on Foreman2

A community fork of [Foreman](https://github.com/DanielKote/Foreman2), the Factorio production planner.
.NET Framework WinForms, one project, **x64 only**. See `README.md` for the feature log.

## Verification

**There are no automated tests.** `Foreman.sln` holds a single project; the Foreman 1.0-era `ForemanTest`
was removed because every one of its tests called a function that no longer exists. Nothing checks that a
change leaves the numbers alone.

So: **before reporting that a change to the solver, to `DataCache`, or to preset handling is verified, run
the harness and show its output.** Reading the diff and reasoning about it is not verification, and neither
is opening the GUI and looking at a graph.

```
tools\run-harness.ps1 -Preset "Factorio 2.1 Pyanodons 3.0"
```

It loads a preset through the real `DataCache` with no window and prints a deterministic, diffable report
(two runs of the same preset are byte-identical). To check a change:

```
tools\run-harness.ps1 -Preset X -Modules -OutFile before.txt
# rebuild, or re-import the preset in the GUI
tools\run-harness.ps1 -Preset X -Modules -OutFile after.txt
git diff --no-index before.txt after.txt
```

Presets come from `Foreman\bin\x64\<Configuration>\Presets\`, so build first. The harness copies what it
needs into a temp folder, which means the real `Presets\` folder is never read or written.

`-PresetDir <folder>` reads the preset from somewhere else instead. That is how to exercise a reader
against data no installed preset carries yet: hand-build a `.pjson` in a scratch folder and point the
harness at it, rather than editing a real preset or waiting on a Factorio re-import.

What it does *not* yet cover: solving a saved graph. Rates are what this application exists to compute and
nothing checks them. `Saved Graphs\` holds `Seablock chart.fjson` plus 13 more under `old\` — a ready-made
regression corpus, and Seablock is the case the solver's own `minRecipeOutRate` comment is written around.

## Building

Visual Studio MSBuild only:

```
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Foreman.sln /t:Rebuild
```

`dotnet build` fails with MSB3823/MSB3822 (non-string resources need `GenerateResourceUsePreserializedResources`).

Four traps, each of which produces a build that looks fine and then misbehaves:

- **Use `/t:Rebuild`, never `/t:Compile` as the last build before running.** `/t:Compile` skips resource
  preparation and a following incremental build treats the stale `.resources` as current. The exe then
  crashes on startup with "Could not find any resources appropriate for the specified culture". A healthy
  Debug exe is ~1700 KB; a resource-stripped one is ~1250 KB, so check the size.
- **Foreman must be closed**, or the copy from `obj` to `bin` fails with MSB3027 and you silently keep
  running the old exe. Ask for it to be closed rather than working around the lock.
- **Bump `$Targets` in `Foreman\Mods\build-tasklist-zips.ps1`** whenever the task-list mod's `control.lua`
  changes, or a rebuilt zip keeps its old version number and the game has no reason to prefer it. Compare
  byte size or timestamp, never the version number. Building is not deploying — the game reads
  `%APPDATA%\Factorio\mods\`, and nothing copies there automatically.
- Every solution platform maps to `x64`, so a bare build lands in `Foreman\bin\x64\Debug\`. The old
  `bin\Debug\` path is dead; an exe left there is a trap to launch by mistake.

## Conventions

- **File access goes through `SafeIO`, not `System.IO`.** A storage failure must never be fatal: an
  unreachable network share, a removed drive or a locked `user.config` degrades to a no-op or a fallback
  location. Use `SafeSettings.Save` rather than `Settings.Default.Save`.
- **Undo/redo is snapshot-based** — the whole graph is serialized through `ProductionGraph.GetObjectData` /
  `InsertNodesFromJson` before each destructive operation. New features need no per-feature undo work.
- **`TableLayoutPanel` forms must be edited in `Designer.cs` directly.** The Visual Studio designer mangles
  layout on forms with a filling `TableLayoutPanel`.
- Gutters are presentation-only, living in `PassthroughNodeElement`. They change how a passthrough node is
  drawn and where links attach, carry no rate semantics, and the solver never sees them.
- The helper mods Foreman deploys itself (`foremanexport`, `foremansavereader`) are stamped with the version
  of the Factorio install being launched. `Mods\*` is `CopyToOutputDirectory=Always`, so a build refreshes
  the deployed Lua.

## Graph layout is deliberate

Structure in saved graphs is intentional, not clutter. Boxed clusters and a passthrough that gathers a few
sources exist because that section will be built as one module in Factorio.

When proposing a graph tool, ask whether it **creates** or **rewrites**. Commands that only add links are
safe. Commands that move or delete existing links need explicit sign-off on the exact rules first, because
an existing link usually encodes a decision. Prefer opt-in scope: act on the selection, never the whole
graph. Never rearrange node positions.

## Don't drive the GUI

Do not drive Foreman's UI with synthetic mouse or keyboard input (`SetCursorPos`, `mouse_event`,
`SetForegroundWindow`, screenshot-and-click loops) without asking first and getting an explicit yes.
Authorization is per-occasion.

Default to observing from outside the process: read the code, measure components headlessly with the
harness above, or watch the process from a background PowerShell (CPU time vs wall clock,
`IsHungAppWindow`, `SendMessageTimeout(WM_NULL)`, thread wait reasons — CPU pegged means compute-bound,
CPU flat means blocked on a call). If a visual check is all that is needed, ask for it to be launched and
described.

Never close Foreman yourself when tabs are dirty — session tabs restore on launch and a stray save would
overwrite `.fjson` files on the file server.
