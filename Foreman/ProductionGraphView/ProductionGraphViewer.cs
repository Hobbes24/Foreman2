using System;
using System.Collections.Generic;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Windows.Forms;
using System.Runtime.Serialization;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using Newtonsoft.Json;
using System.Text;
using System.IO;
using System.Threading.Tasks;

namespace Foreman
{
	public enum NewNodeType { Disconnected, Supplier, Consumer }
	public enum NodeDrawingStyle { Regular, PrintStyle, Simple, IconsOnly } //printstyle is meant for any additional chages (from regular) for exporting to image format, simple will only draw the node boxes (no icons or text) and link lines, iconsonly will draw node icons instead of nodes (for zoomed view)

	[Serializable]
	public partial class ProductionGraphViewer : UserControl, ISerializable
	{
		private enum DragOperation { None, Item, Selection }
		public enum LOD { Low, Medium, High } //low: only names. medium: assemblers, beacons, etc. high: include assembler percentages

		public LOD LevelOfDetail { get; set; }
		public bool ArrowsOnLinks { get; set; }
		public bool IconsOnly { get; set; }
		public int IconsSize { get; set; }
		public int IconsDrawSize { get { return ViewScale > ((double)IconsSize / 96)? 96 : (int)(IconsSize / ViewScale); } }

		public int NodeCountForSimpleView { get; set; } //if the number of elements to draw is over this amount then the drawing functions will switch to simple view draws (mostly for FPS during zoomed out views)
		public bool ShowRecipeToolTip { get; set; }
		public bool TooltipsEnabled { get; set; }
		private bool SubwindowOpen; //used together with tooltip enabled -> if we open up an item/recipe/assembler window, this will halt tooltip show.
		public bool DynamicLinkWidth = false;
		public bool LockedRecipeEditPanelPosition = true;
		public bool FlagOUSuppliedNodes = false; //if true, will add a flag for over or under supplied nodes

		public bool SmartNodeDirection { get; set; }

		public DataCache DCache { get; set; }
		public ProductionGraph Graph { get; private set; }
		public GridManager Grid { get; private set; }
		public FloatingTooltipRenderer ToolTipRenderer { get; private set; }
		public PointingArrowRenderer ArrowRenderer { get; private set; }

        public List<string> SavedPresetNames = new List<string>();
        public Quality LastAssemblerQuality { get; private set; } //quality of the last-edited recipe's assembler (used when placing new recipe nodes)

		public GraphElement MouseDownElement { get; set; }

		public IReadOnlyDictionary<ReadOnlyBaseNode, BaseNodeElement> NodeElementDictionary { get { return nodeElementDictionary; } }
		public IReadOnlyDictionary<ReadOnlyNodeLink, LinkElement> LinkElementDictionary { get { return linkElementDictionary; } }

		public IReadOnlyCollection<BaseNodeElement> SelectedNodes { get { return selectedNodes; } }

		public Point ViewOffset { get; private set; }
		public float ViewScale { get; private set; }
		public Rectangle VisibleGraphBounds { get; private set; }

		private const int minDragDiff = 30;
		private const int minLinkWidth = 3;
		private const int maxLinkWidth = 35;

		private static readonly Pen pausedBorders = new Pen(Color.FromArgb(255, 80, 80), 5);
		private static readonly Pen selectionPen = new Pen(Color.FromArgb(100, 100, 200), 2);

		private Dictionary<ReadOnlyBaseNode, BaseNodeElement> nodeElementDictionary;
		private List<BaseNodeElement> nodeElements;
		private Dictionary<ReadOnlyNodeLink, LinkElement> linkElementDictionary;
		private List<LinkElement> linkElements;
		private DraggedLinkElement draggedLinkElement;

		private Point mouseDownStartScreenPoint;
		private MouseButtons downButtons; //we use this to ensure that any mouse operations only count if they started on this panel

		private Point ViewDragOriginPoint;
		private bool viewBeingDragged = false; //separate from dragOperation due to being able to drag view at all stages of dragOperation

		private DragOperation currentDragOperation = DragOperation.None;

		private Rectangle SelectionZone;
		private Point SelectionZoneOriginPoint;

        private HashSet<BaseNodeElement> selectedNodes; //main list of selected nodes
        private HashSet<BaseNodeElement> currentSelectionNodes; //list of nodes currently under the selection zone (which can be added/removed/replace the full list)

        private List<AnnotationElement> annotationElements;
        private HashSet<AnnotationElement> selectedAnnotations;

        private ContextMenu rightClickMenu = new ContextMenu();

        internal Dictionary<ReadOnlyPassthroughNode, RecipeNodeSnapshot> ConversionSnapshots
			= new Dictionary<ReadOnlyPassthroughNode, RecipeNodeSnapshot>();
        // -------Find feature fields
        private Panel findPanel;
		private TextBox findTextBox;
		private Label findStatusLabel;
		private List<BaseNodeElement> findResults = new List<BaseNodeElement>();
		private int findResultIndex = -1;
        private CheckBox autoZoomCheckBox;

        public ProductionGraphViewer()
		{
			InitializeComponent();
			MouseWheel += new MouseEventHandler(ProductionGraphViewer_MouseWheel);
			Resize += new EventHandler(ProductionGraphViewer_Resized);

			ViewOffset = new Point(Width / -2, Height / -2);
			ViewScale = 1f;
			NodeCountForSimpleView = 200;

			IconsOnly = false;
			IconsSize = 32;

			TooltipsEnabled = true;
			SubwindowOpen = false;

			Graph = new ProductionGraph();
			//Graph.ClearGraph()
			Graph.NodeAdded += Graph_NodeAdded;
			Graph.NodeDeleted += Graph_NodeDeleted;
			Graph.LinkAdded += Graph_LinkAdded;
			Graph.LinkDeleted += Graph_LinkDeleted;
			Graph.NodeValuesUpdated += Graph_NodeValuesUpdated;

			Grid = new GridManager();
			ToolTipRenderer = new FloatingTooltipRenderer(this);
			ArrowRenderer = new PointingArrowRenderer(this);

			nodeElementDictionary = new Dictionary<ReadOnlyBaseNode, BaseNodeElement>();
			nodeElements = new List<BaseNodeElement>();
			linkElementDictionary = new Dictionary<ReadOnlyNodeLink, LinkElement>();
			linkElements = new List<LinkElement>();

            selectedNodes = new HashSet<BaseNodeElement>();
            currentSelectionNodes = new HashSet<BaseNodeElement>();

            annotationElements = new List<AnnotationElement>();
            selectedAnnotations = new HashSet<AnnotationElement>();

            UpdateGraphBounds();
            InitFindPanel();
            Invalidate();
        }

		public void ClearGraph()
		{
			DisposeLinkDrag();
			Graph.ClearGraph();
            //at this point every node element and link element has been removed.

            findResults.Clear();
            findResultIndex = -1;
            lastSearchQuery = "";

            foreach (AnnotationElement ann in annotationElements.ToList())
                ann.Dispose();
            annotationElements.Clear();
            selectedAnnotations.Clear();

            selectedNodes.Clear();
            currentSelectionNodes.Clear();
        }

		public BaseNodeElement GetNodeAtPoint(Point point) //returns first such node (in case of stacking)
		{
			//done in a 2 stage process -> first we do a rough check on the point's location (point within a node's area + 50 boundary on all sides), it goes to part 2)
			//							-> then we do a full element.containsPoint check which includes both the node and any added segments (such as item frames)

			for (int i = nodeElements.Count - 1; i >= 0; i--)
			{
				Rectangle roughNodeZone = new Rectangle(nodeElements[i].X - nodeElements[i].Width / 2 - 50, nodeElements[i].Y - nodeElements[i].Height / 2 - 50, nodeElements[i].Width + 100, nodeElements[i].Height + 100);
				if (roughNodeZone.Contains(point))
					if (nodeElements[i].ContainsPoint(point))
						return nodeElements[i];
			}
            return null;
        }

        public AnnotationElement GetAnnotationAtPoint(Point point)
        {
            // Reverse order: most-recently-added annotation is tested first (topmost in render order).
            for (int i = annotationElements.Count - 1; i >= 0; i--)
                if (annotationElements[i].ContainsPoint(point))
                    return annotationElements[i];
            return null;
        }

        //----------------------------------------------Annotation add/remove/create

        public void AddAnnotationElement(AnnotationElement element)
        {
            annotationElements.Add(element);
            Invalidate();
        }

        public void RemoveAnnotationElement(AnnotationElement element)
        {
            annotationElements.Remove(element);
            selectedAnnotations.Remove(element);
            element.Dispose();
            Invalidate();
        }

        public void AddShapeAnnotation(Point graphPoint)
        {
            AddAnnotationElement(new ShapeAnnotationElement(this, graphPoint));
        }

        public void AddTextAnnotation(Point graphPoint)
        {
            AddAnnotationElement(new TextAnnotationElement(this, graphPoint));
        }

        //----------------------------------------------Adding new node functions (including link dragging) + Node edit
        public void StartLinkDrag(BaseNodeElement startNode, LinkType linkType, ItemQualityPair item)
		{
			draggedLinkElement?.Dispose();
			draggedLinkElement = new DraggedLinkElement(this, startNode, linkType, item);
			MouseDownElement = draggedLinkElement;
		}

		public void DisposeLinkDrag()
		{
			draggedLinkElement?.Dispose();
			draggedLinkElement = null;
		}

		public void AddItem(Point drawOrigin, Point newLocation)
		{
			if (string.IsNullOrEmpty(DCache.PresetName))
			{
				MessageBox.Show("The current preset (" + Properties.Settings.Default.CurrentPresetName + ") is corrupt.");
				return;
			}

			SubwindowOpen = true;
			ItemChooserPanel itemChooser = new ItemChooserPanel(this, drawOrigin);
			itemChooser.ItemRequested += (o, itemRequestArgs) =>
			{
				AddNewNode(drawOrigin, itemRequestArgs.Item, newLocation, NewNodeType.Disconnected);
			};
			itemChooser.PanelClosed += (o, e) => { SubwindowOpen = false; };

			itemChooser.Show();
		}

		public void AddNewNode(Point drawOrigin, ItemQualityPair baseItem, Point newLocation, NewNodeType nNodeType, BaseNodeElement originElement = null, bool offsetLocationToItemTabLevel = false)
		{
			if(string.IsNullOrEmpty(DCache.PresetName))
			{
				DisposeLinkDrag();
				MessageBox.Show("The current preset (" + Properties.Settings.Default.CurrentPresetName + ") is corrupt.");
				return;
			}

			if ((nNodeType != NewNodeType.Disconnected) && (originElement == null || !baseItem))
				Trace.Fail("Origin element or base item not provided for a new (linked) node");
			
			if (Grid.ShowGrid)
				newLocation = Grid.AlignToGrid(newLocation);

			int lastNodeWidth = 0;
			NodeDirection newNodeDirection = (originElement == null || !SmartNodeDirection) ? Graph.DefaultNodeDirection :
				draggedLinkElement.Type != BaseLinkElement.LineType.UShape ? originElement.DisplayedNode.NodeDirection :
				originElement.DisplayedNode.NodeDirection == NodeDirection.Up ? NodeDirection.Down : NodeDirection.Up;

            if ((Control.ModifierKeys & Keys.Control) == Keys.Control) //control key pressed -> we are making a passthrough node.
            {
                ProcessNodeRequest(null, new RecipeRequestArgs(NodeType.Passthrough));
                DisposeLinkDrag();
                Graph.UpdateNodeStates(false);
                Invalidate();
            } else
            {
                fRange tempRange = new fRange(0, 0, true);
                if (baseItem && baseItem.Item is Fluid fluid && fluid.IsTemperatureDependent)
                {
                    if (nNodeType == NewNodeType.Consumer) //need to check all nodes down to recipes for range of temperatures being produced
                        tempRange = LinkChecker.GetTemperatureRange(fluid, originElement.DisplayedNode, LinkType.Output, true);
					//This else if statement might have been interfering with Coke Oven Gas, pulled it out to see.
					//It was not, but removing it does allow hidden recipes to be viewd, so we might want to remove it in the future.
					else if (nNodeType == NewNodeType.Supplier) //need to check all nodes up to recipes for range of temperatures being consumed (guaranteed to be in a SINGLE [] range)
						tempRange = LinkChecker.GetTemperatureRange(fluid, originElement.DisplayedNode, LinkType.Input, true);
				}

				RecipeChooserPanel recipeChooser = new RecipeChooserPanel(this, drawOrigin, baseItem, tempRange, nNodeType); //QUALITY UPDATE
                recipeChooser.RecipeRequested += ProcessNodeRequest;
                recipeChooser.PanelClosed += (o, e) =>
                {
					if (e.Option != IRChooserPanel.ChooserPanelCloseReason.RequiresItemSelection)
					{
						SubwindowOpen = false;
						DisposeLinkDrag();
						Graph.UpdateNodeStates(false);
						Invalidate();
					}
                };

                SubwindowOpen = true;
                recipeChooser.Show();
            }
			return; //end of this function

			//internal helper funtion: called upon a successfull selection of a recipe-selection screen (opened above)
            void ProcessNodeRequest(object o, RecipeRequestArgs recipeRequestArgs)
			{
				ReadOnlyBaseNode newNode = null;
				switch (recipeRequestArgs.NodeType)
				{
					case NodeType.Consumer:
						newNode = Graph.CreateConsumerNode(baseItem, newLocation);
						FinalizeNodePosition(newNode);
						break;
					case NodeType.Supplier:
						newNode = Graph.CreateSupplierNode(baseItem, newLocation);
                        FinalizeNodePosition(newNode);
                        break;
					case NodeType.Passthrough:
						newNode = Graph.CreatePassthroughNode(baseItem, newLocation);
                        FinalizeNodePosition(newNode);
                        break;
					case NodeType.Spoil:
						if (recipeRequestArgs.Direction == NodeDirection.Up)
						{
							newNode = Graph.CreateSpoilNode(baseItem, baseItem.Item.SpoilResult, newLocation);
							FinalizeNodePosition(newNode);
						}
						else if (baseItem.Item.SpoilOrigins.Count == 1)
						{
							newNode = Graph.CreateSpoilNode(new ItemQualityPair(baseItem.Item.SpoilOrigins.ElementAt(0), baseItem.Quality), baseItem.Item, newLocation); //QUALITY UPDATE
							FinalizeNodePosition(newNode);
						}
						else
						{
							//need to open up an item selection window to select a given spoil origin
							SubwindowOpen = true;
							ItemChooserPanel itemChooser = new ItemChooserPanel(this, drawOrigin, baseItem.Item.SpoilOrigins);
							itemChooser.ItemRequested += (oo, itemRequestArgs) =>
							{
								newNode = Graph.CreateSpoilNode(new ItemQualityPair(itemRequestArgs.Item.Item, baseItem.Quality), baseItem.Item, newLocation);
                                FinalizeNodePosition(newNode);
                            };
							itemChooser.PanelClosed += (oo, e) => { SubwindowOpen = false; };
							itemChooser.Show();
						}
						break;
					case NodeType.Plant:
                        if (recipeRequestArgs.Direction == NodeDirection.Up)
                        {
                            newNode = Graph.CreatePlantNode(baseItem.Item.PlantResult, baseItem.Quality, newLocation);
                            FinalizeNodePosition(newNode);
                        }
						else if (baseItem.Item.PlantOrigins.Count == 1)
                        {
                            newNode = Graph.CreatePlantNode(baseItem.Item.PlantOrigins.ElementAt(0).PlantResult, DCache.DefaultQuality, newLocation); //QUALITY UPDATE
                            FinalizeNodePosition(newNode);
                        }
						else
                        {
                            //need to open up an item selection window to select a given spoil origin
                            SubwindowOpen = true;
                            ItemChooserPanel itemChooser = new ItemChooserPanel(this, drawOrigin, baseItem.Item.PlantOrigins);
                            itemChooser.ItemRequested += (oo, itemRequestArgs) =>
                            {
                                newNode = Graph.CreatePlantNode(itemRequestArgs.Item.Item.PlantResult, DCache.DefaultQuality, newLocation);
                                FinalizeNodePosition(newNode);
                            };
                            itemChooser.PanelClosed += (oo, e) => { SubwindowOpen = false; };
                            itemChooser.Show();
                        }
                        break;
					case NodeType.Recipe:
						ReadOnlyRecipeNode rNode = Graph.CreateRecipeNode(recipeRequestArgs.Recipe, newLocation);
						newNode = rNode;
						if ((nNodeType == NewNodeType.Consumer && !recipeRequestArgs.Recipe.Recipe.IngredientSet.ContainsKey(baseItem.Item)) || 
							(nNodeType == NewNodeType.Supplier && !recipeRequestArgs.Recipe.Recipe.ProductSet.ContainsKey(baseItem.Item)) ||
							(nNodeType == NewNodeType.Disconnected && baseItem && !recipeRequestArgs.Recipe.Recipe.IngredientSet.ContainsKey(baseItem.Item) && !recipeRequestArgs.Recipe.Recipe.ProductSet.ContainsKey(baseItem.Item)))
						{
							AssemblerSelector.Style style;
							switch (Graph.AssemblerSelector.DefaultSelectionStyle)
							{
								case AssemblerSelector.Style.Best:
								case AssemblerSelector.Style.BestBurner:
								case AssemblerSelector.Style.BestNonBurner:
									style = AssemblerSelector.Style.BestBurner;
									break;
								case AssemblerSelector.Style.Worst:
								case AssemblerSelector.Style.WorstBurner:
								case AssemblerSelector.Style.WorstNonBurner:
								default:
									style = AssemblerSelector.Style.WorstBurner;
									break;
							}
							List<Assembler> assemblerOptions = Graph.AssemblerSelector.GetOrderedAssemblerList(recipeRequestArgs.Recipe.Recipe, style);

							RecipeNodeController controller = (RecipeNodeController)Graph.RequestNodeController(rNode);
							if ((nNodeType == NewNodeType.Consumer) || (nNodeType == NewNodeType.Disconnected && assemblerOptions.Any(a => a.Fuels.Contains(baseItem.Item))))
							{
								controller.SetAssembler(new AssemblerQualityPair(assemblerOptions.First(a => a.Fuels.Contains(baseItem.Item)), Graph.DefaultAssemblerQuality));
								controller.SetFuel(baseItem.Item);
							}
							else if(nNodeType == NewNodeType.Supplier || (nNodeType == NewNodeType.Disconnected && assemblerOptions.Any(a => a.Fuels.Contains(baseItem.Item.FuelOrigin))))
                            {
								controller.SetAssembler(new AssemblerQualityPair(assemblerOptions.First(a => a.Fuels.Contains(baseItem.Item.FuelOrigin)), Graph.DefaultAssemblerQuality));
								controller.SetFuel(baseItem.Item.FuelOrigin);
							}
						}
                        FinalizeNodePosition(newNode);
                        break;
				}
			}

			//internal helper funtion: once a node has been created it will be placed where it needs to be and all intermediate states (ex: dragged item line) finalized
			void FinalizeNodePosition(ReadOnlyBaseNode newNode)
			{ 
				//this is the offset to take into account multiple recipe additions (holding shift while selecting recipe). First node isnt shifted, all subsequent ones are 'attempted' to be spaced.
				//should be updated once the node graphics are updated (so that the node size doesnt depend as much on the text)
				BaseNodeElement newNodeElement = NodeElementDictionary[newNode];
				int offsetDistance = lastNodeWidth / 2;
				lastNodeWidth = newNodeElement.Width; //effectively: this recipe width
				if (offsetDistance > 0)
				{
					offsetDistance += (lastNodeWidth / 2);
					int newOffsetDistance = Grid.AlignToGrid(offsetDistance);
					if (newOffsetDistance < offsetDistance)
						newOffsetDistance += Grid.CurrentGridUnit;
					offsetDistance = newOffsetDistance;
				}
				newLocation = new Point(newLocation.X + offsetDistance, newLocation.Y);

				int yoffset = offsetLocationToItemTabLevel ? (nNodeType == NewNodeType.Consumer ? -newNodeElement.Height / 2 : nNodeType == NewNodeType.Supplier ? newNodeElement.Height / 2 : 0) : 0;
				yoffset *= newNodeDirection == NodeDirection.Up ? 1 : -1;
				Graph.RequestNodeController(newNode).SetLocation(new Point(newLocation.X, newLocation.Y + yoffset));

				if (originElement != null)
					Graph.RequestNodeController(newNode).SetDirection(newNodeDirection);

				if (nNodeType == NewNodeType.Consumer)
					Graph.CreateLink(originElement.DisplayedNode, newNode, baseItem);
				else if (nNodeType == NewNodeType.Supplier)
					Graph.CreateLink(newNode, originElement.DisplayedNode, baseItem);

				DisposeLinkDrag();
				Graph.UpdateNodeValues();
				Graph.UpdateNodeStates(false);
				Invalidate();
			}
		}

		public void AddPassthroughNodesFromSelection(LinkType linkType, Size offset)
		{
			List<BaseNodeElement> newPassthroughNodes = new List<BaseNodeElement>();
			foreach(PassthroughNodeElement passthroughNode in selectedNodes)
			{
				NodeDirection newNodeDirection = !SmartNodeDirection ? Graph.DefaultNodeDirection :
					draggedLinkElement.Type != BaseLinkElement.LineType.UShape ? passthroughNode.DisplayedNode.NodeDirection :
					passthroughNode.DisplayedNode.NodeDirection == NodeDirection.Up ? NodeDirection.Down : NodeDirection.Up;

				ItemQualityPair passthroughItem = ((ReadOnlyPassthroughNode)passthroughNode.DisplayedNode).PassthroughItem;

				int yoffset = linkType == LinkType.Input ? passthroughNode.Height / 2 : -passthroughNode.Height / 2;
				yoffset *= newNodeDirection == NodeDirection.Up ? 1 : -1;
				yoffset += offset.Height;

				ReadOnlyPassthroughNode newNode = Graph.CreatePassthroughNode(passthroughItem, new Point(passthroughNode.Location.X + offset.Width, passthroughNode.Location.Y + yoffset));
				PassthroughNodeController controller = (PassthroughNodeController)Graph.RequestNodeController(newNode);
				controller.SetDirection(newNodeDirection);

				if (linkType == LinkType.Input)
					Graph.CreateLink(newNode, passthroughNode.DisplayedNode, passthroughItem );
				else
					Graph.CreateLink(passthroughNode.DisplayedNode, newNode, passthroughItem );

				newPassthroughNodes.Add(nodeElementDictionary[newNode]);
			}
			SetSelection(newPassthroughNodes);

			DisposeLinkDrag();
			Graph.UpdateNodeStates(false);
			Invalidate();
		}

        // ---------------------------------------------------------------------------------
        // ADD THESE TWO METHODS to ProductionGraphViewer.cs
        // Place them immediately after the closing brace of AddPassthroughNodesFromSelection
        // ---------------------------------------------------------------------------------

        // Ctrl + drag from any node tab to empty space:
        // Creates a passthrough node for every item on that side of the origin node,
        // spaced 80px apart horizontally starting at the drop location.
        public void AddPassthroughNodesForAllItems(LinkType linkType, BaseNodeElement originElement, Point dropLocation)
        {
            IEnumerable<ItemQualityPair> items = linkType == LinkType.Input
                ? originElement.DisplayedNode.Inputs
                : originElement.DisplayedNode.Outputs;

            List<BaseNodeElement> newPassthroughNodes = new List<BaseNodeElement>();
            int index = 0;

            foreach (ItemQualityPair item in items)
            {
                Point nodeLocation = new Point(dropLocation.X + index * 80, dropLocation.Y);
                if (Grid.ShowGrid)
                    nodeLocation = Grid.AlignToGrid(nodeLocation);

                NodeDirection newNodeDirection = !SmartNodeDirection ? Graph.DefaultNodeDirection :
                    draggedLinkElement.Type != BaseLinkElement.LineType.UShape ? originElement.DisplayedNode.NodeDirection :
                    originElement.DisplayedNode.NodeDirection == NodeDirection.Up ? NodeDirection.Down : NodeDirection.Up;

                ReadOnlyPassthroughNode newNode = Graph.CreatePassthroughNode(item, nodeLocation);
                PassthroughNodeController controller = (PassthroughNodeController)Graph.RequestNodeController(newNode);
                controller.SetDirection(newNodeDirection);

                if (linkType == LinkType.Input)
                    Graph.CreateLink(newNode, originElement.DisplayedNode, item);
                else
                    Graph.CreateLink(originElement.DisplayedNode, newNode, item);

                newPassthroughNodes.Add(nodeElementDictionary[newNode]);
                index++;
            }

            SetSelection(newPassthroughNodes);
            DisposeLinkDrag();
            Graph.UpdateNodeStates(false);
            Invalidate();
        }

        // Ctrl + drag from one node directly onto another matching node:
        // Inserts a single passthrough node at the midpoint and links both ends through it,
        // rather than connecting them directly.
        public void AddPassthroughNodeBetween(BaseNodeElement supplierElement, BaseNodeElement consumerElement, ItemQualityPair item)
        {
            Point midpoint = new Point(
                (supplierElement.Location.X + consumerElement.Location.X) / 2,
                (supplierElement.Location.Y + consumerElement.Location.Y) / 2);
            if (Grid.ShowGrid)
                midpoint = Grid.AlignToGrid(midpoint);

            NodeDirection newNodeDirection = !SmartNodeDirection ? Graph.DefaultNodeDirection :
                draggedLinkElement.Type != BaseLinkElement.LineType.UShape ? supplierElement.DisplayedNode.NodeDirection :
                supplierElement.DisplayedNode.NodeDirection == NodeDirection.Up ? NodeDirection.Down : NodeDirection.Up;

            ReadOnlyPassthroughNode newNode = Graph.CreatePassthroughNode(item, midpoint);
            PassthroughNodeController controller = (PassthroughNodeController)Graph.RequestNodeController(newNode);
            controller.SetDirection(newNodeDirection);

            Graph.CreateLink(supplierElement.DisplayedNode, newNode, item);
            Graph.CreateLink(newNode, consumerElement.DisplayedNode, item);

            SetSelection(new List<BaseNodeElement> { nodeElementDictionary[newNode] });
            DisposeLinkDrag();
            Graph.UpdateNodeValues();
            Graph.UpdateNodeStates(false);
            Invalidate();
        }

        public void TryDeleteSelectedNodes()
		{
			bool proceed = true;
			if (selectedNodes.Count > 10)
				proceed = (MessageBox.Show("You are deleting " + selectedNodes.Count + " nodes. \nAre you sure?", "Confirm delete.", MessageBoxButtons.YesNo) == DialogResult.Yes);
			if (proceed)
			{
				foreach (BaseNodeElement node in selectedNodes.ToList())
					Graph.DeleteNode(node.DisplayedNode);
				selectedNodes.Clear();
				Graph.UpdateNodeValues();
			}
		}

		public void FlipSelectedNodes()
		{
			foreach (BaseNodeElement node in selectedNodes.ToList())
				Graph.RequestNodeController(node.DisplayedNode).SetDirection(node.DisplayedNode.NodeDirection == NodeDirection.Up ? NodeDirection.Down : NodeDirection.Up);
			Invalidate();
		}

        public void ConvertNodeToPassthrough(ReadOnlyBaseNode node, ItemQualityPair item)
        {
            // Capture snapshot before anything is deleted
            RecipeNodeSnapshot snapshot = null;
            if (node is ReadOnlyRecipeNode recipeNode)
                snapshot = new RecipeNodeSnapshot(recipeNode);

            // Snapshot existing connections for this item before we delete anything
            List<ReadOnlyBaseNode> suppliers = node.InputLinks
                .Where(l => l.Item == item)
                .Select(l => l.Supplier)
                .ToList();
            List<ReadOnlyBaseNode> consumers = node.OutputLinks
                .Where(l => l.Item == item)
                .Select(l => l.Consumer)
                .ToList();

            // Create the passthrough at the same spot, same direction
            ReadOnlyPassthroughNode passthrough = Graph.CreatePassthroughNode(item, node.Location);
            ((PassthroughNodeController)Graph.RequestNodeController(passthrough))
                .SetDirection(node.NodeDirection);

            // Delete the original node first (clears all its links cleanly)
            Graph.DeleteNode(node);

            // Re-wire: suppliers → passthrough → consumers
            foreach (ReadOnlyBaseNode supplier in suppliers)
                Graph.CreateLink(supplier, passthrough, item);
            foreach (ReadOnlyBaseNode consumer in consumers)
                Graph.CreateLink(passthrough, consumer, item);

            // Store snapshot keyed to the new passthrough
            if (snapshot != null)
                ConversionSnapshots[passthrough] = snapshot;

            Graph.UpdateNodeValues();
            Graph.UpdateNodeStates(false);
            Invalidate();
        }

        public void RestoreFromSnapshot(ReadOnlyPassthroughNode passthrough)
        {
            if (!ConversionSnapshots.TryGetValue(passthrough, out RecipeNodeSnapshot snapshot))
                return;

            ReadOnlyRecipeNode restored = Graph.CreateRecipeNode(snapshot.BaseRecipe, snapshot.Location);
            RecipeNodeController controller = (RecipeNodeController)Graph.RequestNodeController(restored);

            controller.SetAssembler(snapshot.SelectedAssembler);
            if (snapshot.Fuel != null)
                controller.SetFuel(snapshot.Fuel);
            controller.SetAssemblerModules(snapshot.AssemblerModules, true);
            controller.SetNeighbourCount(snapshot.NeighbourCount);
            controller.SetExtraProductivityBonus(snapshot.ExtraProductivityBonus);

            if (snapshot.SelectedBeacon)
            {
                controller.SetBeacon(snapshot.SelectedBeacon);
                controller.SetBeaconModules(snapshot.BeaconModules, true);
                controller.SetBeaconCount(snapshot.BeaconCount);
                controller.SetBeaconsPerAssembler(snapshot.BeaconsPerAssembler);
                controller.SetBeaconsCont(snapshot.BeaconsConst);
            }

            controller.SetDirection(snapshot.NodeDirection);
            controller.SetPriority(snapshot.LowPriority);
            controller.SetKeyNode(snapshot.KeyNode);
            controller.SetKeyNodeTitle(snapshot.KeyNodeTitle);

            // Delete passthrough (also cleans up snapshot via Graph_NodeDeleted)
            Graph.DeleteNode(passthrough);

            // Re-wire only links whose nodes still exist
            foreach (var (supplierNode, item) in snapshot.InputLinks)
                if (nodeElementDictionary.ContainsKey(supplierNode))
                    Graph.CreateLink(supplierNode, restored, item);

            foreach (var (consumerNode, item) in snapshot.OutputLinks)
                if (nodeElementDictionary.ContainsKey(consumerNode))
                    Graph.CreateLink(restored, consumerNode, item);

            Graph.UpdateNodeValues();
            Graph.UpdateNodeStates(false);
            Invalidate();
        }
        public void SetSelectedPassthroughNodesSimpleDraw(bool simpleDraw)
		{
			foreach (PassthroughNodeElement node in selectedNodes.Where(n => n is PassthroughNodeElement).ToList())
				((PassthroughNodeController)Graph.RequestNodeController(node.DisplayedNode)).SetSimpleDraw(simpleDraw);
			Invalidate();
		}

		public void EditNode(BaseNodeElement bNodeElement)
		{
			if (bNodeElement is RecipeNodeElement rNodeElement)
			{
				EditRecipeNode(rNodeElement);
				return;
			}

			SubwindowOpen = true;
			Control editPanel = new EditFlowPanel(bNodeElement.DisplayedNode, this);

            //offset view if necessary to ensure entire window will be seen (with 25 pixels boundary)
            Point screenOriginPoint = GraphToScreen(new Point(bNodeElement.X - (bNodeElement.Width / 2), bNodeElement.Y));
			screenOriginPoint = new Point(screenOriginPoint.X - editPanel.Width, screenOriginPoint.Y - (editPanel.Height / 2));
			Point offset = new Point(
				(int)(Math.Min(Math.Max(0, 25 - screenOriginPoint.X), this.Width - screenOriginPoint.X - editPanel.Width - bNodeElement.Width - 25)),
				(int)(Math.Min(Math.Max(0, 25 - screenOriginPoint.Y), this.Height - screenOriginPoint.Y - editPanel.Height - 25)));

			ViewOffset = Point.Add(ViewOffset, new Size((int)(offset.X / ViewScale), (int)(offset.Y / ViewScale)));
			UpdateGraphBounds();
			Invalidate();

			//open up the edit panel
			FloatingTooltipControl fttc = new FloatingTooltipControl(editPanel, Direction.Right, new Point(bNodeElement.X - (bNodeElement.Width / 2), bNodeElement.Y), this, true, false);
			fttc.Closing += (s, e) =>
			{
				SubwindowOpen = false;
				//bNodeElement.Update();
				Graph.UpdateNodeValues();
			};
		}

		public void EditRecipeNode(RecipeNodeElement rNodeElement)
		{
			SubwindowOpen = true;
			ReadOnlyRecipeNode rNode = (ReadOnlyRecipeNode)rNodeElement.DisplayedNode;
			Control editPanel = new EditRecipePanel(rNode, this);
			RecipePanel recipePanel = new RecipePanel(new Recipe[] { rNode.BaseRecipe.Recipe });

			if (LockedRecipeEditPanelPosition)
			{
				editPanel.Location = new Point(15, 15);
				recipePanel.Location = new Point(editPanel.Location.X + editPanel.Width + 5, editPanel.Location.Y);
			}
			else
			{
				//offset view if necessary to ensure entire window will be seen (with 25 pixels boundary). Additionally we want the tooltips to start 100 pixels above the arrow point instead of based on the center of the control (due to the dynamically changing height of the recipe option panel)
				Point recipeEditPanelOriginPoint = ToolTipRenderer.getTooltipScreenBounds(GraphToScreen(new Point(rNodeElement.X - (rNodeElement.Width / 2), rNodeElement.Y)), editPanel.Size, Direction.Right).Location;
				recipeEditPanelOriginPoint.Y += editPanel.Height / 2 - 125;
				recipeEditPanelOriginPoint.X -= recipePanel.Width + 5;
				Point offset = new Point(
					(int)(Math.Min(Math.Max(0, 25 - recipeEditPanelOriginPoint.X), this.Width - recipeEditPanelOriginPoint.X - editPanel.Width)),
					(int)(Math.Min(Math.Max(0, 25 - recipeEditPanelOriginPoint.Y), this.Height - recipeEditPanelOriginPoint.Y - editPanel.Height - 25)));

				editPanel.Location = Point.Add(recipeEditPanelOriginPoint, (Size)offset);
				recipePanel.Location = new Point(editPanel.Location.X + editPanel.Width + 5, editPanel.Location.Y);

				ViewOffset = Point.Add(ViewOffset, new Size((int)(offset.X / ViewScale), (int)(offset.Y / ViewScale)));
				UpdateGraphBounds(false);
				Invalidate();

			}

			//add the visible recipe to the right of the node
			new FloatingTooltipControl(recipePanel, Direction.Left, new Point(rNodeElement.X + (rNodeElement.Width / 2), rNodeElement.Y), this, true, true);
			FloatingTooltipControl fttc = new FloatingTooltipControl(editPanel, Direction.Right, new Point(rNodeElement.X - (rNodeElement.Width / 2), rNodeElement.Y), this, true, true);
			fttc.Closing += (s, e) => { SubwindowOpen = false; rNodeElement.RequestStateUpdate(); Graph.UpdateNodeValues(); };
		}

		//----------------------------------------------Selection functions

		private void SetSelection(IEnumerable<BaseNodeElement> newSelection)
		{
			foreach (BaseNodeElement element in selectedNodes)
				element.Highlighted = false;

			selectedNodes.Clear();
			selectedNodes.UnionWith(newSelection);

			foreach (BaseNodeElement element in selectedNodes)
				element.Highlighted = true;
		}

		private void UpdateSelection()
		{
			foreach (BaseNodeElement element in nodeElements)
				element.Highlighted = false;

			if ((Control.ModifierKeys & Keys.Alt) != 0) //remove zone
			{
				foreach (BaseNodeElement selectedNode in selectedNodes)
					selectedNode.Highlighted = true;
				foreach (BaseNodeElement newlySelectedNode in currentSelectionNodes)
					newlySelectedNode.Highlighted = false;
			}
			else if ((Control.ModifierKeys & Keys.Control) != 0)  //add zone
			{
				foreach (BaseNodeElement selectedNode in selectedNodes)
					selectedNode.Highlighted = true;
				foreach (BaseNodeElement newlySelectedNode in currentSelectionNodes)
					newlySelectedNode.Highlighted = true;
			}
			else //add zone (additive with ctrl or simple selection)
			{
				foreach (BaseNodeElement newlySelectedNode in currentSelectionNodes)
					newlySelectedNode.Highlighted = true;
			}
		}

        public void ClearSelection()
        {
            foreach (BaseNodeElement element in nodeElements)
                element.Highlighted = false;
            selectedNodes.Clear();
            currentSelectionNodes.Clear();
            foreach (AnnotationElement ann in selectedAnnotations)
                ann.IsSelected = false;
            selectedAnnotations.Clear();
            Invalidate();
        }

        public void AlignSelected()
		{
			foreach (BaseNodeElement ne in selectedNodes)
				ne.SetLocation(Grid.AlignToGrid(ne.Location));
			Invalidate();
		}

        //----------------------------------------------Paint functions

        protected IEnumerable<GraphElement> GetPaintingOrder()
        {
            if (draggedLinkElement != null)
                yield return draggedLinkElement;
            foreach (AnnotationElement element in annotationElements)  // annotations render first (background layer)
                yield return element;
            foreach (LinkElement element in linkElements)
                yield return element;
            foreach (BaseNodeElement element in nodeElements)
                yield return element;
        }

        public void UpdateNodeVisuals()
		{
			try
			{
				foreach (BaseNodeElement node in nodeElements)
					node.RequestStateUpdate();
			}
			catch (OverflowException) { }//Same as when working out node values, there's not really much to do here... Maybe I could show a tooltip saying the numbers are too big or something...
			Invalidate();
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			e.Graphics.ResetTransform();
			e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
			e.Graphics.Clear(this.BackColor);
			e.Graphics.TranslateTransform(Width / 2, Height / 2);
			e.Graphics.ScaleTransform(ViewScale, ViewScale);
			e.Graphics.TranslateTransform(ViewOffset.X, ViewOffset.Y);

			Paint(e.Graphics, false);
		}

		public new void Paint(Graphics graphics, bool FullGraph = false)
		{
            //update visibility of all elements
            if (FullGraph)
            {
                foreach (GraphElement element in GetPaintingOrder())
                    element.UpdateVisibility(Graph.Bounds);
                // Annotations are viewer-side and may live outside Graph.Bounds — force them visible
                foreach (AnnotationElement ann in annotationElements)
                    ann.ForceVisible();
            }
            else
                foreach (GraphElement element in GetPaintingOrder())
                    element.UpdateVisibility(VisibleGraphBounds);

            //ensure width of selection is correct
            selectionPen.Width = 2 / ViewScale;

			//grid
			if(!FullGraph)
				Grid.Paint(graphics, ViewScale, VisibleGraphBounds, (currentDragOperation == DragOperation.Item) ? MouseDownElement as BaseNodeElement : null);

			//process link element widths
			if (DynamicLinkWidth)
			{
				double itemMax = 0;
				double fluidMax = 0;
				foreach (LinkElement element in linkElements)
				{
					if (element.Item.Item is Fluid && !element.Item.Item.Name.StartsWith("§§")) //§§ is the foreman added special items (currently just §§heat). ignore them
						fluidMax = Math.Max(fluidMax, element.ConsumerElement.DisplayedNode.GetConsumeRate(element.Item));
					else
						itemMax = Math.Max(itemMax, element.ConsumerElement.DisplayedNode.GetConsumeRate(element.Item));
				}
				itemMax += itemMax == 0 ? 1 : 0;
				fluidMax += fluidMax == 0 ? 1 : 0;

				foreach (LinkElement element in linkElements)
				{
					if (element.Item.Item is Fluid)
						element.LinkWidth = (float)Math.Min((minLinkWidth + (maxLinkWidth - minLinkWidth) * (element.DisplayedLink.Throughput / fluidMax)), maxLinkWidth);
					else
						element.LinkWidth = (float)Math.Min((minLinkWidth + (maxLinkWidth - minLinkWidth) * (element.DisplayedLink.Throughput / itemMax)), maxLinkWidth);
				}
			}
			else
			{
				foreach (LinkElement element in linkElements)
					element.LinkWidth = minLinkWidth;
			}

			//run any pre-paint functions
			foreach (GraphElement elemnent in GetPaintingOrder())
				elemnent.PrePaint();

			//paint all elements (nodes & lines)
			int visibleElements = GetPaintingOrder().Count(e => e.Visible && e is BaseNodeElement);
			foreach (GraphElement element in GetPaintingOrder())
				element.Paint(graphics, FullGraph? NodeDrawingStyle.PrintStyle : IconsOnly? NodeDrawingStyle.IconsOnly : (visibleElements > NodeCountForSimpleView || ViewScale < 0.2)? NodeDrawingStyle.Simple : NodeDrawingStyle.Regular); //if viewscale is 0.2, then the text, images, etc being drawn are ~1/5th the size: aka: ~6x6 pixel images, etc. Use simple draw. Also simple draw if too many objects

			//selection zone
			if (currentDragOperation == DragOperation.Selection && !FullGraph)
			{
				graphics.DrawRectangle(selectionPen, SelectionZone);
				double pConsumption = currentSelectionNodes.Where(n => n.DisplayedNode is ReadOnlyRecipeNode).Sum(n => ((ReadOnlyRecipeNode)n.DisplayedNode).GetTotalAssemblerElectricalConsumption() + ((ReadOnlyRecipeNode)n.DisplayedNode).GetTotalBeaconElectricalConsumption());
				double pProduction = currentSelectionNodes.Where(n => n.DisplayedNode is ReadOnlyRecipeNode).Sum(n => ((ReadOnlyRecipeNode)n.DisplayedNode).GetTotalGeneratorElectricalProduction());
				int recipeNodeCount = currentSelectionNodes.Count(n => n.DisplayedNode is ReadOnlyRecipeNode);
				int buildingCount = (int)Math.Ceiling(currentSelectionNodes.Where(n => n.DisplayedNode is ReadOnlyRecipeNode).Sum(n => ((ReadOnlyRecipeNode)n.DisplayedNode).ActualSetValue));
				int beaconCount = currentSelectionNodes.Where(n => n.DisplayedNode is ReadOnlyRecipeNode).Sum(n => ((ReadOnlyRecipeNode)n.DisplayedNode).GetTotalBeacons());

				ToolTipRenderer.AddExtraToolTip(new TooltipInfo() { Text = string.Format("Power consumption: {0}\nPower production: {1}\nRecipe count: {2}\nBuilding count: {3}\nBeacon count: {4}", GraphicsStuff.DoubleToEnergy(pConsumption, "W"), GraphicsStuff.DoubleToEnergy(pProduction, "W"), recipeNodeCount, buildingCount, beaconCount), Direction = Direction.None, ScreenLocation = new Point(10, 10) });
			}

			//everything below will be drawn directly on the screen instead of scaled/shifted based on graph
			graphics.ResetTransform();

			if (!FullGraph)
			{
				//warning/error arrows
				ArrowRenderer.Paint(graphics, Graph);

				//floating tooltips
				ToolTipRenderer.Paint(graphics, TooltipsEnabled && !SubwindowOpen && currentDragOperation == DragOperation.None && !viewBeingDragged);
				ToolTipRenderer.ClearExtraToolTips();

				//paused border
				if (Graph != null && Graph.PauseUpdates) //graph null check is purely for design view
					graphics.DrawRectangle(pausedBorders, 0, 0, Width - 3, Height - 3);
			}
		}

		//----------------------------------------------Production Graph events

		private void Graph_NodeValuesUpdated(object sender, EventArgs e)
		{
			UpdateNodeVisuals();
		}

		private void Graph_LinkDeleted(object sender, NodeLinkEventArgs e)
		{
			BaseNodeElement supplier = nodeElementDictionary[e.nodeLink.Supplier];
			BaseNodeElement consumer = nodeElementDictionary[e.nodeLink.Consumer];

			LinkElement element = linkElementDictionary[e.nodeLink];
			linkElementDictionary.Remove(e.nodeLink);
			linkElements.Remove(element);
			element.Dispose();

			supplier.RequestStateUpdate();
			consumer.RequestStateUpdate();
			Invalidate();
		}

		private void Graph_LinkAdded(object sender, NodeLinkEventArgs e)
		{
			BaseNodeElement supplier = nodeElementDictionary[e.nodeLink.Supplier];
			BaseNodeElement consumer = nodeElementDictionary[e.nodeLink.Consumer];

			LinkElement element = new LinkElement(this, e.nodeLink, supplier, consumer);
			linkElementDictionary.Add(e.nodeLink, element);
			linkElements.Add(element);

			supplier.RequestStateUpdate();
			consumer.RequestStateUpdate();
			Invalidate();
		}

        private void Graph_NodeDeleted(object sender, NodeEventArgs e)
        {
            // Clean up any snapshot stored for this node
            if (e.node is ReadOnlyPassthroughNode passthroughNode)
                ConversionSnapshots.Remove(passthroughNode);

            BaseNodeElement element = nodeElementDictionary[e.node];
            nodeElementDictionary.Remove(e.node);
            nodeElements.Remove(element);
            selectedNodes.Remove(element);
            element.Dispose();
            Invalidate();
        }
        private void Graph_NodeAdded(object sender, NodeEventArgs e)
		{
			BaseNodeElement element = null;
			if (e.node is ReadOnlySupplierNode supplierNode)
				element = new SupplierNodeElement(this, supplierNode);
			else if (e.node is ReadOnlyConsumerNode consumerNode)
				element = new ConsumerNodeElement(this, consumerNode);
			else if (e.node is ReadOnlyPassthroughNode passthroughNode)
				element = new PassthroughNodeElement(this, passthroughNode);
			else if (e.node is ReadOnlyRecipeNode recipeNode)
				element = new RecipeNodeElement(this, recipeNode);
			else if (e.node is ReadOnlySpoilNode spoilNode)
				element = new SpoilNodeElement(this, spoilNode);
            else if (e.node is ReadOnlyPlantNode plantNode)
                element = new PlantNodeElement(this, plantNode);
            else
                Trace.Fail("Unexpected node type created in graph.");

			nodeElementDictionary.Add(e.node, element);
			nodeElements.Add(element);
			Invalidate();
		}

		//----------------------------------------------Mouse events

		private void ProductionGraphViewer_MouseDown(object sender, MouseEventArgs e)
		{
			downButtons |= e.Button;

			ToolTipRenderer.ClearFloatingControls();
			ActiveControl = null; //helps panels like IRChooserPanel (for item/recipe choosing) close when we click on the graph

			mouseDownStartScreenPoint = Control.MousePosition;
			Point graph_location = ScreenToGraph(e.Location);

            GraphElement clickedElement = (GraphElement)draggedLinkElement
                                        ?? (GraphElement)GetNodeAtPoint(ScreenToGraph(e.Location))
                                        ?? GetAnnotationAtPoint(ScreenToGraph(e.Location));

            // Double-click on an annotation opens its properties dialog
            if (e.Clicks == 2 && e.Button == MouseButtons.Left && clickedElement is AnnotationElement doubleClickedAnnotation)
            {
                doubleClickedAnnotation.ShowPropertiesDialog();
                return;
            }

            clickedElement?.MouseDown(graph_location, e.Button);
            if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Right))
			{
				ViewDragOriginPoint = graph_location;
			}
            else if (e.Button == MouseButtons.Left && (clickedElement == null || (clickedElement is AnnotationElement ua && !ua.IsSelected))) //selection
            {
                SelectionZoneOriginPoint = graph_location;
                SelectionZone = new Rectangle();
                if ((Control.ModifierKeys & Keys.Control) == 0 && (Control.ModifierKeys & Keys.Alt) == 0) //clear all selected nodes if we arent using modifier keys
                {
                    foreach (BaseNodeElement ne in selectedNodes)
                        ne.Highlighted = false;
                    selectedNodes.Clear();
                    foreach (AnnotationElement ann in selectedAnnotations)
                        ann.IsSelected = false;
                    selectedAnnotations.Clear();
                }
            }
        }

		private void ProductionGraphViewer_MouseUp(object sender, MouseEventArgs e)
		{
			downButtons &= ~e.Button;

			ToolTipRenderer.ClearFloatingControls();
			Point graph_location = ScreenToGraph(e.Location);
            GraphElement element = (GraphElement)draggedLinkElement
                            ?? (GraphElement)GetNodeAtPoint(graph_location)
                            ?? GetAnnotationAtPoint(graph_location);
            switch (e.Button)
			{
				case MouseButtons.Right:
					if (viewBeingDragged)
						viewBeingDragged = false;
					else if (currentDragOperation == DragOperation.None && element == null) //right click on an empty space -> show add item/recipe menu
					{
						Point screenPoint = new Point(e.Location.X - 150, 15);
						screenPoint.X = Math.Max(15, Math.Min(Width - 650, screenPoint.X)); //want to position the recipe selector such that it is well visible.

						rightClickMenu.MenuItems.Clear();
                        rightClickMenu.MenuItems.Add(new MenuItem("Add Item",
                                                new EventHandler((o, ee) =>
                                                {
                                                    AddItem(screenPoint, ScreenToGraph(e.Location));
                                                })));
                        rightClickMenu.MenuItems.Add(new MenuItem("Add Recipe",
                            new EventHandler((o, ee) =>
                            {
                                AddNewNode(screenPoint, new ItemQualityPair("adding disconnected recipe"), ScreenToGraph(e.Location), NewNodeType.Disconnected);
                            })));
                        rightClickMenu.MenuItems.Add(new MenuItem("Add Shape",
                            new EventHandler((o, ee) =>
                            {
                                AddShapeAnnotation(ScreenToGraph(e.Location));
                            })));
                        rightClickMenu.MenuItems.Add(new MenuItem("Add Text",
                            new EventHandler((o, ee) =>
                            {
                                AddTextAnnotation(ScreenToGraph(e.Location));
                            })));
                        rightClickMenu.Show(this, e.Location);
                    }
					else if(currentDragOperation != DragOperation.Selection)
						element?.MouseUp(graph_location, e.Button, (currentDragOperation == DragOperation.Item));
					break;
				case MouseButtons.Middle:
					viewBeingDragged = false;
					break;
				case MouseButtons.Left:
                    //finished selecting the given zone (process selected nodes)
                    if (currentDragOperation == DragOperation.Selection)
                    {
                        HashSet<AnnotationElement> zoneAnnotations = new HashSet<AnnotationElement>(
                                                    annotationElements.Where(a => a.LassoIntersectsEdge(SelectionZone)));

                        if ((Control.ModifierKeys & Keys.Alt) != 0) //removal zone processing
                        {
                            selectedNodes.ExceptWith(currentSelectionNodes);
                            selectedAnnotations.ExceptWith(zoneAnnotations);
                        }
                        else
                        {
                            if ((Control.ModifierKeys & Keys.Control) == 0) //if we arent using control, then we are just selecting
                            {
                                selectedNodes.Clear();
                                selectedAnnotations.Clear();
                            }
                            selectedNodes.UnionWith(currentSelectionNodes);
                            selectedAnnotations.UnionWith(zoneAnnotations);
                        }

                        // Sync IsSelected flags to the now-committed selectedAnnotations state
                        foreach (AnnotationElement ann in annotationElements)
                            ann.IsSelected = selectedAnnotations.Contains(ann);

                        currentSelectionNodes.Clear();
                    }

                    //this is a release of a left click (non-drag operation) -> modify selection if clicking on node & using modifier keys
                    else if (currentDragOperation == DragOperation.None && MouseDownElement is BaseNodeElement clickedNode)
					{
						if ((Control.ModifierKeys & Keys.Alt) != 0) //remove
						{
							selectedNodes.Remove(clickedNode);
							clickedNode.Highlighted = false;
							MouseDownElement = null;
							Invalidate();
						}
						else if ((Control.ModifierKeys & Keys.Control) != 0) //add if unselected, remove if selected
						{
							if (clickedNode.Highlighted)
								selectedNodes.Remove(clickedNode);
							else
								selectedNodes.Add(clickedNode);

							clickedNode.Highlighted = !clickedNode.Highlighted;
							MouseDownElement = null;
							Invalidate();
						}
                        else if (!viewBeingDragged) //left click without modifier keys -> pass click to node
                        {
                            clickedNode.MouseUp(graph_location, e.Button, false);
                        }
                    }
                    else if (currentDragOperation == DragOperation.None && MouseDownElement is AnnotationElement clickedAnnotation)
                    {
                        if ((Control.ModifierKeys & Keys.Alt) != 0) // remove from selection
                        {
                            selectedAnnotations.Remove(clickedAnnotation);
                            clickedAnnotation.IsSelected = false;
                            MouseDownElement = null;
                            Invalidate();
                        }
                        else if ((Control.ModifierKeys & Keys.Control) != 0) // toggle selection
                        {
                            if (clickedAnnotation.IsSelected)
                                selectedAnnotations.Remove(clickedAnnotation);
                            else
                                selectedAnnotations.Add(clickedAnnotation);
                            clickedAnnotation.IsSelected = !clickedAnnotation.IsSelected;
                            MouseDownElement = null;
                            Invalidate();
                        }
                        else if (!viewBeingDragged) // plain left-click — select only this annotation
                        {
                            foreach (BaseNodeElement ne in selectedNodes) ne.Highlighted = false;
                            selectedNodes.Clear();
                            foreach (AnnotationElement ann in selectedAnnotations) ann.IsSelected = false;
                            selectedAnnotations.Clear();
                            selectedAnnotations.Add(clickedAnnotation);
                            clickedAnnotation.IsSelected = true;
                            MouseDownElement = null;
                            Invalidate();
                        }
                    }
                    else if (currentDragOperation == DragOperation.None && MouseDownElement == null
                                                 && element is AnnotationElement unselectedAnnotation && !viewBeingDragged)
                    {
                        // Click on an unselected annotation — select it
                        if ((Control.ModifierKeys & Keys.Control) != 0)
                        {
                            selectedAnnotations.Add(unselectedAnnotation);
                            unselectedAnnotation.IsSelected = true;
                            Invalidate();
                        }
                        else if ((Control.ModifierKeys & Keys.Alt) == 0) // plain click
                        {
                            foreach (BaseNodeElement ne in selectedNodes) ne.Highlighted = false;
                            selectedNodes.Clear();
                            foreach (AnnotationElement ann in selectedAnnotations) ann.IsSelected = false;
                            selectedAnnotations.Clear();
                            selectedAnnotations.Add(unselectedAnnotation);
                            unselectedAnnotation.IsSelected = true;
                            Invalidate();
                        }
                    }
                    else if (!viewBeingDragged)
                        element?.MouseUp(graph_location, e.Button, (currentDragOperation == DragOperation.Item));

                    currentDragOperation = DragOperation.None;
                    MouseDownElement = null;
                    break;
            }
		}

		private void ProductionGraphViewer_MouseMove(object sender, MouseEventArgs e)
		{
			downButtons &= Control.MouseButtons; //only care about those buttons that were pressed down on this control. This is also the best place to update mouse changes done outside the control (ex: clicking down, dragging outside the window, letting go, moving mouse back into window)

			Point graph_location = ScreenToGraph(e.Location);

			if (currentDragOperation != DragOperation.Selection) //dont care about element mouse move operations during selection operation
			{
				GraphElement element = draggedLinkElement ?? MouseDownElement;
				element?.MouseMoved(graph_location);
			}

			switch (currentDragOperation)
			{
				case DragOperation.None: //check for minimal distance to be considered a drag operation
					Point dragDiff = Point.Subtract(Control.MousePosition, (Size)mouseDownStartScreenPoint);
					if (dragDiff.X * dragDiff.X + dragDiff.Y * dragDiff.Y > minDragDiff)
					{
						if ((downButtons & MouseButtons.Middle) == MouseButtons.Middle || (downButtons & MouseButtons.Right) == MouseButtons.Right)
							viewBeingDragged = true;

						if (MouseDownElement != null) //there is an item under the mouse during drag
							currentDragOperation = DragOperation.Item;
						else if ((downButtons & MouseButtons.Left) != 0)
							currentDragOperation = DragOperation.Selection;
					}
					break;

                case DragOperation.Item:
                    if (selectedNodes.Contains(MouseDownElement)) //dragging a selected node (group drag)
                    {
                        Point startPoint = MouseDownElement.Location;
                        GraphElement element = MouseDownElement;
                        MouseDownElement.Dragged(graph_location);
                        if (element == MouseDownElement) //check to ensure that the dragged operation hasnt changed the mousedown element -> as is the case with item tab to dragged link
                        {
                            Point endPoint = MouseDownElement.Location;
                            if (startPoint != endPoint)
                            {
                                foreach (BaseNodeElement node in selectedNodes.Where(node => node != MouseDownElement))
                                    node.SetLocation(new Point(node.X + endPoint.X - startPoint.X, node.Y + endPoint.Y - startPoint.Y));
                                // Also drag any selected annotations as part of the group
                                foreach (AnnotationElement ann in selectedAnnotations)
                                {
                                    ann.X += endPoint.X - startPoint.X;
                                    ann.Y += endPoint.Y - startPoint.Y;
                                }
                            }
                            Invalidate();
                        }
                    }
                    else if (MouseDownElement is AnnotationElement draggedAnn && selectedAnnotations.Contains(draggedAnn)) //dragging a selected annotation
                    {
                        if (draggedAnn.IsResizing) // resize — don't group-drag other annotations
                        {
                            MouseDownElement.Dragged(graph_location);
                            Invalidate();
                        }
                        else // move — group drag all selected annotations together
                        {
                            Point startPoint = draggedAnn.Location;
                            MouseDownElement.Dragged(graph_location);
                            Point endPoint = draggedAnn.Location;
                            if (startPoint != endPoint)
                                foreach (AnnotationElement ann in selectedAnnotations.Where(a => a != draggedAnn))
                                {
                                    ann.X += endPoint.X - startPoint.X;
                                    ann.Y += endPoint.Y - startPoint.Y;
                                }
                            Invalidate();
                        }
                    }
                    else //dragging a single unselected item
                    {
                        MouseDownElement.Dragged(graph_location);
                        Invalidate();
                    }

                    //accept middle mouse button for view dragging purposes (while dragging item or selection)
                    if ((downButtons & MouseButtons.Middle) == MouseButtons.Middle)
						viewBeingDragged = true;
					break;

                case DragOperation.Selection:
                    SelectionZone = new Rectangle(Math.Min(SelectionZoneOriginPoint.X, graph_location.X), Math.Min(SelectionZoneOriginPoint.Y, graph_location.Y), Math.Abs(SelectionZoneOriginPoint.X - graph_location.X), Math.Abs(SelectionZoneOriginPoint.Y - graph_location.Y));
                    currentSelectionNodes.Clear();
                    currentSelectionNodes.UnionWith(nodeElements.Where(element => element.IntersectsWithZone(SelectionZone, -20, -20)));

                    // Live visual preview for annotations — do NOT modify selectedAnnotations here.
                    // selectedAnnotations is the committed set; only MouseUp commits changes.
                    HashSet<AnnotationElement> zoneAnnotations = new HashSet<AnnotationElement>(
                        annotationElements.Where(a => a.LassoIntersectsEdge(SelectionZone)));

                    if ((Control.ModifierKeys & Keys.Alt) != 0) // remove preview
                    {
                        foreach (AnnotationElement ann in annotationElements)
                            ann.IsSelected = selectedAnnotations.Contains(ann) && !zoneAnnotations.Contains(ann);
                    }
                    else if ((Control.ModifierKeys & Keys.Control) != 0) // add preview
                    {
                        foreach (AnnotationElement ann in annotationElements)
                            ann.IsSelected = selectedAnnotations.Contains(ann) || zoneAnnotations.Contains(ann);
                    }
                    else // simple selection preview
                    {
                        foreach (AnnotationElement ann in annotationElements)
                            ann.IsSelected = zoneAnnotations.Contains(ann);
                    }

                    UpdateSelection();
                    //accept middle mouse button for view dragging purposes (while dragging item or selection)
                    if ((downButtons & MouseButtons.Middle) == MouseButtons.Middle)
						viewBeingDragged = true;
					break;
			}

			//dragging view (can happen during any drag operation)
			if (viewBeingDragged)
			{
				ViewOffset = Point.Add(ViewOffset, (Size)Point.Subtract(graph_location, (Size)ViewDragOriginPoint));// new Point(ViewOffset.X + (int)((graph_location.X - lastMouseDragPoint.X) / ViewScale), ViewOffset.Y + (int)((graph_location.Y - lastMouseDragPoint.Y) / ViewScale));
				UpdateGraphBounds(MouseDownElement == null); //only hard limit the graph bounds if we arent dragging an object
			}

			Invalidate();
		}

		private void ProductionGraphViewer_MouseWheel(object sender, MouseEventArgs e)
		{
            if (ContainsFocus && !this.Focused && !(findPanel.Visible && findTextBox.Focused)) //currently have a control created within this viewer active (ex: recipe chooser) -> dont want to scroll then
                return;

            ToolTipRenderer.ClearFloatingControls();

			Point oldZoomCenter = ScreenToGraph(e.Location);

			if (e.Delta > 0)
				ViewScale *= 1.1f;
			else
				ViewScale /= 1.1f;

			ViewScale = Math.Max(ViewScale, 0.01f);
			ViewScale = Math.Min(ViewScale, 2f);

			Point newZoomCenter = ScreenToGraph(e.Location);
			ViewOffset = new Point(ViewOffset.X + newZoomCenter.X - oldZoomCenter.X, ViewOffset.Y + newZoomCenter.Y - oldZoomCenter.Y);

			UpdateGraphBounds();
			Invalidate();
		}

		private void ProductionGraphViewer_KeyDown(object sender, KeyEventArgs e)
		{
			if (currentDragOperation == DragOperation.None)
			{
                if ((e.KeyCode == Keys.C || e.KeyCode == Keys.X) && (e.Modifiers & Keys.Control) == Keys.Control) //copy or cut
                {
                    StringBuilder stringBuilder = new StringBuilder();
                    var writer = new JsonTextWriter(new StringWriter(stringBuilder));

                    Graph.SerializeNodeIdSet = new HashSet<int>();
                    Graph.SerializeNodeIdSet.UnionWith(selectedNodes.Select(n => n.DisplayedNode.NodeID));

                    JsonSerializer serialiser = JsonSerializer.Create();
                    serialiser.Formatting = Formatting.None;
                    serialiser.Serialize(writer, Graph);

                    Graph.SerializeNodeIdSet.Clear();
                    Graph.SerializeNodeIdSet = null;

                    // Append any selected annotations to the clipboard JSON
                    if (selectedAnnotations.Count > 0)
                    {
                        JObject clipJson = JObject.Parse(stringBuilder.ToString());
                        clipJson["Annotations"] = new JArray(selectedAnnotations.Select(a => a.ToJson()));
                        Clipboard.SetText(clipJson.ToString(Formatting.None));
                    }
                    else
                        Clipboard.SetText(stringBuilder.ToString());

                    if (e.KeyCode == Keys.X) //cut
                    {
                        foreach (BaseNodeElement node in selectedNodes.ToList())
                            Graph.DeleteNode(node.DisplayedNode);
                        foreach (AnnotationElement ann in selectedAnnotations.ToList())
                            RemoveAnnotationElement(ann);
                        selectedAnnotations.Clear();
                    }
                }
                else if (e.KeyCode == Keys.V && (e.Modifiers & Keys.Control) == Keys.Control) //paste
                {
                    try
                    {
                        JObject json = JObject.Parse(Clipboard.GetText());
                        ImportNodesFromJson(json, ScreenToGraph(PointToClient(Cursor.Position)), false);
                        // Also paste any annotations that were part of the copied selection
                        if (json["Annotations"] != null)
                            ImportAnnotationsFromJson((JArray)json["Annotations"], ScreenToGraph(PointToClient(Cursor.Position)));
                    }
                    catch { Console.WriteLine("Non-Foreman paste detected."); } //clipboard string wasnt a proper json object, or didnt process properly. Likely answer: was a clip NOT from foreman.
                }
            }
			else if (currentDragOperation == DragOperation.Selection) //possible changes to selection type
				UpdateSelection();

			bool lockDragAxis = (Control.ModifierKeys & Keys.Shift) != 0;
			if (Grid.LockDragToAxis != lockDragAxis)
			{
				Grid.LockDragToAxis = lockDragAxis;
				Grid.DragOrigin = Grid.AlignToGrid(MouseDownElement?.Location ?? new Point());
				if (currentDragOperation == DragOperation.Item)
					MouseDownElement?.Dragged(ScreenToGraph(PointToClient(Control.MousePosition)));
			}
			Invalidate();
		}

		private void ProductionGraphViewer_KeyUp(object sender, KeyEventArgs e)
		{
			if (currentDragOperation == DragOperation.None)
			{
				switch (e.KeyCode)
				{
                    case Keys.Delete:
                        TryDeleteSelectedNodes();
                        foreach (AnnotationElement ann in selectedAnnotations.ToList())
                            RemoveAnnotationElement(ann);
                        selectedAnnotations.Clear();
                        e.Handled = true;
                        break;
                    case Keys.Escape:
						if (findPanel.Visible)
						{
							CloseFindPanel();
							e.Handled = true;
						}
						break;
				}
			}
			else if (currentDragOperation == DragOperation.Selection) //possible changes to selection type
				UpdateSelection();

			bool lockDragAxis = (Control.ModifierKeys & Keys.Shift) != 0;
			if (Grid.LockDragToAxis != lockDragAxis)
			{
				Grid.LockDragToAxis = lockDragAxis;
				Grid.DragOrigin = Grid.AlignToGrid(MouseDownElement?.Location ?? new Point());
				if (currentDragOperation == DragOperation.Item)
					MouseDownElement?.Dragged(ScreenToGraph(PointToClient(Control.MousePosition)));
			}
			Invalidate();
		}

		//----------------------------------------------Keyboard events

		protected override bool ProcessCmdKey(ref Message msg, Keys keyData) //arrow keys to move the current selection
		{

            // Don't intercept any keys while the find panel is open and focused
            if (findPanel.Visible && findTextBox.Focused)
                return base.ProcessCmdKey(ref msg, keyData);

            bool processed = true;
			int moveUnit = (Grid.CurrentGridUnit > 0) ? Grid.CurrentGridUnit : 6;
			int panUnit = (int)(10 / ViewScale);
			if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift) //large move
			{
				moveUnit = (Grid.CurrentMajorGridUnit > Grid.CurrentGridUnit) ? Grid.CurrentMajorGridUnit : moveUnit * 4;
				panUnit *= 5;
			}

            if ((keyData & Keys.KeyCode) == Keys.Left)
            {
                foreach (BaseNodeElement node in selectedNodes)
                    node.SetLocation(new Point(node.X - moveUnit, node.Y));
                foreach (AnnotationElement ann in selectedAnnotations)
                    ann.X -= moveUnit;
            }
            else if ((keyData & Keys.KeyCode) == Keys.Right)
            {
                foreach (BaseNodeElement node in selectedNodes)
                    node.SetLocation(new Point(node.X + moveUnit, node.Y));
                foreach (AnnotationElement ann in selectedAnnotations)
                    ann.X += moveUnit;
            }
            else if ((keyData & Keys.KeyCode) == Keys.Up)
            {
                foreach (BaseNodeElement node in selectedNodes)
                    node.SetLocation(new Point(node.X, node.Y - moveUnit));
                foreach (AnnotationElement ann in selectedAnnotations)
                    ann.Y -= moveUnit;
            }
            else if ((keyData & Keys.KeyCode) == Keys.Down)
            {
                foreach (BaseNodeElement node in selectedNodes)
                    node.SetLocation(new Point(node.X, node.Y + moveUnit));
                foreach (AnnotationElement ann in selectedAnnotations)
                    ann.Y += moveUnit;
            }

            else if ((keyData & Keys.KeyCode) == Keys.W && !SubwindowOpen)
			{
				ViewOffset += new Size(0, panUnit);
				UpdateGraphBounds();
			}
			else if ((keyData & Keys.KeyCode) == Keys.A && !SubwindowOpen)
			{
				ViewOffset += new Size(panUnit, 0);
				UpdateGraphBounds();
			}
			else if ((keyData & Keys.KeyCode) == Keys.S && !SubwindowOpen)
			{
				ViewOffset += new Size(0, -panUnit);
				UpdateGraphBounds();
			}
			else if ((keyData & Keys.KeyCode) == Keys.D && !SubwindowOpen)
			{
				ViewOffset += new Size(-panUnit, 0);
				UpdateGraphBounds();
			}
			else if ((keyData & Keys.KeyCode) == Keys.F && (keyData & Keys.Control) == Keys.Control && !SubwindowOpen)
			{
				OpenFindPanel();
				return true;
			}
			else
				processed = false;

			if (processed)
			{
				Invalidate();
				return true;
			}
			return base.ProcessCmdKey(ref msg, keyData);
		}

		//----------------------------------------------Viewpoint events

		private void BGTimer_Tick(object sender, EventArgs e)
		{
			//if (key)
		}

		private void ProductionGraphViewer_Resized(object sender, EventArgs e)
		{
			UpdateGraphBounds();
			Invalidate();
		}

		private void ProductionGraphViewer_LostFocus(object sender, EventArgs e)
		{
			Invalidate();
		}

		private void InitFindPanel()
		{
			findPanel = new Panel();
			findPanel.Height = 30;
			findPanel.Dock = DockStyle.Bottom;
			findPanel.BackColor = Color.FromArgb(240, 240, 240);
			findPanel.Visible = false;
			findPanel.TabStop = false;

			var findLabel = new Label();
			findLabel.Text = "Find:";
			findLabel.AutoSize = true;
			findLabel.Location = new Point(6, 7);

			findTextBox = new TextBox();
			findTextBox.Location = new Point(45, 4);
			findTextBox.Width = 200;
			findTextBox.KeyDown += FindTextBox_KeyDown;
            findTextBox.MouseWheel += ProductionGraphViewer_MouseWheel;


            var btnNext = new Button();
            btnNext.Text = "Go to Next";  // renamed
            btnNext.Location = new Point(252, 3);
            btnNext.Width = 80;           // slightly wider for new text
            btnNext.Height = 23;
            btnNext.TabStop = false;
            btnNext.Click += (s, e) => FindNext();

            var btnClose = new Button();
            btnClose.Text = "✕";
            btnClose.Location = new Point(339, 3);  // shifted right slightly
            btnClose.Width = 26;
            btnClose.Height = 23;
            btnClose.TabStop = false;
            btnClose.Click += (s, e) => CloseFindPanel();

            findStatusLabel = new Label();
            findStatusLabel.AutoSize = true;
            findStatusLabel.Location = new Point(372, 7);
            findStatusLabel.ForeColor = Color.DimGray;

            autoZoomCheckBox = new CheckBox();
            autoZoomCheckBox.Text = "Fit all results";
            autoZoomCheckBox.AutoSize = true;
            autoZoomCheckBox.Location = new Point(490, 6);
            autoZoomCheckBox.Checked = true;  // default on
            autoZoomCheckBox.TabStop = false;

            findPanel.Controls.Add(findLabel);
            findPanel.Controls.Add(findTextBox);
            findPanel.Controls.Add(btnNext);
            findPanel.Controls.Add(btnClose);
            findPanel.Controls.Add(findStatusLabel);
            findPanel.Controls.Add(autoZoomCheckBox);  // add checkbox

            this.Controls.Add(findPanel);
		}

		public void UpdateGraphBounds(bool limitView = true)
		{
			if (limitView)
			{
				Rectangle bounds = Graph.Bounds;
				Point screenCentre = ScreenToGraph(new Point(Width / 2, Height / 2));
				if (bounds.Width == 0 || bounds.Height == 0)
				{
					ViewOffset = new Point(0, 0);
				}
				else
				{
					int newX = ViewOffset.X;
					int newY = ViewOffset.Y;
					if (screenCentre.X < bounds.X) { newX -= bounds.X - screenCentre.X; }
					if (screenCentre.Y < bounds.Y) { newY -= bounds.Y - screenCentre.Y; }
					if (screenCentre.X > bounds.X + bounds.Width) { newX -= bounds.X + bounds.Width - screenCentre.X; }
					if (screenCentre.Y > bounds.Y + bounds.Height) { newY -= bounds.Y + bounds.Height - screenCentre.Y; }
					ViewOffset = new Point(newX, newY);
				}
			}

			VisibleGraphBounds = new Rectangle(
				(int)(-Width / (2 * ViewScale) - ViewOffset.X),
				(int)(-Height / (2 * ViewScale) - ViewOffset.Y),
				(int)(Width / ViewScale),
				(int)(Height / ViewScale));
		}

		private void ProductionGraphViewer_Resize(object sender, EventArgs e)
		{
			ToolTipRenderer?.ClearFloatingControls(); //resize can happen before tooltip is created (due to scaling)
		}

		private void ProductionGraphViewer_Leave(object sender, EventArgs e)
		{
			ToolTipRenderer.ClearFloatingControls();
		}

		//----------------------------------------------Helper functions (point conversions, alignment, etc)

		public Point ScreenToGraph(Point point)
		{
			return new Point(Convert.ToInt32(((point.X - Width / 2) / ViewScale) - ViewOffset.X), Convert.ToInt32(((point.Y - Height / 2) / ViewScale) - ViewOffset.Y));
		}

		public Point GraphToScreen(Point point)
		{
			return new Point(Convert.ToInt32(((point.X + ViewOffset.X) * ViewScale) + Width / 2), Convert.ToInt32(((point.Y + ViewOffset.Y) * ViewScale) + Height / 2));
		}

		//----------------------------------------------Save/Load JSON functions

		public void GetObjectData(SerializationInfo info, StreamingContext context)
		{
			//preset options
			info.AddValue("Version", Properties.Settings.Default.ForemanVersion);
			info.AddValue("Object", "ProductionGraphViewer");
            if (!SavedPresetNames.Contains(DCache.PresetName))
                SavedPresetNames.Insert(0, DCache.PresetName);
            info.AddValue("SavedPresetNames", SavedPresetNames);
            info.AddValue("IncludedMods", DCache.IncludedMods.Select(m => m.Key + "|" + m.Value));

			//graph viewer options
			info.AddValue("Unit", Graph.SelectedRateUnit);
			info.AddValue("ViewOffset", ViewOffset);
			info.AddValue("ViewScale", ViewScale);

			//graph defaults (saved here instead of within the graph since they are used here, plus they arent used during copy/paste)
			info.AddValue("ExtraProdForNonMiners", Graph.EnableExtraProductivityForNonMiners);
			info.AddValue("AssemblerSelectorStyle", Graph.AssemblerSelector.DefaultSelectionStyle);
			info.AddValue("ModuleSelectorStyle", Graph.ModuleSelector.DefaultSelectionStyle);
			info.AddValue("FuelPriorityList", Graph.FuelSelector.FuelPriority.Select(i => i.Name));

			//enabled lists
			info.AddValue("EnabledRecipes", DCache.Recipes.Values.Where(r => r.Enabled).Select(r => r.Name));
			info.AddValue("EnabledAssemblers", DCache.Assemblers.Values.Where(a => a.Enabled).Select(a => a.Name));
			info.AddValue("EnabledModules", DCache.Modules.Values.Where(m => m.Enabled).Select(m => m.Name));
			info.AddValue("EnabledBeacons", DCache.Beacons.Values.Where(b => b.Enabled).Select(b => b.Name));
            //planting results are always enabled

            //graph :)
            info.AddValue("ProductionGraph", Graph);

            //annotations (viewer-side; not part of the graph model)
            info.AddValue("Annotations",
                new JArray(annotationElements.Select(a => a.ToJson())).ToString(Formatting.None));
        }

		public void ImportNodesFromJson(JObject json, Point origin, bool loadSolverValues)
		{
			ProductionGraph.NewNodeCollection newNodeCollection = newNodeCollection = Graph.InsertNodesFromJson(DCache, json, loadSolverValues); //NOTE: missing items & recipes may be added here!
			if (newNodeCollection == null || newNodeCollection.newNodes.Count == 0)
				return;

			//update the locations of the new nodes to be centered around the mouse position (as opposed to wherever they were before)
			long xAve = 0;
			long yAve = 0;
			foreach (ReadOnlyBaseNode newNode in newNodeCollection.newNodes)
			{
				xAve += newNode.Location.X;
				yAve += newNode.Location.Y;
			}
			xAve /= newNodeCollection.newNodes.Count;
			yAve /= newNodeCollection.newNodes.Count;

			Point importCenter = new Point((int)xAve, (int)yAve);
			Size offset = (Size)Grid.AlignToGrid(Point.Subtract(origin, (Size)importCenter));
			foreach (ReadOnlyBaseNode newNode in newNodeCollection.newNodes)
				Graph.RequestNodeController(newNode).SetLocation(Point.Add(newNode.Location, offset));

			//update the selection to be just the newly imported nodes
			ClearSelection();
			foreach (BaseNodeElement newNodeElement in newNodeCollection.newNodes.Select(node => nodeElementDictionary[node]))
			{
				selectedNodes.Add(newNodeElement);
				newNodeElement.Highlighted = true;
			}
			Console.WriteLine(selectedNodes.Count);

			UpdateGraphBounds();
			Graph.UpdateNodeValues();
		}

        public void ImportAnnotationsFromJson(JArray annotationsJson, Point origin)
        {
            if (annotationsJson == null || annotationsJson.Count == 0)
                return;

            // Deserialise all annotations.
            List<AnnotationElement> newAnnotations = new List<AnnotationElement>();
            foreach (JObject annJson in annotationsJson)
            {
                try { newAnnotations.Add(AnnotationElement.FromJson(annJson, this)); }
                catch (Exception ex) { Console.WriteLine("Skipping bad annotation: " + ex.Message); }
            }

            if (newAnnotations.Count == 0)
                return;

            // Compute centroid of the pasted annotations.
            long xAve = 0, yAve = 0;
            foreach (AnnotationElement ann in newAnnotations) { xAve += ann.X; yAve += ann.Y; }
            xAve /= newAnnotations.Count;
            yAve /= newAnnotations.Count;

            // Shift so the centroid lands on 'origin' (same logic as ImportNodesFromJson).
            Point importCenter = new Point((int)xAve, (int)yAve);
            Point offset = Point.Subtract(origin, (Size)importCenter);

            foreach (AnnotationElement ann in newAnnotations)
            {
                ann.X += offset.X;
                ann.Y += offset.Y;
                ann.IsSelected = true;
                AddAnnotationElement(ann);
                selectedAnnotations.Add(ann);
            }

            Invalidate();
        }

        public void LoadPreset(Preset preset)
		{
			using (DataLoadForm form = new DataLoadForm(preset))
			{
				form.StartPosition = FormStartPosition.Manual;
				form.Left = ParentForm.Left + 150;
				form.Top = ParentForm.Top + 200;
				DialogResult result = form.ShowDialog(); //LOAD FACTORIO DATA
				if (DCache != null)
					DCache.Clear();
				DCache = form.GetDataCache();
				LastAssemblerQuality = DCache.DefaultQuality; //QUALITY UPDATE
				Graph.DefaultAssemblerQuality = DCache.DefaultQuality;
				Graph.MaxQualitySteps = 5; //DCache.QualityMaxChainLength;

				if (result == DialogResult.Abort)
				{
					MessageBox.Show("The current preset (" + Properties.Settings.Default.CurrentPresetName + ") is corrupt. Switching to the default preset (Factorio 2.0 Vanilla)");
					Properties.Settings.Default.CurrentPresetName = MainForm.DefaultPreset;
					using (DataLoadForm form2 = new DataLoadForm(new Preset(MainForm.DefaultPreset, false, true)))
					{
						form2.StartPosition = FormStartPosition.Manual;
						form2.Left = ParentForm.Left + 150;
						form2.Top = ParentForm.Top + 200;
						DialogResult result2 = form2.ShowDialog(); //LOAD default preset
						if (DCache != null)
							DCache.Clear();
						DCache = form2.GetDataCache();
						if (result2 == DialogResult.Abort)
							MessageBox.Show("The default preset (" + Properties.Settings.Default.CurrentPresetName + ") is corrupt. No Preset is loaded!");
					}
				}
				GC.Collect(); //loaded a new data cache - the old one should be collected (data caches can be over 1gb in size due to icons, plus whatever was in the old graph)
			}
			Invalidate();
		}

		public async Task LoadFromJson(JObject json, bool useFirstPreset, bool setEnablesFromJson)
		{
			if (json["Version"] == null || (int)json["Version"] != Properties.Settings.Default.ForemanVersion || json["Object"] == null || (string)json["Object"] != "ProductionGraphViewer")
			{
				json = VersionUpdater.UpdateSave(json, DCache);
				if (json == null) //update failed
					return;

				VersionUpdater.UpdateGraph((JObject)json["ProductionGraph"], DCache);
			}

			//grab mod list
			Dictionary<string, string> modSet = new Dictionary<string, string>();
			foreach (string str in json["IncludedMods"].Select(t => (string)t).ToList())
			{
				string[] mod = str.Split('|');
				modSet.Add(mod[0], mod[1]);
			}

			//grab include lists
			List<string> itemNames = json["ProductionGraph"]["IncludedItems"].Select(t => (string)t).ToList();
			List<string> assemblerNames = json["ProductionGraph"]["IncludedAssemblers"].Select(t => (string)t).ToList();
			List<string> qualityNames = json["ProductionGraph"]["IncludedQualities"].Select(t => (string)t["Key"]).ToList();
            List<RecipeShort> recipeShorts = RecipeShort.GetSetFromJson(json["ProductionGraph"]["IncludedRecipes"]);
			List<PlantShort> plantShorts = PlantShort.GetSetFromJson(json["ProductionGraph"]["IncludedPlantProcesses"]);

			//now - two options:
			// a) we are told to use the first preset (basically, the selected preset) - so that is the only one added to the possible Presets
			// b) we can choose preset - so go through each one and compare mod lists - ask to continue if
			// the preset list will then be checked for compatibility based on recipes, and the one with least errors will be used.
			// any errors will prompt a message box saying that 'incompatibility was found, but proceeding anyways'.
			List<Preset> allPresets = MainForm.GetValidPresetsList();
			List<PresetErrorPackage> presetErrors = new List<PresetErrorPackage>();
			Preset chosenPreset = null;
			if (useFirstPreset)
				chosenPreset = allPresets[0];
			else
			{
                // Load alias list — fall back to single SavedPresetName for old files
                if (json["SavedPresetNames"] != null)
                    SavedPresetNames = json["SavedPresetNames"].Select(t => (string)t).ToList();
                else if (json["SavedPresetName"] != null)
                    SavedPresetNames = new List<string> { (string)json["SavedPresetName"] };
                else
                    SavedPresetNames = new List<string>();

                // Try each candidate name in order before falling back to full search
                foreach (string candidateName in SavedPresetNames.ToList())
                {
                    Preset candidate = allPresets.FirstOrDefault(p => p.Name == candidateName);
                    if (candidate == null) continue;

                    var errors = await PresetProcessor.TestPreset(candidate, modSet, itemNames, assemblerNames, qualityNames, recipeShorts, plantShorts);
                    if (errors != null && errors.ErrorCount == 0)
                    {
                        chosenPreset = candidate;
                        break;
                    }
                    else
                    {
                        if (errors != null)
                            presetErrors.Add(errors);
                        allPresets.Remove(candidate);
                    }
                }

                //havent found the preset, or it returned some errors (not good) -> have to search for best fit (and leave the decision to user if we have multiple)
                if (chosenPreset == null)
				{
					foreach (Preset preset in allPresets)
					{
						PresetErrorPackage errors = await PresetProcessor.TestPreset(preset, modSet, itemNames, assemblerNames, qualityNames, recipeShorts, plantShorts);
						if (errors != null)
							presetErrors.Add(errors);
					}

					//show the menu to select the preferred preset
					using (PresetSelectionForm form = new PresetSelectionForm(presetErrors))
					{
						form.StartPosition = FormStartPosition.Manual;
						form.Left = ParentForm.Left + 50;
						form.Top = ParentForm.Top + 50;

						if (form.ShowDialog() != DialogResult.OK || form.ChosenPreset == null) //null check is not necessary - if we get an ok dialogresult, we know it will be set
							return;
						chosenPreset = form.ChosenPreset;
						Properties.Settings.Default.CurrentPresetName = chosenPreset.Name;
						Properties.Settings.Default.Save();
					}
				}
				else if (chosenPreset.Name != Properties.Settings.Default.CurrentPresetName) //we had to switch the preset to a new one (without the user having to select a preset from a list)
				{
					MessageBox.Show(string.Format("Loaded graph uses a different Preset.\nPreset switched from \"{0}\" to \"{1}\"", Properties.Settings.Default.CurrentPresetName, chosenPreset.Name));
					Properties.Settings.Default.CurrentPresetName = chosenPreset.Name;
					Properties.Settings.Default.Save();
				}
			}

			//clear graph
			ClearGraph();

			//load new preset
			LoadPreset(chosenPreset);

			//set up graph options
			Graph.SelectedRateUnit = (ProductionGraph.RateUnit)(int)json["Unit"];
			Graph.AssemblerSelector.DefaultSelectionStyle = (AssemblerSelector.Style)(int)json["AssemblerSelectorStyle"];
			Graph.ModuleSelector.DefaultSelectionStyle = (ModuleSelector.Style)(int)json["ModuleSelectorStyle"];
			foreach (string fuelType in json["FuelPriorityList"].Select(t => (string)t))
				if (DCache.Items.ContainsKey(fuelType))
					Graph.FuelSelector.UseFuel(DCache.Items[fuelType]);
			Graph.EnableExtraProductivityForNonMiners = (bool)json["ExtraProdForNonMiners"];

			//set up graph view options
			string[] viewOffsetString = ((string)json["ViewOffset"]).Split(',');
			ViewOffset = new Point(int.Parse(viewOffsetString[0]), int.Parse(viewOffsetString[1]));
			ViewScale = (float)json["ViewScale"];

			//update enabled statuses
			if (setEnablesFromJson)
			{
				foreach (Beacon beacon in DCache.Beacons.Values)
					beacon.Enabled = false;
				foreach (string beacon in json["EnabledBeacons"].Select(t => (string)t).ToList())
					if (DCache.Beacons.ContainsKey(beacon))
						DCache.Beacons[beacon].Enabled = true;

				foreach (Assembler assembler in DCache.Assemblers.Values)
					assembler.Enabled = false;
				foreach (string name in json["EnabledAssemblers"].Select(t => (string)t).ToList())
					if (DCache.Assemblers.ContainsKey(name))
						DCache.Assemblers[name].Enabled = true;
				DCache.RocketAssembler.Enabled = DCache.Assemblers["rocket-silo"]?.Enabled ?? false;

				foreach (Module module in DCache.Modules.Values)
					module.Enabled = false;
				foreach (string name in json["EnabledModules"].Select(t => (string)t).ToList())
					if (DCache.Modules.ContainsKey(name))
						DCache.Modules[name].Enabled = true;

				foreach (Recipe recipe in DCache.Recipes.Values)
					recipe.Enabled = false;
				foreach (string recipe in json["EnabledRecipes"].Select(t => (string)t).ToList())
					if (DCache.Recipes.ContainsKey(recipe))
						DCache.Recipes[recipe].Enabled = true;

                foreach (Recipe recipe in DCache.Recipes.Values)
                    if (recipe.Available && !recipe.Enabled)
                        recipe.Enabled = true;


            }

            //add all nodes
            ProductionGraph.NewNodeCollection collection = Graph.InsertNodesFromJson(DCache, (JObject)json["ProductionGraph"], true);

			//check for old import
			if (json["OldImport"] != null)
				foreach (ReadOnlyRecipeNode rNode in collection.newNodes.Where(node => node is ReadOnlyRecipeNode))
					((RecipeNodeController)Graph.RequestNodeController(rNode)).AutoSetAssembler(AssemblerSelector.Style.BestNonBurner);

            //load annotations (missing key is normal for older save files — treat as empty)
            if (json["Annotations"] != null)
            {
                try
                {
                    JArray annotationsJson = JArray.Parse((string)json["Annotations"]);
                    foreach (JObject annJson in annotationsJson)
                        AddAnnotationElement(AnnotationElement.FromJson(annJson, this));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to load annotations: " + ex.Message);
                }
            }

            //upgrade graph & values
            UpdateGraphBounds();
            Graph.UpdateNodeValues();
            this.Focus();
            Invalidate();
        }

		//Stolen from the designer file
		protected override void Dispose(bool disposing)
		{
			ClearGraph();


			if (disposing && (components != null))
			{
				components.Dispose();
			}

			rightClickMenu.Dispose();

			base.Dispose(disposing);
		}

		//----------------------------------------------Find feature

		private void OpenFindPanel()
		{
			findPanel.Visible = true;
			findTextBox.Focus();
			findTextBox.SelectAll();
		}

        private void CloseFindPanel()
        {
            foreach (BaseNodeElement ne in nodeElements)
                ne.FindHighlighted = false;

            findPanel.Visible = false;
            findResults.Clear();
            findResultIndex = -1;
            findStatusLabel.Text = "";
            lastSearchQuery = "";
            this.Focus();
            Invalidate();
        }

        private void ZoomToFitResults()
        {
            if (findResults.Count == 0) return;

            int minX = findResults.Min(n => n.X - n.Width / 2);
            int maxX = findResults.Max(n => n.X + n.Width / 2);
            int minY = findResults.Min(n => n.Y - n.Height / 2);
            int maxY = findResults.Max(n => n.Y + n.Height / 2);

            int centerX = (minX + maxX) / 2;
            int centerY = (minY + maxY) / 2;

            int padding = 150; // graph-space padding around results
            int boundsWidth = Math.Max(maxX - minX + padding * 2, 1);
            int boundsHeight = Math.Max(maxY - minY + padding * 2, 1);

            float scaleX = (float)Width / boundsWidth;
            float scaleY = (float)(Height - findPanel.Height) / boundsHeight;
            ViewScale = Math.Min(Math.Min(scaleX, scaleY), 2f);
            ViewScale = Math.Max(ViewScale, 0.01f);

            ViewOffset = new Point(-centerX, -centerY);

            UpdateGraphBounds(false);
            Invalidate();
        }

        private string lastSearchQuery = "";

		private void FindTextBox_KeyDown(object sender, KeyEventArgs e)
		{
			if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
			{
				if (findResults.Count > 0 && findTextBox.Text.Trim().ToLowerInvariant() == lastSearchQuery.ToLowerInvariant())
					FindNext();  // same query, just advance
				else
					ExecuteFind(findTextBox.Text);  // new or changed query, fresh search
				e.SuppressKeyPress = true;
			}
			else if (e.KeyCode == Keys.Escape)
			{
				CloseFindPanel();
				e.SuppressKeyPress = true;
			}
            else if ((e.KeyCode == Keys.C || e.KeyCode == Keys.X || e.KeyCode == Keys.V) && e.Control)
            {
                // Route Ctrl+C/X/V to the graph instead of the text box
                e.SuppressKeyPress = true;
                ProductionGraphViewer_KeyDown(this, new KeyEventArgs(e.KeyCode | Keys.Control));
            }
        }
        private void ExecuteFind(string query)
        {
            // Clear previous find highlights
            foreach (BaseNodeElement ne in nodeElements)
                ne.FindHighlighted = false;

            findResults.Clear();
            findResultIndex = -1;
            lastSearchQuery = query.Trim().ToLowerInvariant();
            findStatusLabel.ForeColor = Color.DimGray;

            if (string.IsNullOrWhiteSpace(query))
            {
                findStatusLabel.Text = "";
                Invalidate();
                return;
            }

            string q = query.Trim().ToLowerInvariant();

            foreach (BaseNodeElement ne in nodeElements)
            {
                string nodeName = GetNodeSearchText(ne.DisplayedNode);
                if (nodeName != null && nodeName.ToLowerInvariant().Contains(q))
                    findResults.Add(ne);
            }

            if (findResults.Count == 0)
            {
                findStatusLabel.Text = "No results";
                findStatusLabel.ForeColor = Color.DarkRed;
                Invalidate();
                return;
            }

            // Yellow highlight all results
            foreach (BaseNodeElement ne in findResults)
                ne.FindHighlighted = true;

            findResultIndex = 0;
            CenterOnNode(findResults[0]);  // blue highlight + center on first

            if (autoZoomCheckBox.Checked)
                ZoomToFitResults();  // override view to show all results

            UpdateFindStatus();
        }

        private void FindNext()
		{
			if (findResults.Count == 0)
			{
				ExecuteFind(findTextBox.Text);
				return;
			}

		    // Remove any stale results (nodes that have since been deleted)
		    findResults.RemoveAll(n => !nodeElements.Contains(n));

			if (findResults.Count == 0)
			{
				findStatusLabel.Text = "No results";
				findStatusLabel.ForeColor = Color.DarkRed;
				return;
			}

            // Clamp index in case RemoveAll shifted things, then advance
            findResultIndex = Math.Min(findResultIndex, findResults.Count - 1);
            findResultIndex = (findResultIndex + 1) % findResults.Count;

            // Clear previous highlight before centering on new node
            foreach (BaseNodeElement element in selectedNodes)
                element.Highlighted = false;
            selectedNodes.Clear();

            CenterOnNode(findResults[findResultIndex]);
            UpdateFindStatus();
        }

        private void UpdateFindStatus()
		{
			findStatusLabel.ForeColor = Color.DimGray;
			findStatusLabel.Text = string.Format("{0} of {1}", findResultIndex + 1, findResults.Count);
		}

		private static string GetNodeSearchText(ReadOnlyBaseNode node)
		{
			if (node is ReadOnlyRecipeNode rNode)
				return rNode.BaseRecipe.FriendlyName;
			if (node is ReadOnlySupplierNode sNode)
				return sNode.SuppliedItem.FriendlyName;
			if (node is ReadOnlyConsumerNode cNode)
				return cNode.ConsumedItem.FriendlyName;
			if (node is ReadOnlyPassthroughNode pNode)
				return pNode.PassthroughItem.FriendlyName;
			if (node is ReadOnlySpoilNode spNode)
				return spNode.InputItem.FriendlyName;
			if (node is ReadOnlyPlantNode plNode)
				return plNode.Seed.FriendlyName;
			return null;
		}

		private void CenterOnNode(BaseNodeElement node)
		{
			if (!nodeElements.Contains(node))
				return; // node was deleted, skip it
			// node.X, node.Y is the center of the node in graph space.
			// GraphToScreen formula: screen = ((graph + ViewOffset) * ViewScale) + (Width/2, Height/2)
			// To make node center map to screen center, solve for ViewOffset:
			//   Width/2 = ((node.X + ViewOffset.X) * ViewScale) + Width/2  =>  ViewOffset.X = -node.X
			ViewOffset = new Point(-node.X, -node.Y);

			// Sync Highlighted flag with selectedNodes (same pattern as SetSelection)
			foreach (BaseNodeElement element in selectedNodes)
				element.Highlighted = false;

			// Also select the node so it gets the blue highlight for free
			selectedNodes.Clear();
			selectedNodes.Add(node);
			node.Highlighted = true;

			UpdateGraphBounds(false);
			Invalidate();
		}
	}
}