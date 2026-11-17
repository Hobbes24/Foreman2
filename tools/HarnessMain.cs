// Headless summary of a Foreman preset: loads it through the real DataCache with no window, then
// prints a deterministic report that is meant to be diffed between builds or between presets.
//
// This file is compiled on demand by run-harness.ps1 against the built Foreman.exe - it is not part
// of Foreman.sln, so nothing here can affect a normal build.
//
// Determinism is the whole point: every list is sorted with StringComparer.Ordinal and every number
// is written with the invariant culture, so a diff only shows real changes.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Foreman;

static class HarnessMain
{
	[STAThread]
	static int Main(string[] args)
	{
		if (args.Length < 1)
		{
			Console.Error.WriteLine("usage: Harness.exe <preset name> [--modules]");
			return 2;
		}

		string presetName = args[0];
		bool listModules = args.Contains("--modules");

		// DataCache resolves Presets\ relative to Application.StartupPath, which is this harness exe's
		// folder - so the real Presets folder is never read or written.
		string presetPath = Path.Combine(Application.StartupPath, "Presets", presetName + ".pjson");
		if (!File.Exists(presetPath))
		{
			Console.Error.WriteLine("no such preset in the scratch folder: " + presetPath);
			return 2;
		}

		DataCache cache = new DataCache(true);

		// A real Progress is required - LoadAllData does not null-check it. loadIcons:false keeps the
		// multi-megabyte .dat out of the picture entirely.
		// DataCache narrates its recipe filtering to STDOUT, not stderr, so it has to be muted here
		// rather than redirected by the caller - otherwise every report carries hundreds of
		// "Removal of Recipe ..." lines and no two runs diff cleanly.
		TextWriter realOut = Console.Out;
		Console.SetOut(TextWriter.Null);
		try
		{
			cache.LoadAllData(
				new Preset(presetName, true, false),
				new Progress<KeyValuePair<int, string>>(),
				false).GetAwaiter().GetResult();
		}
		finally
		{
			Console.SetOut(realOut);
		}

		StringBuilder report = new StringBuilder();
		report.AppendLine("preset: " + presetName);
		report.AppendLine();

		report.AppendLine("[counts]");
		Row(report, "mods", cache.IncludedMods.Count);
		Row(report, "technologies", cache.Technologies.Count);
		Row(report, "groups", cache.Groups.Count);
		Row(report, "subgroups", cache.Subgroups.Count);
		Row(report, "qualities", cache.Qualities.Count);
		Row(report, "items", cache.Items.Count);
		Row(report, "recipes", cache.Recipes.Count);
		Row(report, "assemblers", cache.Assemblers.Count);
		Row(report, "modules", cache.Modules.Count);
		Row(report, "beacons", cache.Beacons.Count);
		report.AppendLine();

		// Touches DataCache.PlantProcesses - after the dead preset comparer was removed, this harness is the
		// only thing in the codebase that reads that property, which is how its self-reference went unnoticed.
		report.AppendLine("[plants]");
		Row(report, "plant processes", cache.PlantProcesses.Count);
		Row(report, "available plant processes", cache.AvailablePlantProcesses.Count());
		report.AppendLine();

		// Surfaces, and the predicate that is the whole point of reading them. The pass counts are the real
		// check: a surface that passes every recipe means its property values never got read, and a surface
		// that passes none means the condition bounds came through inverted. Both would otherwise look like
		// a plausible report.
		List<Recipe> conditioned = cache.Recipes.Values
			.Where(r => r.SurfaceConditions.Count > 0)
			.OrderBy(r => r.Name, StringComparer.Ordinal)
			.ToList();
		List<Assembler> conditionedAssemblers = cache.Assemblers.Values
			.Where(a => a.SurfaceConditions.Count > 0)
			.OrderBy(a => a.Name, StringComparer.Ordinal)
			.ToList();

		report.AppendLine("[surfaces]");
		Row(report, "surface properties", cache.SurfaceProperties.Count);
		Row(report, "surfaces", cache.Surfaces.Count);
		Row(report, "recipes with conditions", conditioned.Count);
		Row(report, "assemblers with conditions", conditionedAssemblers.Count);
		// Drives whether the surface selector appears at all. A preset with surfaces but nothing
		// conditioned on them reads "no" - which is what space exploration and vanilla 2.0 do, since
		// the six properties and the nauvis planet come from base rather than from space age.
		report.AppendLine(string.Format(CultureInfo.InvariantCulture,
			"  {0,-38} {1}", "filtering is useful", cache.SurfaceFilteringIsUseful ? "yes" : "no"));
		report.AppendLine();

		if (cache.SurfaceProperties.Count > 0)
		{
			report.AppendLine("[surface property defaults]");
			foreach (SurfaceProperty sp in cache.SurfaceProperties.Values.OrderBy(x => x.Name, StringComparer.Ordinal))
				report.AppendLine(string.Format(CultureInfo.InvariantCulture,
					"  {0,-38} {1}{2}", sp.Name, sp.DefaultValue, sp.IsTime ? " (time)" : ""));
			report.AppendLine();
		}

		if (cache.Surfaces.Count > 0)
		{
			report.AppendLine("[surface recipe pass counts]");
			foreach (Surface surface in cache.Surfaces.Values.OrderBy(x => x.Name, StringComparer.Ordinal))
			{
				int recipesPassing = cache.Recipes.Values.Count(r => surface.Passes(r.SurfaceConditions));
				int assemblersPassing = cache.Assemblers.Values.Count(a => surface.Passes(a.SurfaceConditions));

				// What the picker actually shows: a recipe needs its own conditions to pass AND at least one
				// assembler that can stand here. This mirrors PassesSurfaceFilter with ignoreAssemblerStatus
				// false, so a gap between this and the plain recipe count is the assembler half doing work.
				int pickerPassing = cache.Recipes.Values.Count(r =>
					surface.Passes(r.SurfaceConditions) &&
					(r.Assemblers.Count == 0 || r.Assemblers.Any(a => surface.Passes(a.SurfaceConditions))));

				report.AppendLine(string.Format(CultureInfo.InvariantCulture,
					"  {0,-24} {1,-10} recipes {2}/{3}  assemblers {4}/{5}  picker shows {6}",
					surface.Name, surface.SurfaceType,
					recipesPassing, cache.Recipes.Count,
					assemblersPassing, cache.Assemblers.Count,
					pickerPassing));
			}
			report.AppendLine();

			// Mirrors SetupAssemblerOptions in EditRecipePanel. "narrowed" is the recipes where some but
			// not all machines can be built here, so the chooser hides the rest. "fallback" is where NO
			// machine passes, which drops the filter entirely rather than leaving a node uneditable - and
			// those are exactly the recipes the picker hides, so it should equal the picker gap above.
			report.AppendLine("[surface assembler chooser]");
			foreach (Surface surface in cache.Surfaces.Values.OrderBy(x => x.Name, StringComparer.Ordinal))
			{
				int narrowed = 0, fallback = 0;
				foreach (Recipe r in cache.Recipes.Values)
				{
					if (r.Assemblers.Count == 0)
						continue;
					int passing = r.Assemblers.Count(a => surface.Passes(a.SurfaceConditions));
					if (passing == 0)
						fallback++;
					else if (passing < r.Assemblers.Count)
						narrowed++;
				}
				report.AppendLine(string.Format(CultureInfo.InvariantCulture,
					"  {0,-24} narrowed {1,-6} fallback {2}", surface.Name, narrowed, fallback));
			}
			report.AppendLine();

			report.AppendLine("[conditioned recipes]");
			foreach (Recipe r in conditioned)
				report.AppendLine(string.Format(CultureInfo.InvariantCulture,
					"  {0,-38} {1}", r.Name, string.Join(" & ", r.SurfaceConditions.Select(c => c.ToString()))));
			report.AppendLine();

			report.AppendLine("[conditioned assemblers]");
			foreach (Assembler a in conditionedAssemblers)
				report.AppendLine(string.Format(CultureInfo.InvariantCulture,
					"  {0,-38} {1}", a.Name, string.Join(" & ", a.SurfaceConditions.Select(c => c.ToString()))));
			report.AppendLine();
		}

		report.AppendLine("[missing]");
		Row(report, "qualities", cache.MissingQualities.Count);
		Row(report, "items", cache.MissingItems.Count);
		Row(report, "assemblers", cache.MissingAssemblers.Count);
		Row(report, "modules", cache.MissingModules.Count);
		Row(report, "beacons", cache.MissingBeacons.Count);
		report.AppendLine();

		// A recipe's module list is narrowed by TWO independent mechanisms - allowed_effects (which
		// drops, say, productivity modules) and allowed_module_categories - and DataCache has already
		// applied both by this point, so this count cannot attribute a change to either one. It is a
		// fingerprint, not a diagnosis: if it moves between two runs, something about module
		// availability moved, and --modules then shows which recipes.
		int totalModules = cache.Modules.Count;
		List<Recipe> restricted = cache.Recipes.Values
			.Where(r => r.AssemblerModules.Count < totalModules)
			.OrderBy(r => r.Name, StringComparer.Ordinal)
			.ToList();

		report.AppendLine("[module restrictions]");
		Row(report, "recipes total", cache.Recipes.Count);
		Row(report, "accepting every module", cache.Recipes.Count - restricted.Count);
		Row(report, "narrowed (effects and/or categories)", restricted.Count);
		report.AppendLine();

		if (listModules)
		{
			report.AppendLine("[per-recipe assembler modules]");
			foreach (Recipe r in restricted)
			{
				string mods = string.Join(",", r.AssemblerModules
					.Select(m => m.Name)
					.OrderBy(n => n, StringComparer.Ordinal));
				report.AppendLine(string.Format(CultureInfo.InvariantCulture,
					"  {0} = {1} [{2}]", r.Name, r.AssemblerModules.Count, mods));
			}
			report.AppendLine();
		}

		// The surface selection is written by ProductionGraph.GetObjectData and read back by
		// InsertNodesFromJson. Nothing else exercises that pair without saving and reloading a graph in
		// the GUI, and a key that is written but never read looks identical to one that works until the
		// next time a file is opened. Each case is serialized and parsed back for real.
		if (cache.Surfaces.Count > 0)
		{
			report.AppendLine("[surface save round trip]");
			List<Surface> cases = new List<Surface> { null };
			cases.AddRange(cache.Surfaces.Values.OrderBy(x => x.Name, StringComparer.Ordinal));
			foreach (Surface original in cases)
			{
				string label = original == null ? "(none)" : original.Name;
				string result;
				try
				{
					ProductionGraph saved = new ProductionGraph();
					saved.DefaultAssemblerQuality = cache.DefaultQuality;
					saved.SelectedSurface = original;

					JObject json = JObject.Parse(JsonConvert.SerializeObject(saved));

					ProductionGraph loaded = new ProductionGraph();
					loaded.DefaultAssemblerQuality = cache.DefaultQuality;
					loaded.InsertNodesFromJson(cache, json, true);

					string back = loaded.SelectedSurface == null ? "(none)" : loaded.SelectedSurface.Name;
					result = (back == label) ? "ok" : ("MISMATCH -> " + back);
				}
				catch (Exception ex)
				{
					result = "THREW " + ex.GetType().Name;
				}
				report.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,-38} {1}", label, result));
			}
			report.AppendLine();
		}

		report.AppendLine("[mods]");
		foreach (KeyValuePair<string, string> mod in cache.IncludedMods.OrderBy(m => m.Key, StringComparer.Ordinal))
			report.AppendLine("  " + mod.Key + " " + mod.Value);

		Console.Out.Write(report.ToString());
		return 0;
	}

	static void Row(StringBuilder sb, string label, int value)
	{
		sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,-38} {1}", label, value));
	}
}
