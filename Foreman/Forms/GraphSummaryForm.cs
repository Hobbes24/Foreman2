using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Foreman
{
	public partial class GraphSummaryForm : Form
	{
		protected class ItemCounter
		{
			public double Input { get; set; }
			public double InputUnlinked { get; set; }
			public double Output { get; set; }
			public double OutputUnlinked { get; set; }
			public double OutputOverflow { get; set; }
			public double Production { get; set; }
			public double Consumption { get; set; }

			public ItemCounter(double i, double iu, double o, double ou, double oo, double p, double c) { Input = i; InputUnlinked = iu; Output = o; OutputUnlinked = ou; OutputOverflow = oo; Production = p; Consumption = c; }
		}


		private List<ListViewItem> unfilteredAssemblerList;
		private List<ListViewItem> unfilteredMinerList;
		private List<ListViewItem> unfilteredPowerList;
		private List<ListViewItem> unfilteredBeaconList;
		private List<ListViewItem> unfilteredModuleList;
		private List<ListViewItem> unfilteredBeaconModuleList;

		private List<ListViewItem> unfilteredItemsList;
		private List<ListViewItem> unfilteredFluidsList;
		private List<ListViewItem> unfilteredAllList;

		private List<ListViewItem> unfilteredKeyNodesList;

		private List<ListViewItem> filteredAssemblerList;
		private List<ListViewItem> filteredMinerList;
		private List<ListViewItem> filteredPowerList;
		private List<ListViewItem> filteredBeaconList;
		private List<ListViewItem> filteredModuleList;
		private List<ListViewItem> filteredBeaconModuleList;

		private List<ListViewItem> filteredItemsList;
		private List<ListViewItem> filteredFluidsList;
		private List<ListViewItem> filteredAllList;

		private List<ListViewItem> filteredKeyNodesList;

		private Timer exportFlashTimer;

		private Dictionary<ListView, int> lastSortOrder; //int is +ve if sorted down, -ve if sorted up, |value| is the column # (starts from 1 due to 0 not having a sign) of the sort.

		private readonly ProductionGraph graph;

        private readonly ProductionGraphViewer graphViewer;

        private object navLastTarget = null; //the row that was last double clicked (item or module) - a repeat click on it steps to the next matching node
        private int navCycleIndex = 0;

        private string rateString;

		private string itemsTabBaseText;
		private string buildingCountBaseText;
		private string beaconCountBaseText;
		private string moduleCountBaseText;
		private string powerConsumptionBaseText;
		private string powerProductionBaseText;

		private static readonly Color AvailableObjectColor = Color.White;
		private static readonly Color UnavailableObjectColor = Color.Pink;

        public GraphSummaryForm(ProductionGraph graph, ProductionGraphViewer viewer)
        {
			InitializeComponent();
			MainForm.SetDoubleBuffered(AssemblerListView);
			MainForm.SetDoubleBuffered(MinerListView);
			MainForm.SetDoubleBuffered(PowerListView);
			MainForm.SetDoubleBuffered(BeaconListView);
			MainForm.SetDoubleBuffered(ModuleListView);
			MainForm.SetDoubleBuffered(BeaconModuleListView);
			MainForm.SetDoubleBuffered(ItemsListView);
			MainForm.SetDoubleBuffered(FluidsListView);
			MainForm.SetDoubleBuffered(AllListView);
			MainForm.SetDoubleBuffered(KeyNodesListView);

			// Capture designer-set base texts before any appending
			itemsTabBaseText = ItemsTabPage.Text;
			buildingCountBaseText = BuildingCountLabel.Text;
			beaconCountBaseText = BeaconCountLabel.Text;
			moduleCountBaseText = ModuleCountLabel.Text;
			powerConsumptionBaseText = PowerConsumptionLabel.Text;
			powerProductionBaseText = PowerProductionLabel.Text;

			unfilteredAssemblerList = new List<ListViewItem>();
			unfilteredMinerList = new List<ListViewItem>();
			unfilteredPowerList = new List<ListViewItem>();
			unfilteredBeaconList = new List<ListViewItem>();
			unfilteredModuleList = new List<ListViewItem>();
			unfilteredBeaconModuleList = new List<ListViewItem>();
			unfilteredItemsList = new List<ListViewItem>();
			unfilteredFluidsList = new List<ListViewItem>();
			unfilteredAllList = new List<ListViewItem>();
			unfilteredKeyNodesList = new List<ListViewItem>();

			filteredAssemblerList = new List<ListViewItem>();
			filteredMinerList = new List<ListViewItem>();
			filteredPowerList = new List<ListViewItem>();
			filteredBeaconList = new List<ListViewItem>();
			filteredModuleList = new List<ListViewItem>();
			filteredBeaconModuleList = new List<ListViewItem>();
			filteredItemsList = new List<ListViewItem>();
			filteredFluidsList = new List<ListViewItem>();
			filteredAllList = new List<ListViewItem>();
			filteredKeyNodesList = new List<ListViewItem>();

			lastSortOrder = new Dictionary<ListView, int>();
			lastSortOrder.Add(AssemblerListView, 2);
			lastSortOrder.Add(MinerListView, 2);
			lastSortOrder.Add(PowerListView, 2);
			lastSortOrder.Add(BeaconListView, 2);
			lastSortOrder.Add(ModuleListView, 2);
			lastSortOrder.Add(BeaconModuleListView, 2);
			lastSortOrder.Add(ItemsListView, 1);
			lastSortOrder.Add(FluidsListView, 1);
			lastSortOrder.Add(AllListView, 1);
			lastSortOrder.Add(KeyNodesListView, 1);

			this.graph = graph;

            this.graphViewer = viewer;

            ItemsListView.DoubleClick += ItemsOrFluids_DoubleClick;
            FluidsListView.DoubleClick += ItemsOrFluids_DoubleClick;
            AllListView.DoubleClick += ItemsOrFluids_DoubleClick;
            KeyNodesListView.DoubleClick += KeyNodes_DoubleClick;
            ModuleListView.DoubleClick += Modules_DoubleClick;
            BeaconModuleListView.DoubleClick += Modules_DoubleClick;

            graph.NodeAdded += Graph_Changed;
			graph.NodeDeleted += Graph_Changed;
			graph.LinkAdded += Graph_Changed;
			graph.LinkDeleted += Graph_Changed;
			graph.NodeValuesUpdated += Graph_Changed;

			this.FormClosed += GraphSummaryForm_FormClosed;

			RefreshData();
		}

        //centers the graph on one of the matching nodes; repeat calls with the same navTarget step through the rest of them
        private void CycleToNode(object navTarget, List<ReadOnlyBaseNode> targets)
        {
            if (targets.Count == 0) return;

            if (navLastTarget == null || !navLastTarget.Equals(navTarget))
            {
                navLastTarget = navTarget;
                navCycleIndex = 0;
            }
            else
            {
                navCycleIndex = (navCycleIndex + 1) % targets.Count;
            }

            if (graphViewer.NodeElementDictionary.TryGetValue(targets[navCycleIndex], out BaseNodeElement element))
                graphViewer.CenterOnNode(element, targetScale: 1.0f);
        }

        private void ItemsOrFluids_DoubleClick(object sender, EventArgs e)
        {
            if (graphViewer == null) return;
            ListView lv = (ListView)sender;

            Point hit = lv.PointToClient(Cursor.Position);
            ListViewHitTestInfo info = lv.HitTest(hit);
            if (info.Item == null || !(info.Item.Tag is ItemQualityPair item)) return;

            List<ReadOnlyBaseNode> targets = graph.Nodes
                .Where(n =>
                    (n.Inputs.Contains(item) && !n.InputLinks.Any(l => l.Item == item)) ||
                    (n.Outputs.Contains(item) && !n.OutputLinks.Any(l => l.Item == item)))
                .ToList();

            CycleToNode(item, targets);
        }

        private void Modules_DoubleClick(object sender, EventArgs e)
        {
            if (graphViewer == null) return;
            ListView lv = (ListView)sender;
            bool beaconModules = (lv == BeaconModuleListView);

            Point hit = lv.PointToClient(Cursor.Position);
            ListViewHitTestInfo info = lv.HitTest(hit);
            if (info.Item == null || !(info.Item.Tag is ModuleQualityPair module)) return;

            List<ReadOnlyBaseNode> targets = graph.Nodes
                .Where(n => n is ReadOnlyRecipeNode rNode && (beaconModules ?
                    rNode.SelectedBeacon && rNode.BeaconModules.Contains(module) :
                    rNode.AssemblerModules.Contains(module)))
                .ToList();

            //the same module in the assembler & beacon lists points at different nodes -> keep their cycles apart
            CycleToNode(Tuple.Create(module, beaconModules), targets);
        }

        private void KeyNodes_DoubleClick(object sender, EventArgs e)
        {
            if (graphViewer == null) return;

            Point hit = KeyNodesListView.PointToClient(Cursor.Position);
            ListViewHitTestInfo info = KeyNodesListView.HitTest(hit);
            if (info.Item == null || !(info.Item.Tag is ReadOnlyBaseNode node)) return;

            if (graphViewer.NodeElementDictionary.TryGetValue(node, out BaseNodeElement element))
                graphViewer.CenterOnNode(element);
        }

        private void GraphSummaryForm_FormClosed(object sender, FormClosedEventArgs e)
		{
			graph.NodeAdded -= Graph_Changed;
			graph.NodeDeleted -= Graph_Changed;
			graph.LinkAdded -= Graph_Changed;
			graph.LinkDeleted -= Graph_Changed;
			graph.NodeValuesUpdated -= Graph_Changed;
		}

		private void Graph_Changed(object sender, EventArgs e)
		{
			if (InvokeRequired)
				BeginInvoke(new Action(RefreshData));
			else
				RefreshData();
		}

		private void RefreshData()
		{
			rateString = graph.GetRateName();

			unfilteredAssemblerList.Clear();
			unfilteredMinerList.Clear();
			unfilteredPowerList.Clear();
			unfilteredBeaconList.Clear();
			unfilteredModuleList.Clear();
			unfilteredBeaconModuleList.Clear();
			unfilteredItemsList.Clear();
			unfilteredFluidsList.Clear();
			unfilteredAllList.Clear();
			unfilteredKeyNodesList.Clear();

			IconList.Images.Clear();
			IconList.Images.Add(DataCache.UnknownIcon);

			var nodes = graph.Nodes;
			var links = graph.NodeLinks;

			LoadUnfilteredSelectedAssemblerList(nodes.Where(n => n is ReadOnlyRecipeNode rNode && rNode.SelectedAssembler.Assembler.EntityType == EntityType.Assembler).Select(n => (ReadOnlyRecipeNode)n), unfilteredAssemblerList);
			LoadUnfilteredSelectedAssemblerList(nodes.Where(n => n is ReadOnlyRecipeNode rNode && (rNode.SelectedAssembler.Assembler.EntityType == EntityType.Miner || rNode.SelectedAssembler.Assembler.EntityType == EntityType.OffshorePump)).Select(n => (ReadOnlyRecipeNode)n), unfilteredMinerList);
			LoadUnfilteredSelectedAssemblerList(nodes.Where(n => n is ReadOnlyRecipeNode rNode && (rNode.SelectedAssembler.Assembler.EntityType == EntityType.Boiler || rNode.SelectedAssembler.Assembler.EntityType == EntityType.BurnerGenerator || rNode.SelectedAssembler.Assembler.EntityType == EntityType.Generator || rNode.SelectedAssembler.Assembler.EntityType == EntityType.Reactor)).Select(n => (ReadOnlyRecipeNode)n), unfilteredPowerList);
			LoadUnfilteredBeaconList(nodes.Where(n => n is ReadOnlyRecipeNode rNode && rNode.SelectedBeacon).Select(n => (ReadOnlyRecipeNode)n), unfilteredBeaconList);
			LoadUnfilteredModuleList(nodes.Where(n => n is ReadOnlyRecipeNode).Select(n => (ReadOnlyRecipeNode)n), false, unfilteredModuleList);
			LoadUnfilteredModuleList(nodes.Where(n => n is ReadOnlyRecipeNode).Select(n => (ReadOnlyRecipeNode)n), true, unfilteredBeaconModuleList);
			LoadUnfilteredItemLists(nodes, links, false, unfilteredItemsList);
			LoadUnfilteredItemLists(nodes, links, true, unfilteredFluidsList);
			unfilteredAllList.AddRange(unfilteredItemsList);
			unfilteredAllList.AddRange(unfilteredFluidsList);
			LoadUnfilteredKeyNodesList(nodes.Where(n => n.KeyNode), unfilteredKeyNodesList);

			double buildingTotal = nodes.Where(n => n is ReadOnlyRecipeNode).Sum(n => Math.Ceiling(((ReadOnlyRecipeNode)n).ActualSetValue));
			double beaconTotal = nodes.Where(n => n is ReadOnlyRecipeNode).Sum(n => ((ReadOnlyRecipeNode)n).GetTotalBeacons());
			double moduleTotal = nodes.Where(n => n is ReadOnlyRecipeNode).Sum(n => GetNodeModuleTotal((ReadOnlyRecipeNode)n, false) + GetNodeModuleTotal((ReadOnlyRecipeNode)n, true));
			BuildingCountLabel.Text = buildingCountBaseText + GraphicsStuff.DoubleToString(buildingTotal);
			BeaconCountLabel.Text = beaconCountBaseText + GraphicsStuff.DoubleToString(beaconTotal);
			ModuleCountLabel.Text = moduleCountBaseText + GraphicsStuff.DoubleToString(moduleTotal);

			double powerConsumption = nodes.Where(n => n is ReadOnlyRecipeNode).Sum(n => ((ReadOnlyRecipeNode)n).GetTotalAssemblerElectricalConsumption() + ((ReadOnlyRecipeNode)n).GetTotalBeaconElectricalConsumption());
			double powerProduction = nodes.Where(n => n is ReadOnlyRecipeNode).Sum(n => ((ReadOnlyRecipeNode)n).GetTotalGeneratorElectricalProduction());
			PowerConsumptionLabel.Text = powerConsumptionBaseText + GraphicsStuff.DoubleToEnergy(powerConsumption, "W");
			PowerProductionLabel.Text = powerProductionBaseText + GraphicsStuff.DoubleToEnergy(powerProduction, "W");

			ItemsTabPage.Text = itemsTabBaseText + " ( per " + rateString + ")";

			UpdateFilteredBuildingLists();
			UpdateFilteredItemsLists();
			UpdateFilteredKeyNodesList();
		}

		//-------------------------------------------------------------------------------------------------------Initial list initialization

		private void LoadUnfilteredSelectedAssemblerList(IEnumerable<ReadOnlyRecipeNode> origin, List<ListViewItem> lviList)
		{
			Dictionary<AssemblerQualityPair, int> buildingCounters = new Dictionary<AssemblerQualityPair, int>();
			Dictionary<AssemblerQualityPair, Tuple<double, double>> buildingElectricalPower = new Dictionary<AssemblerQualityPair, Tuple<double, double>>(); //power for buildings, power for beacons)

			foreach(ReadOnlyRecipeNode rnode in origin)
			{
				if (!buildingCounters.ContainsKey(rnode.SelectedAssembler))
				{
					buildingCounters.Add(rnode.SelectedAssembler, 0);
					buildingElectricalPower.Add(rnode.SelectedAssembler, new Tuple<double, double>(0,0));
				}
				buildingCounters[rnode.SelectedAssembler] += (int)Math.Ceiling(rnode.ActualSetValue); //should probably check the validity of ceiling in case of near correct (ex: 1.0001 assemblers should really be counted as 1 instead of 2)
				Tuple<double, double> oldValues = buildingElectricalPower[rnode.SelectedAssembler];
				buildingElectricalPower[rnode.SelectedAssembler] = new Tuple<double,double>(oldValues.Item1 + rnode.GetTotalGeneratorElectricalProduction() + rnode.GetTotalAssemblerElectricalConsumption(), oldValues.Item2 + rnode.GetTotalBeaconElectricalConsumption());
			}

			foreach (AssemblerQualityPair assembler in buildingCounters.Keys.OrderByDescending(a => a.Assembler.Available).ThenBy(a => a.Assembler.FriendlyName).ThenBy(a => a.Quality.Level).ThenBy(a => a.Quality.FriendlyName))
			{
				ListViewItem lvItem = new ListViewItem();
				if (assembler.Assembler.Icon != null)
				{
					IconList.Images.Add(assembler.Icon);
					lvItem.ImageIndex = IconList.Images.Count - 1;
				}
				else
				{
					lvItem.ImageIndex = 0;
				}

				lvItem.Text = buildingCounters[assembler] >= 10000000? buildingCounters[assembler].ToString("0.##e0") : buildingCounters[assembler].ToString("N0");
				lvItem.Tag = assembler;
				lvItem.Name = assembler.Assembler.Name + ":" + assembler.Quality.Name; //key
				lvItem.BackColor = assembler.Assembler.Available ? AvailableObjectColor : UnavailableObjectColor;
				lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = assembler.FriendlyName });
				lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = buildingElectricalPower[assembler].Item1 == 0 ? "-" : GraphicsStuff.DoubleToEnergy(buildingElectricalPower[assembler].Item1, "W"), Tag = buildingElectricalPower[assembler].Item1 });
				lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = buildingElectricalPower[assembler].Item2 == 0 ? "-" : GraphicsStuff.DoubleToEnergy(buildingElectricalPower[assembler].Item2, "W"), Tag = buildingElectricalPower[assembler].Item2 });
				lviList.Add(lvItem);
			}
		}

		private void LoadUnfilteredBeaconList(IEnumerable<ReadOnlyRecipeNode> origin, List<ListViewItem> lviList)
		{
			Dictionary<BeaconQualityPair, int> beaconCounters = new Dictionary<BeaconQualityPair, int>();

			foreach (ReadOnlyRecipeNode rnode in origin)
			{
				if (!rnode.SelectedBeacon)
					continue;

				if (!beaconCounters.ContainsKey(rnode.SelectedBeacon))
					beaconCounters.Add(rnode.SelectedBeacon, 0);
				beaconCounters[rnode.SelectedBeacon] += rnode.GetTotalBeacons();
			}

			foreach (BeaconQualityPair beacon in beaconCounters.Keys.OrderByDescending(b => b.Beacon.Available).ThenBy(b => b.Beacon.FriendlyName).ThenBy(b => b.Quality.Level).ThenBy(b => b.Quality.FriendlyName))
			{
				ListViewItem lvItem = new ListViewItem();
				if (beacon.Icon != null)
				{
					IconList.Images.Add(beacon.Icon);
					lvItem.ImageIndex = IconList.Images.Count - 1;
				}
				else
				{
					lvItem.ImageIndex = 0;
				}

				lvItem.Text = beaconCounters[beacon].ToString();
				lvItem.Tag = beacon;
				lvItem.Name = beacon.Beacon.Name + ":" + beacon.Quality.Name; //key
				lvItem.BackColor = beacon.Beacon.Available ? AvailableObjectColor : UnavailableObjectColor;
				lvItem.SubItems.Add(beacon.FriendlyName);
				double beaconPowerConsumption = beaconCounters[beacon] * (beacon.Beacon.GetEnergyConsumption(beacon.Quality) + beacon.Beacon.GetEnergyDrain());  //QUALITY UPDATE REQUIRED
				lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = beaconCounters[beacon] == 0 ? "-" : GraphicsStuff.DoubleToEnergy(beaconPowerConsumption, "W"), Tag = beaconPowerConsumption });
				lviList.Add(lvItem);
			}
		}

		//number of module items required to fill every assembler (or every beacon) of a single recipe node
		private static int GetNodeModuleTotal(ReadOnlyRecipeNode rnode, bool beaconModules)
		{
			if (beaconModules)
				return rnode.SelectedBeacon ? rnode.BeaconModules.Count * rnode.GetTotalBeacons() : 0;
			return rnode.AssemblerModules.Count * (int)Math.Ceiling(rnode.ActualSetValue);
		}

		private void LoadUnfilteredModuleList(IEnumerable<ReadOnlyRecipeNode> origin, bool beaconModules, List<ListViewItem> lviList)
		{
			Dictionary<ModuleQualityPair, int> moduleCounters = new Dictionary<ModuleQualityPair, int>();
			Dictionary<ModuleQualityPair, int> holderCounters = new Dictionary<ModuleQualityPair, int>(); //buildings (or beacons) that hold at least one of this module

			foreach (ReadOnlyRecipeNode rnode in origin)
			{
				IReadOnlyList<ModuleQualityPair> modules = beaconModules ? rnode.BeaconModules : rnode.AssemblerModules;
				if (modules.Count == 0 || (beaconModules && !rnode.SelectedBeacon))
					continue;

				int holders = beaconModules ? rnode.GetTotalBeacons() : (int)Math.Ceiling(rnode.ActualSetValue);

				foreach (ModuleQualityPair module in modules) //one entry per filled module slot -> duplicates are intentional
				{
					if (!moduleCounters.ContainsKey(module))
					{
						moduleCounters.Add(module, 0);
						holderCounters.Add(module, 0);
					}
					moduleCounters[module] += holders;
				}
				foreach (ModuleQualityPair module in modules.Distinct())
					holderCounters[module] += holders;
			}

			foreach (ModuleQualityPair module in moduleCounters.Keys.OrderByDescending(m => m.Module.Available).ThenBy(m => m.Module.FriendlyName).ThenBy(m => m.Quality.Level).ThenBy(m => m.Quality.FriendlyName))
			{
				ListViewItem lvItem = new ListViewItem();
				if (module.Module.Icon != null)
				{
					IconList.Images.Add(module.Icon);
					lvItem.ImageIndex = IconList.Images.Count - 1;
				}
				else
				{
					lvItem.ImageIndex = 0;
				}

				lvItem.Text = moduleCounters[module] >= 10000000 ? moduleCounters[module].ToString("0.##e0") : moduleCounters[module].ToString("N0");
				lvItem.SubItems[0].Tag = (double)moduleCounters[module];
				lvItem.Tag = module;
				lvItem.Name = module.Module.Name + ":" + module.Quality.Name; //key
				lvItem.BackColor = module.Module.Available ? AvailableObjectColor : UnavailableObjectColor;
				lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = module.FriendlyName });
				lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = holderCounters[module].ToString("N0"), Tag = (double)holderCounters[module] });
				lviList.Add(lvItem);
			}
		}

		private void LoadUnfilteredItemLists(IEnumerable<ReadOnlyBaseNode> nodes, IEnumerable<ReadOnlyNodeLink> links, bool fluids, List<ListViewItem> lviList)
		{
			//NOTE: throughput is initially calculatated as all non-overflow linked input & output of each recipe node. At the end we will add
			Dictionary<ItemQualityPair, ItemCounter> itemCounters = new Dictionary<ItemQualityPair, ItemCounter>();

			foreach (ReadOnlyBaseNode node in nodes)
			{
                if (node is ReadOnlyRecipeNode || node is ReadOnlySpoilNode || node is ReadOnlyPlantNode)
                {
					foreach (ItemQualityPair input in node.Inputs.Where(i => fluids.Equals(i.Item is Fluid)))
					{
						if (!itemCounters.ContainsKey(input))
							itemCounters.Add(input, new ItemCounter(0, 0, 0, 0, 0, 0, 0));

						double consumeRate = node.GetConsumeRate(input);
						if (consumeRate > 0)
						{
							if (!node.InputLinks.Any(l => l.Item == input))
								itemCounters[input].InputUnlinked += consumeRate;
							else
								itemCounters[input].Consumption += consumeRate;
						}
					}

					foreach (ItemQualityPair output in node.Outputs.Where(i => fluids.Equals(i.Item is Fluid)))
					{
						if (!itemCounters.ContainsKey(output))
							itemCounters.Add(output, new ItemCounter(0, 0, 0, 0, 0, 0, 0));

						double supplyRate = node.GetSupplyRate(output);
						bool isOverProduced = node.IsOverproducing(output);
						double supplyUsedRate = isOverProduced ? node.GetSupplyUsedRate(output) : supplyRate;

						if (supplyRate > 0)
						{
							if (!node.OutputLinks.Any(l => l.Item == output))
								itemCounters[output].OutputUnlinked += supplyRate;

							itemCounters[output].Production += supplyRate;
							if (isOverProduced)
								itemCounters[output].OutputOverflow += supplyRate - supplyUsedRate;
						}
					}
				}

				else if(node is ReadOnlySupplierNode sNode && fluids.Equals(sNode.SuppliedItem.Item is Fluid))
				{
					if (!itemCounters.ContainsKey(sNode.SuppliedItem))
						itemCounters.Add(sNode.SuppliedItem, new ItemCounter(0, 0, 0, 0, 0, 0, 0));
					itemCounters[sNode.SuppliedItem].Input += sNode.ActualRate;
				}

				else if(node is ReadOnlyConsumerNode cNode && fluids.Equals(cNode.ConsumedItem.Item is Fluid))
				{
					if (!itemCounters.ContainsKey(cNode.ConsumedItem))
						itemCounters.Add(cNode.ConsumedItem, new ItemCounter(0, 0, 0, 0, 0, 0, 0));
					itemCounters[cNode.ConsumedItem].Output += cNode.ActualRate;
				}
			}

			foreach (ItemQualityPair item in itemCounters.Keys.OrderBy(a => a.Item.FriendlyName).ThenBy(a => a.Quality.Level).ThenBy(a => a.Quality.FriendlyName))
			{
				ListViewItem lvItem = new ListViewItem();
				if (item.Icon != null)
				{
					IconList.Images.Add(item.Icon);
					lvItem.ImageIndex = IconList.Images.Count - 1;
				}
				else
				{
					lvItem.ImageIndex = 0;
				}

				lvItem.Text = item.FriendlyName;
				lvItem.Tag = item;
				lvItem.Name = item.Item.Name + ":" + item.Quality.Name; //key
				lvItem.BackColor = item.Item.Available ? AvailableObjectColor : UnavailableObjectColor;
				lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = itemCounters[item].Input == 0 ? "-" : GraphicsStuff.DoubleToString(itemCounters[item].Input), Tag = itemCounters[item].Input });
				lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = itemCounters[item].InputUnlinked == 0 ? "-" : GraphicsStuff.DoubleToString(itemCounters[item].InputUnlinked), Tag = itemCounters[item].InputUnlinked});
				lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = itemCounters[item].Output == 0 ? "-" : GraphicsStuff.DoubleToString(itemCounters[item].Output), Tag = itemCounters[item].Output });
				lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = itemCounters[item].OutputUnlinked == 0 ? "-" : GraphicsStuff.DoubleToString(itemCounters[item].OutputUnlinked), Tag = itemCounters[item].OutputUnlinked });
				lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = itemCounters[item].OutputOverflow == 0 ? "-" : GraphicsStuff.DoubleToString(itemCounters[item].OutputOverflow), Tag = itemCounters[item].OutputOverflow });
				lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = itemCounters[item].Production == 0 ? "-" : GraphicsStuff.DoubleToString(itemCounters[item].Production), Tag = itemCounters[item].Production });
				lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = itemCounters[item].Consumption == 0 ? "-" : GraphicsStuff.DoubleToString(itemCounters[item].Consumption), Tag = itemCounters[item].Consumption });
				lviList.Add(lvItem);
			}
		}

		private void LoadUnfilteredKeyNodesList(IEnumerable<ReadOnlyBaseNode> origin, List<ListViewItem> lviList)
		{
			foreach (ReadOnlyBaseNode node in origin)
			{
				ListViewItem lvItem = new ListViewItem();

				Bitmap icon;
				string nodeText;
				string nodeType;
				if (node is ReadOnlyConsumerNode cNode)
				{
					icon = cNode.ConsumedItem.Icon;
					nodeText = cNode.ConsumedItem.FriendlyName;
					nodeType = "Consumer";
				}
				else if (node is ReadOnlySupplierNode sNode)
				{
					icon = sNode.SuppliedItem.Icon;
					nodeText = sNode.SuppliedItem.FriendlyName;
					nodeType = "Supplier";
				}
				else if (node is ReadOnlyPassthroughNode pNode)
				{
					icon = pNode.PassthroughItem.Icon;
					nodeText = pNode.PassthroughItem.FriendlyName;
					nodeType = "Passthrough";
				}
				else if (node is ReadOnlyRecipeNode rNode)
				{
					icon = rNode.BaseRecipe.Icon;
					nodeText = rNode.BaseRecipe.FriendlyName;
					nodeType = "Recipe";
				}
				else if (node is ReadOnlySpoilNode spNode)
                {
                    icon = spNode.InputItem.Icon;
                    nodeText = spNode.InputItem.FriendlyName + " spoiling";
                    nodeType = "Spoil";
                }
				else if (node is ReadOnlyPlantNode plNode)
                {
                    icon = plNode.Seed.Icon;
                    nodeText = plNode.Seed.FriendlyName + " planting";
                    nodeType = "Plant";
                }
				else
					continue;

				if (icon != null)
				{
					IconList.Images.Add(icon);
					lvItem.ImageIndex = IconList.Images.Count - 1;
				}
				else
				{
					lvItem.ImageIndex = 0;
				}

				lvItem.Text = nodeType;
				lvItem.Tag = node;
				lvItem.Name = nodeText; //key
				lvItem.BackColor = AvailableObjectColor;
				lvItem.SubItems.Add(nodeText);
				lvItem.SubItems.Add(node.KeyNodeTitle);

				if(node is ReadOnlyRecipeNode rrNode)
				{
					lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = "-", Tag = (double)0 });
					lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = GraphicsStuff.DoubleToString(rrNode.ActualSetValue), Tag = rrNode.ActualSetValue });
				}
				else
				{
					lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = GraphicsStuff.DoubleToString(node.ActualRate), Tag = node.ActualRate });
					lvItem.SubItems.Add(new ListViewItem.ListViewSubItem() { Text = "-", Tag = (double)0 });
				}
				lviList.Add(lvItem);
			}
		}

		//-------------------------------------------------------------------------------------------------------Filter functions

		private void UpdateFilteredBuildingLists()
		{
			UpdateFilteredBuildingList(unfilteredAssemblerList, filteredAssemblerList, AssemblerListView);
			UpdateFilteredBuildingList(unfilteredMinerList, filteredMinerList, MinerListView);
			UpdateFilteredBuildingList(unfilteredPowerList, filteredPowerList, PowerListView);
			UpdateFilteredBuildingList(unfilteredBeaconList, filteredBeaconList, BeaconListView);
			UpdateFilteredBuildingList(unfilteredModuleList, filteredModuleList, ModuleListView);
			UpdateFilteredBuildingList(unfilteredBeaconModuleList, filteredBeaconModuleList, BeaconModuleListView);
		}

		//tags of the building/module lists are quality pair structs -> they dont share a common base to pull the name from
		private static string GetTagLFriendlyName(object tag)
		{
			switch (tag)
			{
				case AssemblerQualityPair assembler: return assembler.Assembler.LFriendlyName;
				case BeaconQualityPair beacon: return beacon.Beacon.LFriendlyName;
				case ModuleQualityPair module: return module.Module.LFriendlyName;
				case ItemQualityPair item: return item.Item.LFriendlyName;
				case DataObjectBase dob: return dob.LFriendlyName;
				default: return "";
			}
		}

		private void UpdateFilteredBuildingList(List<ListViewItem> unfilteredList, List<ListViewItem> filteredList, ListView owner)
		{
			string filterString = BuildingsFilterTextBox.Text.ToLower();

			filteredList.Clear();

			foreach (ListViewItem lvItem in unfilteredList)
				if (string.IsNullOrEmpty(filterString) || GetTagLFriendlyName(lvItem.Tag).Contains(filterString))
					filteredList.Add(lvItem);

			owner.VirtualListSize = filteredList.Count;
			owner.Invalidate();
		}

		private void UpdateFilteredItemsLists()
		{
			UpdateFilteredItemsList(unfilteredItemsList, filteredItemsList, ItemsListView);
			UpdateFilteredItemsList(unfilteredFluidsList, filteredFluidsList, FluidsListView);
			UpdateFilteredItemsList(unfilteredAllList, filteredAllList, AllListView);
		}

		private void UpdateFilteredItemsList(List<ListViewItem> unfilteredList, List<ListViewItem> filteredList, ListView owner)
		{
			string filterString = ItemsFilterTextBox.Text.ToLower();
			bool includeInputs = ItemFilterInputCheckBox.Checked;
			bool includeInputUnlinked = ItemFilterInputUnlinkedCheckBox.Checked;
			bool includeOutputs = ItemFilterOutputCheckBox.Checked;
			bool includeOutputsUnlinked = ItemFilterOutputUnlinkedCheckBox.Checked;
			bool includeOutputsOverflow = ItemFilterOutputOverproducedCheckBox.Checked;
			bool includeProduced = ItemFilterProductionCheckBox.Checked;
			bool includeConsumed = ItemFilterConsumptionCheckBox.Checked;

			filteredList.Clear();

            foreach (ListViewItem lvItem in unfilteredList)
            {
                if (string.IsNullOrEmpty(filterString) || ((ItemQualityPair)lvItem.Tag).Item.LFriendlyName.Contains(filterString))
                {
                    if ((includeInputs && lvItem.SubItems[1].Text != "-") ||
                        (includeInputUnlinked && lvItem.SubItems[2].Text != "-") ||
                        (includeOutputs && lvItem.SubItems[3].Text != "-") ||
                        (includeOutputsUnlinked && lvItem.SubItems[4].Text != "-") ||
                        (includeOutputsOverflow && lvItem.SubItems[5].Text != "-") ||
                        (includeProduced && lvItem.SubItems[6].Text != "-") ||
                        (includeConsumed && lvItem.SubItems[7].Text != "-"))
                    {
                        filteredList.Add(lvItem);
                    }
                }
            }

            owner.VirtualListSize = filteredList.Count;
			owner.Invalidate();
		}

		private void UpdateFilteredKeyNodesList()
		{
			string filterString = KeyNodesFilterTextBox.Text.ToLower();
			bool includeSuppliers = SupplierNodeFilterCheckBox.Checked;
			bool includeConsumers = ConsumerNodeFilterCheckBox.Checked;
			bool includePassthrough = PassthroughNodeFilterCheckBox.Checked;
			bool includeRecipe = RecipeNodeFilterCheckBox.Checked;

			filteredKeyNodesList.Clear();

			foreach (ListViewItem lvItem in unfilteredKeyNodesList)
			{
				if (string.IsNullOrEmpty(filterString) || lvItem.Text.ToLower().Contains(filterString) || lvItem.SubItems[1].Text.ToLower().Contains(filterString) || lvItem.SubItems[2].Text.ToLower().Contains(filterString))
				{
					if ((includeSuppliers && (lvItem.Tag is ReadOnlySupplierNode)) ||
						(includeConsumers && (lvItem.Tag is ReadOnlyConsumerNode)) ||
						(includePassthrough && (lvItem.Tag is ReadOnlyPassthroughNode)) ||
						(includeRecipe && (lvItem.Tag is ReadOnlyRecipeNode)))
					{
						filteredKeyNodesList.Add(lvItem);
					}
				}
			}

			KeyNodesListView.VirtualListSize = filteredKeyNodesList.Count;
			KeyNodesListView.Invalidate();
		}

		//-------------------------------------------------------------------------------------------------------Virtual item retrieval for all list views

		private void AssemblerListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { e.Item = filteredAssemblerList[e.ItemIndex]; }
		private void MinerListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { e.Item = filteredMinerList[e.ItemIndex]; }
		private void PowerListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { e.Item = filteredPowerList[e.ItemIndex]; }
		private void BeaconListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { e.Item = filteredBeaconList[e.ItemIndex]; }
		private void ModuleListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { e.Item = filteredModuleList[e.ItemIndex]; }
		private void BeaconModuleListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { e.Item = filteredBeaconModuleList[e.ItemIndex]; }
		private void ItemsListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { e.Item = filteredItemsList[e.ItemIndex]; }
		private void FluidsListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { e.Item = filteredFluidsList[e.ItemIndex]; }
		private void AllListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { e.Item = filteredAllList[e.ItemIndex]; }
		private void KeyNodesListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { e.Item = filteredKeyNodesList[e.ItemIndex]; }

		//-------------------------------------------------------------------------------------------------------Filter changed events

		private void BuildingsFilterTextBox_TextChanged(object sender, EventArgs e) { UpdateFilteredBuildingLists(); }

		private void ItemsFilterTextBox_TextChanged(object sender, EventArgs e) { UpdateFilteredItemsLists(); }
		private void ItemFilterCheckBox_CheckedChanged(object sender, EventArgs e) { UpdateFilteredItemsLists(); }

		private void KeyNodesFilterTextBox_TextChanged(object sender, EventArgs e) { UpdateFilteredKeyNodesList(); }
		private void KeyNodesFilterCheckBox_CheckedChanged(object sender, EventArgs e) { UpdateFilteredKeyNodesList(); }

		//-------------------------------------------------------------------------------------------------------Column clicked events

		private void AssemblerListView_ColumnClick(object sender, ColumnClickEventArgs e) { BuildingListView_ColumnSort(unfilteredAssemblerList, filteredAssemblerList, AssemblerListView, e.Column); }
		private void MinerListView_ColumnClick(object sender, ColumnClickEventArgs e) { BuildingListView_ColumnSort(unfilteredMinerList, filteredMinerList, MinerListView, e.Column); }
		private void PowerListView_ColumnClick(object sender, ColumnClickEventArgs e) { BuildingListView_ColumnSort(unfilteredPowerList, filteredPowerList, PowerListView, e.Column); }
		private void BeaconListView_ColumnClick(object sender, ColumnClickEventArgs e) { BuildingListView_ColumnSort(unfilteredBeaconList, filteredBeaconList, BeaconListView, e.Column); }
		private void ModuleListView_ColumnClick(object sender, ColumnClickEventArgs e) { BuildingListView_ColumnSort(unfilteredModuleList, filteredModuleList, ModuleListView, e.Column); }
		private void BeaconModuleListView_ColumnClick(object sender, ColumnClickEventArgs e) { BuildingListView_ColumnSort(unfilteredBeaconModuleList, filteredBeaconModuleList, BeaconModuleListView, e.Column); }

		private void BuildingListView_ColumnSort(List<ListViewItem> unfilteredList, List<ListViewItem> filteredList, ListView owner, int column)
		{
			int reverseSortLamda = (lastSortOrder[owner] == column + 1) ? -1 : 1; //last sort was this very column -> this is now a reverse sort
			lastSortOrder[owner] = reverseSortLamda * (column + 1);

			unfilteredList.Sort((a, b) =>
			{
				int result;
				if (column == 0)
					result = -double.Parse(a.Text).CompareTo(double.Parse(b.Text));
				else if (column == 1)
					result = a.SubItems[1].Text.ToLower().CompareTo(b.SubItems[1].Text.ToLower());
				else
					result = -((double)a.SubItems[column].Tag).CompareTo((double)b.SubItems[column].Tag);

				if (result == 0)
					result = a.SubItems[1].Text.ToLower().CompareTo(b.SubItems[1].Text.ToLower());
				if (result == 0)
					result = a.Name.CompareTo(b.Name);
				return result * reverseSortLamda;

			});

			UpdateFilteredBuildingList(unfilteredList, filteredList, owner);
			owner.Invalidate();
		}

		private void ItemsListView_ColumnClick(object sender, ColumnClickEventArgs e) { ItemListView_ColumnSort(unfilteredItemsList, filteredItemsList, ItemsListView, e.Column); }
		private void FluidsListView_ColumnClick(object sender, ColumnClickEventArgs e) { ItemListView_ColumnSort(unfilteredFluidsList, filteredFluidsList, FluidsListView, e.Column); }
		private void AllListView_ColumnClick(object sender, ColumnClickEventArgs e) { ItemListView_ColumnSort(unfilteredAllList, filteredAllList, AllListView, e.Column); }

		private void ItemListView_ColumnSort(List<ListViewItem> unfilteredList, List<ListViewItem> filteredList, ListView owner, int column)
		{
			int reverseSortLamda = (lastSortOrder[owner] == column + 1) ? -1 : 1; //last sort was this very column -> this is now a reverse sort
			lastSortOrder[owner] = reverseSortLamda * (column + 1);

			unfilteredList.Sort((a, b) =>
			{
				int result;
				if (column == 0)
					result = a.SubItems[0].Text.ToLower().CompareTo(b.SubItems[0].Text.ToLower());
				else
					result = -((double)a.SubItems[column].Tag).CompareTo((double)b.SubItems[column].Tag);

                if (result == 0)
                {
                    string aName = a.Tag is ItemQualityPair iqpA ? iqpA.Item.LFriendlyName : ((DataObjectBase)a.Tag).LFriendlyName;
                    string bName = b.Tag is ItemQualityPair iqpB ? iqpB.Item.LFriendlyName : ((DataObjectBase)b.Tag).LFriendlyName;
                    result = aName.CompareTo(bName);
                }
                if (result == 0)
                {
                    string aName = a.Tag is ItemQualityPair iqpA2 ? iqpA2.Item.Name : ((DataObjectBase)a.Tag).Name;
                    string bName = b.Tag is ItemQualityPair iqpB2 ? iqpB2.Item.Name : ((DataObjectBase)b.Tag).Name;
                    result = aName.CompareTo(bName);
                }
                return result * reverseSortLamda;
			});

			UpdateFilteredItemsList(unfilteredList, filteredList, owner);
			owner.Invalidate();
		}

		private void KeyNodesListView_ColumnClick(object sender, ColumnClickEventArgs e)
		{
			const int maxDigits = 20;
			Regex comparerRegex = new Regex(@"\d+", RegexOptions.Compiled);
			Dictionary<string, string> stringComparerProcessedStrings = new Dictionary<string, string>();
			int NaturalCompareStrings(string a, string b)
			{
				if (!stringComparerProcessedStrings.ContainsKey(a))
					stringComparerProcessedStrings.Add(a, comparerRegex.Replace(a.ToLower(), matcha => matcha.Value.PadLeft(maxDigits, '0')));
				if (!stringComparerProcessedStrings.ContainsKey(b))
					stringComparerProcessedStrings.Add(b, comparerRegex.Replace(b.ToLower(), matcha => matcha.Value.PadLeft(maxDigits, '0')));

				return stringComparerProcessedStrings[a].CompareTo(stringComparerProcessedStrings[b]);
			}

			int reverseSortLamda = (lastSortOrder[KeyNodesListView] == e.Column + 1) ? -1 : 1; //last sort was this very column -> this is now a reverse sort
			lastSortOrder[KeyNodesListView] = reverseSortLamda * (e.Column + 1);

			unfilteredKeyNodesList.Sort((a, b) =>
			{
				int result;
				if (e.Column == 2)
					result = NaturalCompareStrings(a.SubItems[2].Text, b.SubItems[2].Text);
				else if(e.Column < 3)
					result = a.SubItems[e.Column].Text.ToLower().CompareTo(b.SubItems[e.Column].Text.ToLower());
				else
					result =  -((double)a.SubItems[e.Column].Tag).CompareTo((double)b.SubItems[e.Column].Tag);

				if(result == 0 && e.Column != 2)
					result = NaturalCompareStrings(a.SubItems[2].Text, b.SubItems[2].Text);
				if(result == 0 && e.Column != 0)
					result = a.SubItems[0].Text.ToLower().CompareTo(b.SubItems[0].Text.ToLower());
				if (result == 0 && e.Column != 1)
					result = a.SubItems[1].Text.ToLower().CompareTo(b.SubItems[1].Text.ToLower());
				if (result == 0)
					result = ((ReadOnlyBaseNode)a.Tag).NodeID.CompareTo(((ReadOnlyBaseNode)b.Tag).NodeID);
				return result * reverseSortLamda;
			});

			UpdateFilteredKeyNodesList();
			KeyNodesListView.Invalidate();
		}

		//-------------------------------------------------------------------------------------------------------Export CSV functions

		private void BuildingsExportButton_Click(object sender, EventArgs e)
		{
			ExportCSV(
				new List<ListViewItem>[] { filteredAssemblerList, filteredMinerList, filteredPowerList, filteredBeaconList, filteredModuleList, filteredBeaconModuleList },
				new string[][] {
					new string[] { "#", "Assembler", "Electrical power consumed by assemblers (in W)", "Electrical power consumed by beacons (in W)" },
					new string[] { "#", "Miner", "Electrical power consumed by assemblers (in W)", "Electrical power consumed by beacons (in W)" },
					new string[] { "#", "Power Building", "Electrical power generated (in W)", "Electrical power consumed (in W)" },
					new string[] { "#", "Beacon", "Electrical power consumed by beacons (in W)" },
					new string[] { "#", "Module (in buildings)", "Buildings holding this module" },
					new string[] { "#", "Module (in beacons)", "Beacons holding this module" }
				});
		}

		private void ItemsExportButton_Click(object sender, EventArgs e)
		{
			ExportCSV(
				new List<ListViewItem>[] { filteredItemsList, filteredFluidsList },
				new string[][]
				{
					new string[] {"Item", "Input (per "+rateString+")", "Input through un-linked recipe ingredients (per "+rateString+")", "Output (per " + rateString + ")", "Output through un-linked recipe products (per " + rateString + ")", "Output through overproduction (per " + rateString + ")", "Produced by recipe nodes (per " + rateString + ")", "Consumed by recipe nodes (per " + rateString + ")" },
					new string[] {"Fluid", "Input (per "+rateString+")", "Input through un-linked recipe ingredients (per "+rateString+")", "Output (per " + rateString + ")", "Output through un-linked recipe products (per " + rateString + ")", "Output through overproduction (per " + rateString + ")", "Produced by recipe nodes (per " + rateString + ")", "Consumed by recipe nodes (per " + rateString + ")" }
				});
		}

		private void keyNodesExportButton_Click(object sender, EventArgs e)
		{
			ExportCSV(
				new List<ListViewItem>[] { filteredKeyNodesList },
				new string[][]
				{
					new string[] {"Node Type", "Node Details (item / recipe name)", "Node Title", "Throughput (for non-recipe nodes) (per " + rateString + ")", "Building Count (for recipe nodes)" }
				});
		}

		private void ExportCSV(List<ListViewItem>[] inputList, string[][] columnNames)
		{
			using (SaveFileDialog dialog = new SaveFileDialog())
			{
				dialog.AddExtension = true;
				dialog.Filter = "CSV (*.csv)|*.csv";
				dialog.InitialDirectory = Path.Combine(Application.StartupPath, "Exported CSVs");
				if (!Directory.Exists(dialog.InitialDirectory))
					Directory.CreateDirectory(dialog.InitialDirectory);
				dialog.FileName = "foreman data.csv";
				dialog.ValidateNames = true;
				dialog.OverwritePrompt = true;
				var result = dialog.ShowDialog();

				if (result == DialogResult.OK)
				{
					List<string[]> csvLines = new List<string[]>();

					for(int i = 0; i < inputList.Length; i++)
					{
						csvLines.Add(columnNames[i]);
						foreach (ListViewItem lvi in inputList[i])
						{
							string[] cLine = new string[columnNames[i].Length];
							for (int j = 0; j < cLine.Length; j++)
								cLine[j] = (lvi.SubItems[j].Tag?? lvi.SubItems[j].Text).ToString().Replace(",", "").Replace("\n", "; ").Replace("\t", "");
							csvLines.Add(cLine);
						}
						csvLines.Add(new string[] { "" });
					}
					if (csvLines.Count > 0)
						csvLines.RemoveAt(csvLines.Count - 1);

					//export to csv.
					StringBuilder csvBuilder = new StringBuilder();
					csvLines.ForEach(line => { csvBuilder.AppendLine(string.Join(",", line)); });
					File.WriteAllText(dialog.FileName, csvBuilder.ToString());
				}
			}
		}

        private const string ExportToFactorioButtonCaption = "Copy for Factorio";

        // The mod turns each line into a personal logistic request, so the internal name
        // has to be the ITEM that places the building - not the entity. They are usually
        // spelled the same, which is why this went unnoticed, but pyanodons' TURD
        // machines are not: entity "fish-farm-mk01-turd" is placed by item
        // "fish-farm-mk01", and Factorio has no item under the entity's name.
        private static string GetPlacingItemName(object tag, string entityName)
        {
            switch (tag)
            {
                case AssemblerQualityPair assembler: return assembler.Assembler.AssociatedItems.FirstOrDefault()?.Name ?? entityName;
                case BeaconQualityPair beacon: return beacon.Beacon.AssociatedItems.FirstOrDefault()?.Name ?? entityName;
                default: return entityName;
            }
        }

        // The building lists only carry their count as display text (grouped, and in
        // exponent form past 10 million), so it has to come back through a parse.
        private static double ParseListViewCount(string text)
        {
            if (double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out double count))
                return count;
            return double.TryParse(text.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out count) ? count : 0;
        }

        private void ExportToFactorioButton_Click(object sender, EventArgs e)
        {
            //one line per item: several rows can collapse onto the same one once the placing
            //item is resolved - quality variants of a building, a TURD machine sitting next to
            //its plain form, or a module used in both assemblers and beacons
            var taskCounts = new Dictionary<string, double>();
            var taskNames = new Dictionary<string, string>();
            var taskOrder = new List<string>();

            void addTask(string internalName, string friendlyName, double count)
            {
                if (count <= 0)
                    return;
                if (!taskCounts.ContainsKey(internalName))
                {
                    taskCounts.Add(internalName, 0);
                    taskNames.Add(internalName, friendlyName);
                    taskOrder.Add(internalName);
                }
                taskCounts[internalName] += count;
            }

            var allBuildingLists = new[]
            {
				filteredAssemblerList,
				filteredMinerList,
				filteredPowerList,
				filteredBeaconList
			};

            foreach (var list in allBuildingLists)
            {
                foreach (ListViewItem lvi in list)
                {
                    // lvi.Name is "internal-name:quality-name" — grab just the entity name
                    string entityName = lvi.Name.Split(':')[0];
                    addTask(GetPlacingItemName(lvi.Tag, entityName), lvi.SubItems[1].Text, ParseListViewCount(lvi.SubItems[0].Text));
                }
            }

            //module names are already item names - nothing to resolve
            foreach (var list in new[] { filteredModuleList, filteredBeaconModuleList })
                foreach (ListViewItem lvi in list)
                    addTask(lvi.Name.Split(':')[0], lvi.SubItems[1].Text, (double)lvi.SubItems[0].Tag);

            //the mod parses "<digits>x " -> group separators or an exponent make the line unparsable
            var lines = taskOrder.Select(key => $"{taskCounts[key].ToString("0", CultureInfo.InvariantCulture)}x {taskNames[key]} [{key}]").ToList();

            if (lines.Count == 0)
            {
                FlashExportButton("Nothing to copy");
                return;
            }

            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            FlashExportButton(lines.Count == 1 ? "Copied 1 line" : $"Copied {lines.Count} lines");
        }

        // Shows a transient caption on the export button instead of interrupting with a
        // dialog. The timer is a field so a second click restarts the countdown rather
        // than stacking timers, and it hangs off `components` so the form disposes it.
        private void FlashExportButton(string message)
        {
            if (exportFlashTimer == null)
            {
                exportFlashTimer = new Timer(components) { Interval = 1200 };
                exportFlashTimer.Tick += (sender, e) =>
                {
                    exportFlashTimer.Stop();
                    ExportToFactorioButton.Text = ExportToFactorioButtonCaption;
                };
            }

            exportFlashTimer.Stop();
            ExportToFactorioButton.Text = message;
            exportFlashTimer.Start();
        }
    }
}
