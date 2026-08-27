# Foreman2 — Community Fork: Feature & Improvement Log

> A fork of [Foreman](https://github.com/DanielKote/Foreman2) — the Factorio 2.0 production planner.  
> This fork extends the original with quality-of-life features, workflow improvements, and bug fixes  
> developed through a series of AI-assisted sessions.

---

## ✨ New Features

---

### 📋 Copy for Factorio — Inline Feedback *(2.4.0)*
Copying the building list no longer interrupts with a dialog you have to dismiss every time.
- The **"Copy for Factorio"** button reports on itself: it flashes **"Copied 42 lines"** for just over a second, then returns to its normal caption
- The line count is preserved from the old dialog, with correct singular wording for a one-line list
- The empty-graph case — nothing to export — flashes **"Nothing to copy"** on the same button instead of raising a message box
- Clicking again mid-flash restarts the countdown rather than stacking timers, so a rapid double-click cannot leave the button stuck on the wrong caption

---

### 🧳 Shared Settings File *(2.4.0)*
Settings can now live next to `Foreman.exe` instead of in the per-machine Windows store, so one set of preferences follows the program.
- Windows keys the normal `user.config` to the machine, the windows user, **and** the assembly version — which is why settings appear to reset when you switch machines or update Foreman
- **Settings → Graph Options → Settings Storage** turns on a `foreman-settings.json` written beside the exe; run Foreman from a drive both machines can reach and they share one set of settings
- The presence of the file is the switch, so the second machine needs no setup — it picks the file up on its next start
- Every save mirrors into the file, and the file is written to a temp name first so a dropped network connection cannot leave it half written
- Last writer wins: if both machines are open at once, whichever saves last is the state that survives
- Independently of the file, local settings now carry across a Foreman version update instead of starting from defaults
- An unreachable share, a read-only folder, or a damaged file falls back to local settings and a log line rather than an error

---

### 🛟 Crash-Tolerant IO & Session Recovery *(2.4.0)*
Work in progress now survives closing the program, a crash, or losing access to the original save location.
- `SessionManager` stores every open tab in `%LOCALAPPDATA%\Foreman\Session`, falling back to the user temp folder
- All session operations no-op rather than throw when the directory is unusable
- `SafeIO` wraps file access so an unreachable path (network share, removed drive) degrades gracefully instead of taking the program down
- `SafeSettings.Save` replaces direct `Settings.Default.Save` calls, which could throw on a locked or unwritable `user.config`
- Open tabs are restored on launch, and unused snapshots are pruned automatically

---

### 🛤️ Gutter Nodes *(2.4.0)*
A passthrough node can now be drawn as a long axis-aligned line with its links attaching along its length.
- Connections become short perpendicular stubs instead of long diagonal crossings — a bus lane for your graph
- Drag either end to resize; create a gutter from a selection, add nodes to an existing one, or remove it
- Attach unconnected selected nodes to a gutter in one action
- **Rate math is unchanged** — the solver never sees a gutter

---

### 🙈 Utility-Link Hiding *(2.4.0)*
Links carrying utility items collapse to a marked stub at each end so they stop crossing the whole graph.
- Per-item toggle: *Hide links for this item*
- Selecting either endpoint brings the link back in full, so nothing is lost
- Dedicated **Utility Items** tab in settings for managing the list
- Image export gets an opt-in *Show hidden utility links* checkbox

---

### 🔎 Find Overhaul *(2.4.0)*
The Ctrl+F find panel was reworked end to end.
- Results are retired the moment the query or scope changes — stale matches no longer stay highlighted or remain reachable via *Go to Next*
- Enter and *Go to Next* zoom in on the individual hit rather than framing every result at once
- Scope dropdown selects **All / Nodes / Links**, with links matched on the item they carry
- Node matching now also covers the building, beacon, modules, fuel, and key-node title
- Panel is laid out by flow rather than fixed coordinates, so it survives display scaling

---

### 🤖 Task-List Mod — Personal Logistic Requests *(2.4.0)*
The task list can now ask your logistic bots to bring the buildings to you.
- Each task row has a **request button** that adds a personal logistic request for that item at the task's count
- **Request All** does the same for every unchecked task in one click
- Requests are only ever raised, never lowered, so re-importing a bigger list tops up an existing request instead of shrinking it
- The request is **cleared automatically once the task goes done** — whether the bots filled it or you checked the box yourself — so nothing keeps hauling after the fact
- **Clear Requests** drops everything the mod added, and *Clear All* does it too when wiping the list
- All requests live in a single logistic section named after the player, so the mod never edits requests you set up yourself
- Entity names are resolved to the item that places them, so the request works for buildings whose item name differs from the entity name

---

### 📦 Task-List Mod — Inventory Scanning & Producer Search *(2.4.0)*
The companion `foremantasklist` Factorio mod control script was reworked, and the packaged mod zip rebuilt to match.
- Scans player and platform inventories to track task progress against what you actually have
- Correct producer search for locating the machine that makes a given item
- Mod zip is regenerated automatically by a post-build step, so the shipped zip never drifts from source
- One zip is built per supported Factorio version (`foreman-tasklist_1.2.0.zip` for 2.0, `foreman-tasklist_1.3.0.zip` for 2.1) because Factorio matches a mod's `factorio_version` exactly - drop both into the mods folder and the game loads the one it can
- The helper mods Foreman deploys itself (`foremanexport`, `foremansavereader`) are stamped with the version of the Factorio install being launched, so preset import and save-file loading work on 2.0 and 2.1 alike

---

### 🧩 Factory Summary — Module Tabs *(2.4.0)*
Two new tabs in the Factory Summary break down every module your factory needs.
- **Modules (in buildings)** and **Modules (in beacons)** are tracked separately — the same module in an assembler and in a beacon points at different nodes
- Counts are per *filled slot*, multiplied by the number of buildings (or beacons) holding them, so the total is what you actually have to produce
- Second column reports how many buildings/beacons hold each module
- A **Modules** total joins the building and beacon counters in the summary header
- Sorting, filtering, and quality pairs work exactly as on the existing building tabs
- **Double-click a module** to center the graph on a node using it; double-click again to step through the rest
- Both tabs are included in the **CSV export** and in **Copy for Factorio**, where assembler and beacon usage are merged into a single per-module total for the task list

---

### 🗂️ Full Pyanodons Support Through Py Logistic Science

Through extensive testing I know that this mod works through Py Logistics.  It may work beyond that, I 
haven't advanced my game far enough to test it, if you do please let me know.

It should work with vanilla Factorio and the expansions, but this has not been tested.  If you do test it, 
please let me know if it works or if there are any issues.

---

### 🗂️ Multi-Tab Graph Support
Open and work on multiple production graphs simultaneously in a tabbed interface.
- Full `GraphTabControl` with add, close, and rename tabs
- Each tab maintains its own graph, preset, and undo history
- **Tabs persist across sessions** — reopen Foreman and your tabs are restored exactly as you left them
- New tabs inherit the active tab's preset automatically
- Paste operations correctly target the active tab
- Per-tab gridline settings saved and restored on switch

---

### ↩️ Undo / Redo
Full snapshot-based undo and redo for all graph editing operations.
- `Ctrl+Z` / `Ctrl+Y` to step back and forward through history
- Works for node creation, deletion, moves, connections, annotations, grouping, and more
- Snapshot taken before every destructive operation — nothing is lost
- Coverage across all right-click context menu operations including assembler/module changes

---

### 📝 Shapes & Text Annotations
Draw directly on the production graph canvas to label and organize your factory plans.
- **Drag-to-draw** rectangles and ellipses anywhere on the canvas using a crosshair cursor
- **Text labels** — place floating text with full font, size, color, and style control
- **Text placement workflow** — a popup dialog lets you set format first, then the label attaches to the mouse for precise placement on the canvas
- **Auto-resizing** text boxes that grow with your content as you type
- Left, center, and right **text alignment** options
- **Properties menus** for both shapes (color, type) and text (font, size, style) with color picker dialog
- **Anchor point resizing** — click any annotation to get a bordered selection with drag handles
- Annotations participate in **rubber-band selection**, grouping, and copy/paste
- Annotation style defaults (font, color, shape type) **persist across sessions**
- DPI-aware scaling so annotations look correct on any display or machine

---

### 📦 Node & Annotation Grouping
Select any mix of nodes and annotations and group them into a single moveable unit.
- Drag the group header to move everything together
- Groups can be expanded and collapsed
- Annotations co-move with grouped nodes during drag operations
- Grouping works across the full canvas

---

### 🔍 Ctrl+F Node Search
Quickly find any node in a large or complex graph.
- `Ctrl+F` opens a search panel; type any item or recipe name
- **Result highlighting** — matching nodes are visually highlighted
- **Auto-zoom** — the view centers and zooms to the found node
- Navigate through multiple results with Next/Previous

---

### ➡️ Convert Node to Passthrough
One-click conversion of any recipe or supply node into a passthrough node.
- Snapshot-based: fully undoable
- Keeps all existing connections intact
- Right-click on any connection or node to access

---

### 🔀 Merge Passthrough Nodes
Select multiple passthrough nodes for the same item and merge them into one.
- Consolidates all upstream and downstream connections
- Cleans up graphs that have grown sprawling passthroughs

---

### ↕️ Align Selected Nodes
Right-click → Align to snap selected nodes to a clean grid.
- Align left, right, top, bottom, or center (horizontal and vertical)
- Makes large graphs dramatically easier to read

---

### ⚡ Shift+Click / Shift+Drag Supplier & Consumer Shortcuts
Speed up graph building with keyboard-mouse shortcuts for creating supply and demand nodes.
- **Shift+click** a passthrough to instantly create a supplier node
- **Shift+drag** from a passthrough to place a supplier or consumer
- **Axis-lock drag** with Shift held: constrain movement to horizontal or vertical
- **Ctrl / Ctrl+Shift** shortcuts during link dragging for passthrough wiring

---

### 🔬 Science Pack Filter
Filter the recipe chooser by technology tier so you only see recipes you've unlocked.
- Filter persists **globally** across all panel instances in the session
- **Persists per save file** — each graph remembers its last tech filter
- Works in the recipe chooser panel during node creation

---

### 🧪 Research Pack (Tech Tier) Filter Persistence
The selected research pack and tech tier filter are saved with each graph file.
- Reopen a graph and the filter is exactly where you left it

---

### 📊 Factory Summary — Non-Modal & Live Refresh
The Factory Summary window no longer blocks your workflow.
- Runs as a **non-modal** window — keep it open while editing the graph
- **Live refresh** — summary updates automatically as you change the graph
- **Double-click any unlinked item** to zoom to and highlight the relevant node in the graph

---

### 📊 Factory Summary — "All" Tab
A new **All** tab in the Factory Summary shows every item and fluid together in one combined view, alongside the existing Items and Fluids tabs. Defaults to open on first launch.

---

### 📋 Copy for Factorio Button + Companion Mod
Export your production plan directly into a running Factorio game.
- **"Copy for Factorio"** button in the Factory Summary serializes the building list to clipboard
- Companion **Factorio mod** (`foremantasklist`) reads the clipboard data and creates a persistent in-game task list toggled with Ctrl+B
- **Double-click any item** in the in-game list to find all machines producing it — places map pins and opens remote map view centered on the first producer
- **Clear Pins** button removes all search map tags
- Auto-zip build step keeps the mod versioned alongside the application

---

### 💾 Multi-Preset Alias List
Save files now store a list of preset aliases.
- Supports workflows where graphs are opened with different-but-compatible presets
- Aliases resolve automatically on load

---

### 🚀 Icon Cache Performance
Startup time significantly reduced for large mod presets (e.g. Pyanodon's, AngelBob).
- Icon cache uses **GZip compression** — smaller disk footprint, faster reads
- **Local mirror** — icons are downloaded once and cached locally in `%LOCALAPPDATA%\Foreman2\IconCache\`
- **Async preparation** — preset JSON parsing and graph file reads moved off the UI thread
- **Batch invalidation** — per-node `Invalidate()` suppressed during graph load; single repaint at the end
- Progress reporting throttled to once-per-percent to reduce overhead during load

---

### 📁 Remember Last Directory
Foreman now remembers the last folder you opened or saved a graph from.
- No more navigating from scratch every time

---

### 📂 Auto-Load Last File on Startup
Foreman automatically reopens the last graph you had open when you start the application.
- Falls back to loading the default preset if no previous file exists, ensuring the UI is always ready

---

### 🧮 Right-Click Node Math Breakdown
Right-click any recipe node to see the full calculation breakdown — inputs, outputs, assembler count, and rates — in a readable format.

---

### 🔗 Unlinked Outputs in Cross-Link Summary
Items and fluids that are unlinked outputs are now correctly included in the cross-link item and fluid summary totals.

---

### ⚡ Electricity Display
Power consumption and production data is now shown in the Factory Summary Buildings tab, giving an accurate picture of your factory's electrical load.

---

### 📋 Copy Item Name from Right-Click Menu
Right-click any item tab on a node to copy the item's internal or friendly name to the clipboard — useful for searching in-game or in external tools.

---

## 🐛 Bug Fixes

| Fix | Description |
|-----|-------------|
| False unsaved-changes warning | Opening a graph no longer triggers a "save?" prompt immediately after loading |
| False save prompt on view changes | Switching views (zoom, pan, tab changes) no longer marks the file as dirty |
| Annotation-only unsaved changes | Adding or modifying annotations without touching nodes now correctly triggers the unsaved-changes prompt |
| Pyanodon fluid-mining recipes | Fluid-mining recipes in Pyanodon's mods now appear correctly in the node selector |
| Coke Oven Gas fluid temperature | Fixed Lua export typo (`product.temperate` → `product.temperature`) that caused all fluid product temperatures to export as nil, resulting in incorrect 15°c defaults across all fluid-output recipes |
| Barreling recipes | Barreling recipes now appear properly in production graphs (resolved Korlex milk display as a side effect) |
| GraphSummaryForm sort crash | Fixed `InvalidCastException` when sorting columns containing quality item pairs |
| Science pack filter persistence | Filter and preset now correctly saved and restored on graph load |
| Recipe book icon glitch | Fixed garbled item names and missing icons in the recipe chooser caused by the Factorio data exporter outputting UTF-8 while `PresetImportForm` read stdout as Windows-1252 ANSI; fixed by setting `StandardOutputEncoding = UTF8` on the process |
| Item summary bugs | Various display and calculation bugs in the item summary panel resolved |
| Node drag state bugs | Fixed ghost-drag and stuck-drag states in `BaseNodeElement` and the graph viewer; `DragStarted` now reset in `MouseDown` as well as `MouseUp` |
| Post-convert node fly bug | Fixed node flying to random location and locking after converting a node; `CleanupNodeFromGroups` is now called before `DeleteNode` in `ConvertNodeToPassthrough` |
| Fish farm / Wood Processing Unit not found | The Lua companion mod now searches by entity name directly with a `-turd` suffix fallback for Pyanodon's recipe variant naming convention |
| Annotation DPI scaling | Annotations now scale correctly across machines with different display DPIs |
| Annotation selection | Annotation hit boxes no longer block rubber-band selection of nodes behind them |
| Text annotation hit testing | Right-click context menu on text annotations now correctly triggers |
| Mixed selection delete | Deleting a selection containing both nodes and annotations now works correctly in a single operation |
| Copy/paste relative positioning | Pasting a selection containing both nodes and annotations now preserves their relative positions; both groups use the same computed offset |
| Find panel focus | Find panel now correctly captures keyboard input on open |
| NullReferenceException (graph before preset) | Fixed crash when interacting with graph before preset finishes loading; startup now loads a default preset when no save file is present |
| NullReferenceException (paste to new tab) | Fixed crash when pasting content into a freshly created tab |
| NullReferenceException (ParentForm) | Replaced `ParentForm` references with `FindForm()` for reliability in tabbed context |
| ArgumentOutOfRangeException (tab drawing) | Fixed crash in `GraphTabControl.OnDrawItem` when tab list changes during render |
| KeyNotFoundException (missing modules) | Fixed crash when a save file references a module no longer in the active preset |
| Settings crash on blank graph | Fixed `NullReferenceException` when opening Settings before a preset is loaded; DCache is now always initialized on startup |
| Async race condition on startup | Fixed race where DCache null-check could fire before `LoadGraph` completed by switching to a `loadingFromFile` flag |
| Summary filter crash | Fixed `InvalidCastException` when typing in the Factory Summary filter box; building/module rows tag quality-pair structs that don't derive from `DataObjectBase`, so the name lookup now switches on the tag type instead of blind-casting |
| Comma in Copy for Factorio export | Module and building counts of 1,000+ were formatted with thousands separators, which broke the companion mod's `"<digits>x "` line parser; separators are now stripped from the exported count |

---

## 🛠️ Technical Notes

- All undo/redo is **snapshot-based** — the full graph state is serialized before each destructive operation using the existing `ProductionGraph.GetObjectData` / `InsertNodesFromJson` infrastructure, making it robust across all feature areas without per-feature undo instrumentation.
- Annotations are full first-class citizens: they serialize to JSON with the graph, participate in selection, copy/paste, grouping, and undo.
- Tab state (open files, scroll positions, active tab, gridline settings) serializes to a sidecar file alongside the graph JSON.
- The companion Factorio mod uses `storage` (not the Factorio 1.x `global`), `player.set_controller{type = defines.controllers.remote}` for map navigation (replacing removed `open_map`/`zoom_to_world`), and tag scanning via `force.find_chart_tags()` with a `"[F2] "` prefix since `LuaCustomChartTag` no longer has an `id` property in Factorio 2.0.
- WinForms Designer limitations with `TableLayoutPanel`: layout changes to forms with filling `TableLayoutPanel` controls must be made directly in `Designer.cs` rather than through the Visual Studio Designer UI.
- File access goes through `SafeIO` rather than `System.IO` directly. The guiding rule is that a storage failure should never be fatal: an unreachable network share, a removed drive, or a locked `user.config` degrades to a no-op or a fallback location instead of an unhandled exception.
- Gutters are a **presentation-layer** concept living in `PassthroughNodeElement`. A gutter changes only how a passthrough node is drawn and where its links attach — it carries no rate semantics, so the solver is entirely unaware of it and throughput results are identical with or without gutters.
- The project builds **x64 only**. Every platform in the solution maps to `x64`, so Debug lands in `bin\x64\Debug\` and Release in `bin\x64\Release\`. The old `Debug|x86` and `Release|x86` configurations that wrote to `bin\Debug\` and `bin\Release\` were unreachable through the solution and have been removed — their names were vestigial anyway, since `Release|x86` targeted x64 and `Debug|x86` targeted AnyCPU. Stale executables left in those folders were a trap to launch by mistake.

---

*Built with AI-assisted development sessions using [Claude](https://claude.ai), May 2026.*

---

# Foreman 2.0 #
![1: Foreman 2.0](https://puu.sh/Im6D4/5a42f137e2.jpg)

This is a relatively simple program for generating flowcharts for production lines in the game [Factorio](https://www.factorio.com/).

Requires .Net 4.8 or higher and Visual C++ 2019 x86 to run. I am not sure about earlier versions, sorry.

For example, here's a flowchart showing the optimal resources and assemblers required to make the first base red science in the Pyanodon mod pack (rather comparable to base Factorio rocket I would say):

![2: Base red science for Pyanodons](https://puu.sh/Im6qB/83d13bab31.png)

## Download ##

To download the latest version of Foreman 2.0 please visit the "Releases" tab here on Github and download the "Release.zip" from the latest release.

The vanilla preset is included in the release, with a couple presets (from common modpacks) available in the "Presets.zip". You can always import your own preset using your customized modpack via the foreman app (see below for "presets" heading).

## Usage ##

Run Foreman.exe. It will already have the default Factorio 1.1 preset loaded so you can start graphing right away. Click on 'add item' or 'add recipe' button to begin.

Once you have your first node, you can drag from the ingredients/products of the node to add more nodes, or just click on add item/add recipe to add a disconnected node.

If you are dragging from the ingredients/products of the node and let go, you will have an option to choose which recipe you wish to use for the new node, or if you wish to create an input/passthrough/output node. If however you are holding Ctrl when you let go you will automatically create a passthrough node without any options.

If you are dragging from the ingredients/products of a selected passthrough node while holding down Ctrl and have multiple passthrough nodes selected (and only passthrough nodes), you will automatically place down a set of new passthrough nodes connected to the old ones. This should enable for quickly laying down a bus for larger graphs.

Movement around the graph can be done by dragging with the middle mouse button, or by dragging with the right mouse button (assuming you werent pressing down on a node when you started). Dragging with the middle mouse button is recommended, and is possible even while doing other operations such as selecting / moving modes, or dragging a new connection.

Dragging with the left mouse button (from an empty location) will enable you to select a group of nodes, and doing this while holding down the Ctrl key will 'add' to the currently selected nodes, while doing the selection while holding down the Alt key will 'subtract' from the currently selected nodes.

A node (or a selection of nodes) can be moved around the graph by dragging them with the left mouse button, or by using the arrow keys. Holding shift while dragging with the mouse will limit movement to the horizontal or vertical axis, while holding shift while moving with the arrow keys will move by a major grid increment rather than the minor grid increment.

Once a group of nodes has been selected you can also Ctrl+C or Ctrl+X the group (copy/cut), and Ctrl-V afterwards to paste the nodes wherever you wish. Keep in mind that pasting nodes will deselect the currently selected nodes and select the newly pasted nodes instead.

In most cases there is a helpful tool tip available at the top left of the screen to give guidance. Dont quite rely on it though - still working on it.

### Menu ###

![3: Main Menu](https://puu.sh/Im6AT/d126c4de38.jpg)

Add new item/recipe buttons will allow you to make the first nodes, following which you can drag from the ingredients/products to add further nodes.

You can activate grid lines to snap nodes into position, and set the given graph time scale in the options right above the graph. Currently the graph summary is still in development, so you can ignore that.

In most cases you would wish to keep the graph in auto-update mode which will re-calculate the flows every time you make a change (such as adding a new recipe, linking two nodes, deleting a node, etc). However for extremely large graph chains (such as planning out the entire production chain for science in B&A mods) it would be a good idea to activate the "pause all calculations" option (under graph options) which will stop any graph flow updates. Once your entire graph is finalized, you can deactivate that option and it will calculate the flows again (which for large graphs can take over 1 second).

Graphs are loaded in and saved through the save/load buttons, though keep in mind that no auto-save is present. It is recommended to save often, as this is currently a DEV version and is (not as much anymore but still) prone to crashes. For larger graphs it is recommended to design them in parts, save each separately, then import them into the final large graph.

Clear graph will clear the current graph completely, though it WILL NOT touch your save. You will need to save/overwrite your save for that to happen.

### Item / Recipe Selection ###

![4: Item & Recipe Selection Window](https://puu.sh/Im8Lm/21fca42176.jpg)

The item and recipe selection have been modeled after the Factorio window, so should be intuitive to navigate. A few things of note:

(1) There is an additional extraction/power group that has been added by foreman. It is there to group together any 'recipe' for mining ores, pumping oil (or other liquid, including water), generating heat (for example from nuclear reactors), and producing electricity (ex: steam generators). So if you are looking for any of those, they will be in that group.

(2) The filter will search through both the dev-name of the item/recipe, as well as the translated name. If you wish to search for the recipe only, there is a checkbox for that. Otherwise it will search for a possible match in the recipe name as well as a possible match in the ingredients/products.

(3) It is recommended to leave 'Ignore assembler' and 'Show disabled' turned off. Ignore assembler will no longer check if the item/recipe has an enabled assembler for its production, while show disabled will include recipes that have been disabled. If these options are turned on, then the recipes without an enabled assembler will have a dark yellow background, while disabled recipes will have a dark red background. If the DEV option to show unavailable items/recipes is turned on (in settings), then any unavailable item/recipe will have a light purple background.

(4) If the recipe selection is based off a pre-selected item (such as after the 'add item' button, or after a drag operation on a node's input/output), then there may be several other filter options: Ingredient, Product, and Fuel. They do exactly as you would expect - filtering the possible recipes based on the item's use as an ingredient, as a product, or as fuel. So if you are planning your coal production and dont want to see all the different recipes that can use coal as fuel, then you can turn off that option.

(5) The lower buttons can be used to add a source / passthrough / output node.


### Nodes ###

![5: Node examples](https://puu.sh/Im8AG/5924f95fa4.jpg)

Nodes come in 4 varieties; source nodes that act as inputs, sink nodes that act as outputs, passthrough nodes that can be used as limiters or just to tidy up the graph, and recipe nodes that actually do stuff. The first 3 can have a specific flow set that would specify the amount of items coming in/out/through the node, while the last (recipe node) can specify the number of buildings (among many other options) that will be utilized. Any of the 4 can be set to automatic (and in fact are thus set when first placed), meaning that their flow/building count is calculated based on the optimized flow of the graph. Those nodes with set flow/building count will have a darker background, and should thus be easy to visually identify.

The item input/output boxes are usually drawn with a grey border, but appear as red if they are not connected to anything, or golden if they are receiving too much input You can drag from them to quickly establish a new linked node, or right click for options (delete all links).

The nodes themselves are usually colored in light green with a dark green border around them.

(1) If the assigned flow or building count can not be achieved (due to insufficient incoming ingredients), then the border will be colored red. This tends to happen if the user has set one of the previous recipes / inputs to a fixed amount (that is insufficient).

(2) If the assigned flow or building count is too high (overproduction is expected) or the output isnt connected to anything then the border will be colored golden and whichever item is being overproduced (and thus will begin to stockpile!) will also have its frame colored golden. The two values provided represent the consumed amount (top) and the produced amount (bottom). The difference between the two represent the rate at which the item will accumulate.

(3) If the node uses an unobtainable or disabled recipe or building, then there will be an orange flag on the top left of the node, with a warning sign on the top left.

(4) If the node has errors (such as a recipe / item / building from another mod, assembler / fuel / module assigned that cant be used, or anything else of similar severity), then the background will be fully colored in orange with a warning sign on the top left.

For passthrough nodes, they can be set to be simply drawn, meaning they will appear as a line with two circles you can drag connections from. When you export the graph to an image the circles will not be visible, leaving the passthrough node virtually unrecognizable from a simple connection. This should allow for cleaner graphs. You can also set it to be fully drawn, which will draw the full node with item input/output boxes and flow values.

Hovering over the warning sign will list the issues, clicking on the warning sign will auto-resolve issues (WARNING: in case of errors this will quite often lead to the deletion of the node!), while right clicking on the warning sign will give a menu of possible solutions. You can of-course resolve the issue yourself, or just ignore it if you know what you are doing.

Right clicking on the node itself will give you several options, including deleting the node, copying its properties (so you can later paste it to a node / selection), and applying default assembler/ modules. If you already copied a node's properties you can also paste them to the given node/selection while specifying what exactly you wish copied (assembler, modules, fuel, beacon). You can also set the 'simple draw' options for passthrough nodes.

Left clicking on the node will lead you to the flow or recipe editor.

Left clicking on a passthrough node will also allow you to set its 'simple draw' option.

### Recipe Node Options ###

![6: Recipe node editor](https://puu.sh/Im7OQ/f3b4573b74.jpg)

Most options here are rather self-explanatory and I have taken the initial design of the recipe node editor from the HelMod Factorio mod, so any users of that will feel right at home.

You can click on any of the building (assemblers) to select which one you wish to use. If the building supports modules you can click on the module in the module option to add it to the selected modules, or click on one of the already added modules to remove it. If the building can be supported by beacons, you can select the beacon and beacon modules in much the same way.

If the selected building burns fuel (liquid or solid), the options will be available right under the building selection.

If any of the options are red, that means that they have an issue, and it is recommended not to use them. In most cases it represents that the selected building / module is not available in regular play. Note: They will still work, and in the example above you have the 'hand crafting' as an assembler option which will still work (though it is red - meaning not buildable in regular play... you have to invite friends over to use them as an assembler, which is an out-of-game action).

To set the actual number of buildings you wish to use, switch the # of assemblers from auto to fixed and set the value. The graph will then calculate all the flows knowing that there are exactly that many assemblers in that node. Keep in mind that if there arent enough ingredients being passed to the node then it will show a lower value!

Specifically for reactors you can set the number of neighbors so as to properly apply neighbor bonuses (ex: for nuclear reactors).

For the beacon, there are several values to be set:

(1) # of beacons: this specifies the average number of beacons that will affect the building, and is the value that will be used to calculate the bonuses applied to the building.

(2) / Assembler: this specifies the number of beacons you will place per placed assembler. So for example if you are building a linear setup with beacon-assembler-beacon, you would have 2 beacons placed per assembler, so you would put in 2. This is used for the 'total beacons' and the power usage calculations, and will not impact the number of buildings/assemblers necessary.

(3) Additional: this specifies the number of additional beacons you will need. So in the example above, if you need to place 2 more beacons above your rows of beacons-assembler and 2 more below your rows (in order to have 8 beacons active on each assembler), you would put in 4.

Think of the values as 'total beacons' = 'per assembler' x '# of assemblers' + 'additional'.

## Settings ##

Settings have mostly been moved to the settings form, which has been clearly broken into 3 sections:

### Presets ###

![7: Presets](https://puu.sh/Im6B4/0a6aef4421.jpg)

All the currently saved presets (in the Preset folder) are listed here. You can check their mods & difficulty options in the list on the right by clicking on the preset you wish to see. To import a new preset you must prepare your Factorio game to the settings you wish - such that if you created a brand new game with the default options it will be the kind of preset you wish to see. Once that is done, exit out of Factorio, click 'import new preset from Factorio' browse to find the Factorio location (its the main install folder with 'bin' and 'data' folders - if using the steam version it should auto-locate for you), choose the difficulties you wish to use, give the preset a name, and click import. If you are using advanced options (such as --mod-directory) that change the mod folder location from the default, you can manually search for the mod folder. Otherwise it is best to leave the 'Mod Folder Location' blank and the importer will auto-locate your mods for you.

If you have more than 1 preset currently in your list, you can compare 2 presets to see any differences between them. Rather helpful to find what changes the newly updated mods have brought that might impact your game.

### Enabled Objects ###

![8: Enabled Objects](https://puu.sh/Im6Bo/6d0473b0e1.jpg)

This is where you can set which buildings / recipes are to be enabled for your graph. Rather handy if you wish to plan for a specific science tier. Each building/recipe can be set manually by searching for it and enabling/disabling it, by loading a save file, or by selecting which science packs you wish to have available (any technology with the required science packs will be researched).

You can also allow unavailable items (and enable their use), though this is not recommended. Unavailable items are those that are uncraftable during regular play, such as the infinite pipes, cheat beacons (from bobs), and other such objects. It is highly unlikely that you will require them, so keep them off.

Recipes can also be enabled/disabled straight from the recipe selection window by right clicking on them, though keep in mind that they will disappear from the visible list if 'show disabled' is not checked.

### Graph Options ###

![9: Graph Options](https://puu.sh/In2aO/c462e226a0.jpg)

**Level of detail:** specifies how much detail you wish shown on the nodes. Low will just show the recipe name, Medium will show the assembler + beacon + modules + number of buildings, while High will add building percentages (productivity, speed, power)

**Maximum number of graphical objects:** when more than this number of nodes is visible on the screen, the graphics shift to a simple view where you will no longer see any node information or item icons. The same thing happens if you zoom out too far. If your computer can handle it, crank it up! Keep in mind that the default (300) should be more than enough for most users. On the other hand if you have a meh computer decreasing this value to 200 or 150 may help performance (visual only).

**Draw arrows to show direction on link lines**: if enabled each throughput line will have a direction arrow at the end showing the direction of flow. If dynamic link width is used (or the option is disabled), the item tabs will have a light arrow drawn inside them instead.

**Dynamic link width:** if enabled the width of the item flow between nodes will be proportional to the amounts being moved around (so expect really beefy lines from your miners to the smelters, and really thin ones from your high end electronics). Fluids and items are considered separately.

**Abbreviate science packs:** If the mod pack you are graphing for has too many science packs (ex: space exploration), using this option is recommended. It will hide any science packs from the 'required science packs' of any recipe that is required to craft a higher tier science pack required by said recipe. So for example a red+green science will be abbreviated to just green since red science is necessary to research green science.

**Show recipe tool-tip:** If turned on will show the recipe of a given node when you hover over it.

**Round building count:** If turned on will round up buildings to the nearest integer (so instead of 0.2 buildings you will see 1 building). This is visual only and doesnt impact the power consumption of the buildings!

**Lock recipe editor to top left corner:** If turned on the recipe editor panel will always show up in the top left corner, otherwise it will show up next to the node being edited.

**Flag over or under supplied nodes** If enabled, any over or under supplied nodes will have not just the border set to the appropriate color (red or gold), but also a flag will be visible in the top left corner similar to warning/error flags. Turn this on for better visibility of over/under supplied nodes.

**Guide Arrows** Useful to find any error nodes (recipe from another mod save for example), or warning nodes (disabled assemblers, uncraftable fuel, etc). Can also be used for missing link nodes (nodes where some of the inputs/outputs are not connected to anything), or over/under supplied nodes. If any exist outside the currently viewed area (where they are rather obvious, having an orange flag/background to them), there will be an arrow pointing in their direction.

**Defaults (assemblers & modules):** Should be straight forward. You can set which type of assembler you wish to be automatically assigned to newly added nodes, as well as what type of modules to give it.

**Defaults (node direction)** Placed nodes can either have inputs at the bottom and outputs at the top (Up direction), or inputs at the top and outputs at the bottom (Down direction). The set default will be applied to newly placed nodes.

**Defaults (smart direction)** If enabled, the direction of a new node placed by dragging from an item tab (which will be linked to said item tab) will be set automatically - if the new node is below the node you dragged from, then it will be pointing down, and if it is placed above the node you dragged from then it will be pointing up.

**Defaults (Simple draw passthrough nodes)** Passthrough nodes have 2 drawing options - regular and simple. Regular draw will draw the passthrough node as all other nodes - with a rounded border, input & output item boxes, and flow values. Simple draw will draw the passthrough node as a single line connecting the inputs to the outputs with no item boxes - virtually indistinguishable from a regular link line. This could be helpful in organizing the graph without unduly splitting the viewer's attention away from the nodes that actually do something (as opposed to directing flow). Passthrough nodes with set flow (instead of automatic), or passthrough nodes that are over/under supplied will be fully drawn no matter what.

**Advanced:** probably best to leave it alone (turned off) unless there is a particular need for it.

**Advanced (Enable extra productivity bonus for all entities):** to allow for miner productivity, there is an 'extra productivity' value you can set within your mining nodes. If this is turned on then all nodes (and not just the miners) will have an extra productivity that you can set. This should be used cautiously as you can accidentally copy the extra productivity to all nodes, but it is left as an option for those mods that allow mining productivity to act on non-miners (usually by creating 'invisible' beacons that apply the productivity effect)

**Advanced (Show unavailable items):** if turned on will display those items that cant be acquired in regular play (ex: infinite pipes, coins).

**Advanced (Load barreling or crating recipes):** if turned on will load the barreling / crating recipes from the preset (instead of just ignoring them). In most cases you really dont want them, so its best to just keep it off. NOTE: if you are planning on crating and wish to find the flows, please - by all means turn it on.

## Exporting the graph ##

![10: Export](https://puu.sh/Im91L/703d54d784.jpg)

Graph export remains unchanged at this moment from the original Foreman. Click on the 'Export image' button to bring up the export form, browse to select where you wish to save the resulting png file, set the scale (1x should be fine) for the image, check the transparent background if you wish to, and hit export to save the image. Dont worry - the grid will not be exported.

Additionally, be careful when exporting large graphs - this is a raster format instead of a vector one, so large graphs can quickly spiral out of control. It is highly recommended to plan out the factory in smaller sub-factories with a saved graph for each (that you can export as an image), and if you wish to plan the ENTIRE factory, then do so by importing the smaller graphs into the main graph, connecting all the nodes, doing your planning, and NOT exporting the final image (instead just saving the graph and navigating it within Foreman).

## Major changes from Foreman 1.0 ##

(1) removal of LUA code dependency (import of data now done through an automatic process that copies in the export mod, runs Factorio in the background to generate preset, and finally deletes the export mod).

(2) Item graphics have been updated to show what the item looks like in Factorio, not a rough approximation.

(3) Filtering of items/available objects should be much better.

(4) Selection of enabled/disabled objects has been streamlined - initial preset will have all objects available in the standard playthrough enabled; with the option to set the enabled status manually for each object, set the enabled status based off of an existing save file, or set the enabled status based off of the science packs you wish to be available.

(5) Burners have been handled properly (so furnaces work just fine now). In addition objects such as steam engines, nuclear reactors, and heat exchangers work, so if you wish to plan out your nuclear power plant, you can do that.

(6) Item / recipe selection panel has been designed to look and feel like the Factorio one - no need to scroll through hundreds of recipe options.

(7) Graphics have been redesigned to better handle large graphs - tested with 1000+ node graphs. Still recommended to pause updates while editing those graphs. Linear algebra can struggle with huge variable counts. But the graphics will struggle no longer!

(8) Better handling of wrong/missing recipes/items. Save files now store information about what recipes/items are used, and are loaded properly even into wrong preset (though they will be labeled as 'missing', and might not calculate correctly). This should allow for import of saves between preset versions with minimal trouble.

(9) Quality of life changes, including dragging around groups of nodes, copy/pasting nodes, copying node options between each other, and importing graphs from other saves into the current graph.

## Troubleshooting ##

(1) Make sure Visual C++ 2019 x86 is installed.

(2) Add an issue? There is likely to be quite a few bugs at the moment...

## Contributing ##

At the time of writing the only official "contributor" is myself, DanielKotes. This started out as a slight fork of the [original foreman](https://github.com/Rybadour/Foreman), with just a few changes that I didnt bother using git for. It kind of spiraled out of control to the point where it is no longer something that can be considered the original Foreman, thus the new repository.

I have mostly finished with active development and will mostly be releasing updates pertaining to keeping the software functional / fixing up any major bugs. You are free to make a fork of this project and make any changes you want; I will try to check up on any posted merge requests when I have time.
