using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Foreman
{
	/// <summary>
	/// Builds the "&lt;count&gt;x &lt;name&gt; [&lt;internal-name&gt;]" lines that the Foreman2 Task List mod parses.
	///
	/// The mod turns each line into a personal logistic request, so the internal name has to be the
	/// ITEM that places a building - not the entity. They are usually spelled the same, which is why
	/// the difference went unnoticed, but pyanodons' TURD machines are not: entity
	/// "fish-farm-mk01-turd" is placed by item "fish-farm-mk01", and Factorio has no item under the
	/// entity's name.
	///
	/// Several rows can collapse onto one line once the placing item is resolved - a TURD machine
	/// beside its plain form, one building at two qualities, or a module used in both assemblers and
	/// beacons - so everything accumulates by item name and is formatted once at the end.
	/// </summary>
	public class FactorioTaskListBuilder
	{
		private readonly Dictionary<string, double> counts = new Dictionary<string, double>();
		private readonly Dictionary<string, string> names = new Dictionary<string, string>();
		private readonly List<string> order = new List<string>(); //first-seen order, so the output follows the summary window's grouping

		/// <summary>
		/// The item that places this entity. Falls back to the entity's own name when nothing places
		/// it; the mod reports that case rather than requesting an item that does not exist.
		/// </summary>
		public static string GetPlacingItemName(EntityObjectBase entity)
		{
			return entity.AssociatedItems.FirstOrDefault()?.Name ?? entity.Name;
		}

		public void Add(string internalName, string friendlyName, double count)
		{
			if (string.IsNullOrWhiteSpace(internalName) || count <= 0)
				return;

			if (!counts.ContainsKey(internalName))
			{
				counts.Add(internalName, 0);
				names.Add(internalName, friendlyName);
				order.Add(internalName);
			}
			counts[internalName] += count;
		}

		public void AddAssembler(AssemblerQualityPair assembler, double count) { Add(GetPlacingItemName(assembler.Assembler), assembler.FriendlyName, count); }
		public void AddBeacon(BeaconQualityPair beacon, double count) { Add(GetPlacingItemName(beacon.Beacon), beacon.FriendlyName, count); }
		public void AddModule(ModuleQualityPair module, double count) { Add(module.Module.Name, module.FriendlyName, count); } //module names are already item names

		/// <summary>
		/// The mod parses "&lt;digits&gt;x " - group separators or an exponent make a line unparsable, so
		/// the count is always written as plain invariant digits.
		/// </summary>
		public List<string> ToLines()
		{
			return order.Select(key => string.Format("{0}x {1} [{2}]", counts[key].ToString("0", CultureInfo.InvariantCulture), names[key], key)).ToList();
		}
	}

	public static class FactorioTaskList
	{
		//the summary window splits buildings across four tabs; matching that grouping here keeps the
		//graph-view copy byte-identical to the summary window's copy for the same set of nodes
		private static readonly EntityType[] AssemblerTypes = { EntityType.Assembler };
		private static readonly EntityType[] MinerTypes = { EntityType.Miner, EntityType.OffshorePump };
		private static readonly EntityType[] PowerTypes = { EntityType.Boiler, EntityType.BurnerGenerator, EntityType.Generator, EntityType.Reactor };

		/// <summary>
		/// Task list lines for an arbitrary set of nodes. Every quantity involved is per-node and
		/// additive, so a partial selection yields a correct partial total.
		/// </summary>
		public static List<string> BuildLines(IEnumerable<ReadOnlyBaseNode> nodes)
		{
			List<ReadOnlyRecipeNode> rnodes = nodes.OfType<ReadOnlyRecipeNode>().ToList();
			FactorioTaskListBuilder builder = new FactorioTaskListBuilder();

			AddBuildings(rnodes, AssemblerTypes, builder);
			AddBuildings(rnodes, MinerTypes, builder);
			AddBuildings(rnodes, PowerTypes, builder);
			AddBeacons(rnodes, builder);
			AddModules(rnodes, false, builder);
			AddModules(rnodes, true, builder);

			return builder.ToLines();
		}

		private static void AddBuildings(IEnumerable<ReadOnlyRecipeNode> rnodes, EntityType[] types, FactorioTaskListBuilder builder)
		{
			Dictionary<AssemblerQualityPair, int> counters = new Dictionary<AssemblerQualityPair, int>();
			foreach (ReadOnlyRecipeNode rnode in rnodes.Where(n => types.Contains(n.SelectedAssembler.Assembler.EntityType)))
			{
				if (!counters.ContainsKey(rnode.SelectedAssembler))
					counters.Add(rnode.SelectedAssembler, 0);
				counters[rnode.SelectedAssembler] += (int)Math.Ceiling(rnode.ActualSetValue);
			}

			foreach (AssemblerQualityPair assembler in SortBuildings(counters.Keys))
				builder.AddAssembler(assembler, counters[assembler]);
		}

		private static void AddBeacons(IEnumerable<ReadOnlyRecipeNode> rnodes, FactorioTaskListBuilder builder)
		{
			Dictionary<BeaconQualityPair, int> counters = new Dictionary<BeaconQualityPair, int>();
			foreach (ReadOnlyRecipeNode rnode in rnodes.Where(n => n.SelectedBeacon))
			{
				if (!counters.ContainsKey(rnode.SelectedBeacon))
					counters.Add(rnode.SelectedBeacon, 0);
				counters[rnode.SelectedBeacon] += rnode.GetTotalBeacons();
			}

			foreach (BeaconQualityPair beacon in counters.Keys.OrderByDescending(b => b.Beacon.Available).ThenBy(b => b.Beacon.FriendlyName).ThenBy(b => b.Quality.Level).ThenBy(b => b.Quality.FriendlyName))
				builder.AddBeacon(beacon, counters[beacon]);
		}

		private static void AddModules(IEnumerable<ReadOnlyRecipeNode> rnodes, bool beaconModules, FactorioTaskListBuilder builder)
		{
			Dictionary<ModuleQualityPair, int> counters = new Dictionary<ModuleQualityPair, int>();
			foreach (ReadOnlyRecipeNode rnode in rnodes)
			{
				IReadOnlyList<ModuleQualityPair> modules = beaconModules ? rnode.BeaconModules : rnode.AssemblerModules;
				if (modules.Count == 0 || (beaconModules && !rnode.SelectedBeacon))
					continue;

				int holders = beaconModules ? rnode.GetTotalBeacons() : (int)Math.Ceiling(rnode.ActualSetValue);
				foreach (ModuleQualityPair module in modules) //one entry per filled module slot -> duplicates are intentional
				{
					if (!counters.ContainsKey(module))
						counters.Add(module, 0);
					counters[module] += holders;
				}
			}

			foreach (ModuleQualityPair module in counters.Keys.OrderByDescending(m => m.Module.Available).ThenBy(m => m.Module.FriendlyName).ThenBy(m => m.Quality.Level).ThenBy(m => m.Quality.FriendlyName))
				builder.AddModule(module, counters[module]);
		}

		private static IEnumerable<AssemblerQualityPair> SortBuildings(IEnumerable<AssemblerQualityPair> assemblers)
		{
			return assemblers.OrderByDescending(a => a.Assembler.Available).ThenBy(a => a.Assembler.FriendlyName).ThenBy(a => a.Quality.Level).ThenBy(a => a.Quality.FriendlyName);
		}
	}
}
