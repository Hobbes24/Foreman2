using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Foreman
{
	public partial class SettingsForm : Form
	{
		public class SettingsFormOptions
		{
			public DataCache DCache { get; private set; }

			public List<Preset> Presets;
			public Preset SelectedPreset;
            public List<string> FilePresetNames; // null = no file loaded
            public bool RequireReload;

            public uint QualitySteps;

			public ProductionGraphViewer.LOD LevelOfDetail;
			public int NodeCountForSimpleView;
			public int IconsOnlyIconSize;

			public bool ArrowsOnLinks;
			public bool SimplePassthroughNodes;
			public bool DynamicLinkWidth;
			public bool AbbreviateSciPacks;
			public bool ShowRecipeToolTip;
			public bool RoundAssemblerCount;
			public bool LockedRecipeEditPanelPosition;
			public bool FlagOUSuppliedNodes;
			public bool FlagDarkMode;

			public bool ShowErrorArrows;
			public bool ShowWarningArrows;
			public bool ShowDisconnectedArrows;
			public bool ShowOUSuppliedArrows;

			public AssemblerSelector.Style DefaultAssemblerStyle;
			public ModuleSelector.Style DefaultModuleStyle;
			public NodeDirection DefaultNodeDirection;
			public bool SmartNodeDirection;
			
			public bool EnableExtraProductivityForNonMiners;
			public bool DEV_ShowUnavailableItems;
			public bool DEV_UseRecipeBWFilters;

			//settings storage: true = settings live in a file next to Foreman.exe and follow it between machines
			public bool UseSharedSettingsFile;

			public double Solver_LowPriorityPower;
			public double Solver_PullConsumerNodesPower;
			public bool Solver_PullConsumerNodes;

			public HashSet<DataObjectBase> EnabledObjects;
			public bool EnabledObjectsChanged; //only then are the enabled objects written to the preset

			public SettingsFormOptions(DataCache cache)
			{
				DCache = cache;
				Presets = new List<Preset>();
				EnabledObjects = new HashSet<DataObjectBase>();
				RequireReload = false;
			}
		}

		private static readonly Color AvailableObjectColor = Color.White;
		private static readonly Color UnavailableObjectColor = Color.Pink;
		private static readonly Color DisabledObjectTextColor = Color.Gray;
		private const int StatusColumnWidth = 64;

		public SettingsFormOptions Options;

		//---------------------------------------------------------enabled objects tab
		//The check boxes only mark objects for the Enable / Disable Checked buttons; the enabled state itself is the status
		//column. Both are kept in hash sets and neither is ever read back out of the virtual list views, which only know
		//their rows by index - so a row that the filter moves can never carry a stale state onto a different object.

		private class ObjectListState
		{
			public readonly List<DataObjectBase> All = new List<DataObjectBase>();
			public readonly List<DataObjectBase> Shown = new List<DataObjectBase>();
		}

		private readonly Dictionary<ListView, ObjectListState> objectLists = new Dictionary<ListView, ObjectListState>();
		private readonly Dictionary<DataObjectBase, int> objectIconIndices = new Dictionary<DataObjectBase, int>();
		private readonly HashSet<DataObjectBase> checkedObjects = new HashSet<DataObjectBase>();

		private ComboBox StatusFilterDropDown;
		private Button EnableCheckedButton;
		private Button DisableCheckedButton;
		private Button UncheckAllButton;
		private ToolTip enabledObjectsToolTip;

        private MouseHoverDetector mhDetector;
        private MainForm mainForm;

        // File preset alias list controls
        private GroupBox FilePresetsGroupBox;
        private ListBox FilePresetListBox;
        private Button RemoveFilePresetButton;

        //---------------------------------------------------------utility items tab
        //built in code rather than the designer: it is the one page whose contents belong to the active graph rather
        //than to the application, and keeping it out of the generated file makes that separation obvious

        private ProductionGraphViewer utilityViewer;
        private CheckedListBox utilityTrackedList;
        private TextBox utilityFilterBox;
        private ListBox utilityCandidateList;

        private class UtilityEntry
        {
            public readonly ItemQualityPair Item;
            public UtilityEntry(ItemQualityPair item) { Item = item; }
            public override string ToString() { return Item.Item.FriendlyName + "  (" + Item.Quality.Name + ")"; }
        }

        private void BuildUtilityItemsTab()
        {
            TabPage page = new TabPage("Utility Items");
            MainTabControl.TabPages.Add(page);

            utilityViewer = mainForm?.ActiveViewer;
            if (utilityViewer == null)
            {
                page.Controls.Add(new Label() { Text = "No graph is open.", AutoSize = true, Location = new Point(14, 14) });
                return;
            }

            page.Controls.Add(new Label()
            {
                Text = "Utility items belong to the current graph, not to Foreman as a whole.\nA ticked item has its links drawn as short stubs instead of full lines.",
                AutoSize = true,
                Location = new Point(14, 12)
            });

            GroupBox trackedBox = new GroupBox() { Text = "Tracked items  (ticked = hidden)", Location = new Point(14, 56), Size = new Size(310, 330) };
            utilityTrackedList = new CheckedListBox() { Location = new Point(10, 24), Size = new Size(290, 258), CheckOnClick = true, IntegralHeight = false };
            utilityTrackedList.ItemCheck += UtilityTrackedList_ItemCheck;
            Button removeButton = new Button() { Text = "Remove", Location = new Point(10, 292), Size = new Size(96, 27) };
            removeButton.Click += UtilityRemoveButton_Click;
            trackedBox.Controls.Add(utilityTrackedList);
            trackedBox.Controls.Add(removeButton);
            page.Controls.Add(trackedBox);

            GroupBox addBox = new GroupBox() { Text = "Add an item", Location = new Point(336, 56), Size = new Size(310, 330) };
            utilityFilterBox = new TextBox() { Location = new Point(10, 24), Size = new Size(290, 22) };
            utilityFilterBox.TextChanged += UtilityFilterBox_TextChanged;
            utilityCandidateList = new ListBox() { Location = new Point(10, 52), Size = new Size(290, 230), IntegralHeight = false };
            utilityCandidateList.DoubleClick += UtilityAddButton_Click;
            Button addButton = new Button() { Text = "Add", Location = new Point(10, 292), Size = new Size(96, 27) };
            addButton.Click += UtilityAddButton_Click;
            addBox.Controls.Add(utilityFilterBox);
            addBox.Controls.Add(utilityCandidateList);
            addBox.Controls.Add(addButton);
            page.Controls.Add(addBox);

            RefreshUtilityTrackedList();
            RefreshUtilityCandidates();
        }

        //sorted by item first so the quality tiers of one thing sit together rather than scattering alphabetically
        private static int CompareUtilityEntries(ItemQualityPair a, ItemQualityPair b)
        {
            int byItem = string.Compare(a.Item.FriendlyName, b.Item.FriendlyName, StringComparison.OrdinalIgnoreCase);
            return byItem != 0 ? byItem : a.Quality.Level.CompareTo(b.Quality.Level);
        }

        private void RefreshUtilityTrackedList()
        {
            utilityTrackedList.Items.Clear();
            List<KeyValuePair<ItemQualityPair, bool>> tracked = new List<KeyValuePair<ItemQualityPair, bool>>(utilityViewer.Graph.UtilityItems);
            tracked.Sort((a, b) => CompareUtilityEntries(a.Key, b.Key));
            foreach (KeyValuePair<ItemQualityPair, bool> entry in tracked)
                utilityTrackedList.Items.Add(new UtilityEntry(entry.Key), entry.Value);
        }

        private void UtilityTrackedList_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (utilityTrackedList.Items[e.Index] is UtilityEntry entry)
                utilityViewer.SetUtilityItemHidden(entry.Item, e.NewValue == CheckState.Checked);
        }

        private void UtilityRemoveButton_Click(object sender, EventArgs e)
        {
            if (!(utilityTrackedList.SelectedItem is UtilityEntry entry))
                return;
            utilityViewer.RemoveUtilityItem(entry.Item);
            RefreshUtilityTrackedList();
            RefreshUtilityCandidates();
        }

        private void UtilityFilterBox_TextChanged(object sender, EventArgs e) { RefreshUtilityCandidates(); }

        private void RefreshUtilityCandidates()
        {
            utilityCandidateList.Items.Clear();
            string filter = utilityFilterBox.Text.Trim();
            //every item crossed with every quality is far too long a list to be useful, so it stays empty until the
            //filter narrows it - which is also the fastest way to add something you would otherwise hunt for
            if (filter.Length < 2)
            {
                utilityCandidateList.Items.Add("(type at least two letters)");
                return;
            }

            List<ItemQualityPair> matches = new List<ItemQualityPair>();
            foreach (var item in utilityViewer.DCache.Items.Values)
            {
                if (item.FriendlyName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                foreach (var quality in utilityViewer.DCache.AvailableQualities)
                {
                    ItemQualityPair pair = new ItemQualityPair(item, quality);
                    if (!utilityViewer.Graph.UtilityItems.ContainsKey(pair))
                        matches.Add(pair);
                }
                if (matches.Count > 300)
                    break;
            }

            matches.Sort(CompareUtilityEntries);
            foreach (ItemQualityPair match in matches)
                utilityCandidateList.Items.Add(new UtilityEntry(match));
            if (utilityCandidateList.Items.Count == 0)
                utilityCandidateList.Items.Add("(no matches)");
        }

        private void UtilityAddButton_Click(object sender, EventArgs e)
        {
            if (!(utilityCandidateList.SelectedItem is UtilityEntry entry))
                return;
            //added items start hidden - that is why you went looking for them
            utilityViewer.SetUtilityItemHidden(entry.Item, true);
            RefreshUtilityTrackedList();
            RefreshUtilityCandidates();
        }

        public SettingsForm(SettingsFormOptions options, MainForm mainForm)
		{
			Options = options;

			InitializeComponent();

			this.mainForm = mainForm;
			BuildUtilityItemsTab();

			foreach (ListView view in new ListView[] { AssemblerListView, MinerListView, PowerListView, BeaconListView, ModuleListView, RecipeListView, QualityListView })
			{
				MainForm.SetDoubleBuffered(view);
				view.Columns.Add("Status", StatusColumnWidth);
				view.Columns[0].Width = view.Width - 32 - StatusColumnWidth;
				objectLists.Add(view, new ObjectListState());
			}
			BuildEnabledObjectsControls();

			SelectPresetMenuItem.Click += SelectPresetMenuItem_Click;
			DeletePresetMenuItem.Click += DeletePresetMenuItem_Click;

			mhDetector = new MouseHoverDetector(100, 200);
			mhDetector.Add(RecipeListView, RecipeListView_StartHover, RecipeListView_EndHover);

			CurrentPresetLabel.Text = Options.SelectedPreset.Name;
			PresetListBox.Items.AddRange(Options.Presets.ToArray());
			PresetListBox.Items.RemoveAt(0); //0 is the currently active preset.

            //settings

            QualityStepsInput.Value = Math.Max(QualityStepsInput.Minimum, Math.Min(QualityStepsInput.Maximum, (decimal)Options.QualitySteps));

            DynamicLWCheckBox.Checked = Options.DynamicLinkWidth;
			NodeCountForSimpleViewInput.Value = Math.Min(NodeCountForSimpleViewInput.Maximum, Options.NodeCountForSimpleView);

			IconsSizeInput.Value = Options.IconsOnlyIconSize;

			ArrowsOnLinksCheckBox.Checked = Options.ArrowsOnLinks;
			SimplePassthroughNodesCheckBox.Checked = Options.SimplePassthroughNodes;
			ShowNodeRecipeCheckBox.Checked = Options.ShowRecipeToolTip;
			RoundAssemblerCountCheckBox.Checked = Options.RoundAssemblerCount;
			AbbreviateSciPackCheckBox.Checked = Options.AbbreviateSciPacks;
			RecipeEditPanelPositionLockCheckBox.Checked = Options.LockedRecipeEditPanelPosition;
			FlagOUSupplyNodesCheckBox.Checked = Options.FlagOUSuppliedNodes;
			FlagDarkModeCheckBox.Checked = Options.FlagDarkMode;

			ErrorArrowsCheckBox.Checked = Options.ShowErrorArrows;
			WarningArrowsCheckBox.Checked = Options.ShowWarningArrows;
			DisconnectedArrowsCheckBox.Checked = Options.ShowDisconnectedArrows;
			OUSuppliedArrowsCheckBox.Checked = Options.ShowOUSuppliedArrows;

			switch(Options.LevelOfDetail)
			{
				case ProductionGraphViewer.LOD.Low:
					LowLodRadioButton.Checked = true;
					break;
				case ProductionGraphViewer.LOD.Medium:
					MediumLodRadioButton.Checked = true;
					break;
				case ProductionGraphViewer.LOD.High:
					HighLodRadioButton.Checked = true;
					break;
			}

			switch(Options.DefaultNodeDirection)
			{
				case NodeDirection.Down:
					NodeDirectionDropDown.SelectedIndex = 1;
					break;
				case NodeDirection.Up:
				default:
					NodeDirectionDropDown.SelectedIndex = 0;
					break;
			}

			SmartNodeDirectionCheckBox.Checked = Options.SmartNodeDirection;

			AssemblerSelectorStyleDropDown.Items.AddRange(AssemblerSelector.StyleNames);
			AssemblerSelectorStyleDropDown.SelectedIndex = (int)Options.DefaultAssemblerStyle;
			ModuleSelectorStyleDropDown.Items.AddRange(ModuleSelector.StyleNames);
			ModuleSelectorStyleDropDown.SelectedIndex = (int)Options.DefaultModuleStyle;

			ShowProductivityBonusOnAllCheckBox.Checked = Options.EnableExtraProductivityForNonMiners;
			ShowUnavailablesCheckBox.Checked = Options.DEV_ShowUnavailableItems;
            LoadBarrelingCheckBox.Checked = false;  // deprecated - barreling now handled automatically

			SharedSettingsCheckBox.Checked = Options.UseSharedSettingsFile;
			SharedSettingsCheckBox.CheckedChanged += SharedSettingsCheckBox_CheckedChanged;
			UpdateSharedSettingsInfoLabel();

            LowPriorityPowerInput.Value = Math.Min(LowPriorityPowerInput.Maximum, (decimal)Options.Solver_LowPriorityPower);
			PullConsumerNodesCheckBox.Checked = Options.Solver_PullConsumerNodes;
			PullConsumerNodesPowerInput.Value = Math.Min(PullConsumerNodesPowerInput.Maximum, (decimal)Options.Solver_PullConsumerNodesPower);

            //lists
            if (Options.DCache != null)
                LoadObjectLists();
			UpdateModList();
            InitializeFilePresetsSection();

            if (Options.DCache == null)
            {
                LoadEnabledFromSaveButton.Enabled = false;
                SetEnabledFromSciencePacksButton.Enabled = false;
                EnableAllButton.Enabled = false;
            }
        }
        private void UpdateModList()
		{
			Preset selectedPreset = (Preset)PresetListBox.SelectedItem;
			if (selectedPreset == null)
				selectedPreset = Options.SelectedPreset;

			PresetInfo presetInfo = PresetProcessor.ReadPresetInfo(selectedPreset);
			ModSelectionBox.Items.Clear();
			if (presetInfo.ModList != null)
			{
				List<string> modList = presetInfo.ModList.Select(kvp => kvp.Key + "_" + kvp.Value).ToList();
				modList.Sort();
				ModSelectionBox.Items.AddRange(modList.ToArray());
			}
			RecipeDifficultyLabel.Text = presetInfo.ExpensiveRecipes ? "Expensive" : "Normal";
			TechnologyDifficultyLabel.Text = presetInfo.ExpensiveTechnology ? "Expensive" : "Normal";

		}

        private void InitializeFilePresetsSection()
        {
            FilePresetsGroupBox = new GroupBox();
            FilePresetsGroupBox.Text = "Default Presets for this File";
            FilePresetsGroupBox.Dock = DockStyle.Fill;
            FilePresetsGroupBox.Font = new Font("Microsoft Sans Serif", 8.25f);
            FilePresetsGroupBox.Padding = new Padding(5);

            FilePresetListBox = new ListBox();
            FilePresetListBox.Dock = DockStyle.Fill;
            FilePresetListBox.SelectedIndexChanged += (s, e) =>
                RemoveFilePresetButton.Enabled = FilePresetListBox.SelectedIndex >= 0;

            RemoveFilePresetButton = new Button();
            RemoveFilePresetButton.Text = "Remove";
            RemoveFilePresetButton.Dock = DockStyle.Bottom;
            RemoveFilePresetButton.Enabled = false;
            RemoveFilePresetButton.Click += RemoveFilePresetButton_Click;

            FilePresetsGroupBox.Controls.Add(FilePresetListBox);
            FilePresetsGroupBox.Controls.Add(RemoveFilePresetButton);

            PresetsTable.RowCount++;
            PresetsTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 120f));
            PresetsTable.SetColumnSpan(FilePresetsGroupBox, 2);
            PresetsTable.Controls.Add(FilePresetsGroupBox, 0, PresetsTable.RowCount - 1);

            if (Options.FilePresetNames == null)
            {
                FilePresetsGroupBox.Enabled = false;
                FilePresetListBox.Items.Add("No file loaded");
            }
            else
            {
                foreach (string name in Options.FilePresetNames)
                    FilePresetListBox.Items.Add(name);
            }
        }

        private void RemoveFilePresetButton_Click(object sender, EventArgs e)
        {
            int idx = FilePresetListBox.SelectedIndex;
            if (idx < 0) return;
            FilePresetListBox.Items.RemoveAt(idx);
            Options.FilePresetNames.RemoveAt(idx);
            RemoveFilePresetButton.Enabled = false;
        }

        private void LoadObjectLists()
		{
			IconList.Images.Clear();
			IconList.Images.Add(DataCache.UnknownIcon);

			LoadObjectList(AssemblerListView, Options.DCache.Assemblers.Values.Where(a => a.EntityType == EntityType.Assembler));
			LoadObjectList(MinerListView, Options.DCache.Assemblers.Values.Where(a => a.EntityType == EntityType.Miner || a.EntityType == EntityType.OffshorePump));
			LoadObjectList(PowerListView, Options.DCache.Assemblers.Values.Where(a => a.EntityType == EntityType.Boiler || a.EntityType == EntityType.BurnerGenerator || a.EntityType == EntityType.Generator || a.EntityType == EntityType.Reactor));
			LoadObjectList(BeaconListView, Options.DCache.Beacons.Values);
			LoadObjectList(ModuleListView, Options.DCache.Modules.Values);
			LoadObjectList(RecipeListView, Options.DCache.Recipes.Values);
			LoadObjectList(QualityListView, Options.DCache.Qualities.Values);

			UpdateFilteredLists();
		}

		private void LoadObjectList(ListView view, IEnumerable<DataObjectBase> origin)
		{
			IOrderedEnumerable<DataObjectBase> orderedList;
			if (origin is IEnumerable<Quality>)
				orderedList = origin.OrderByDescending(a => a.Available).ThenBy(a => a);
			else
				orderedList = origin.OrderByDescending(a => a.Available).ThenBy(a => a.FriendlyName);

			foreach (DataObjectBase dObject in orderedList)
			{
				if (dObject.Icon != null)
				{
					IconList.Images.Add(dObject.Icon);
					objectIconIndices[dObject] = IconList.Images.Count - 1;
				}
				objectLists[view].All.Add(dObject);
			}
		}

		private void UpdateFilteredLists()
		{
			string filterString = FilterTextBox.Text.ToLower();
			bool showUnavailables = ShowUnavailablesFilterCheckBox.Checked;
			int statusFilter = StatusFilterDropDown.SelectedIndex; //0 = all, 1 = enabled only, 2 = disabled only

			foreach (KeyValuePair<ListView, ObjectListState> list in objectLists)
			{
				list.Value.Shown.Clear();
				foreach (DataObjectBase dObject in list.Value.All)
					if ((showUnavailables || dObject.Available) &&
						(statusFilter == 0 || (statusFilter == 1) == Options.EnabledObjects.Contains(dObject)) &&
						(string.IsNullOrEmpty(filterString) || dObject.FriendlyName.ToLower().Contains(filterString)))
						list.Value.Shown.Add(dObject);

				//a selection is a set of row numbers, so once the rows change it would sit on different objects
				list.Key.SelectedIndices.Clear();
				list.Key.VirtualListSize = list.Value.Shown.Count;
				list.Key.Invalidate();
			}
		}

		//the status filter and the checked-object buttons are fitted around the designer's table here, like the file presets box
		private void BuildEnabledObjectsControls()
		{
			groupBox2.Text = "Enabled Objects (remembered for this preset):";

			StatusFilterDropDown = new ComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Font = FilterTextBox.Font, Margin = new Padding(0, 2, 10, 2) };
			StatusFilterDropDown.Items.AddRange(new object[] { "All", "Enabled only", "Disabled only" });
			StatusFilterDropDown.SelectedIndex = 0;
			StatusFilterDropDown.SelectedIndexChanged += Filters_Changed;

			EnabledObjectsTable.Controls.Remove(ShowUnavailablesFilterCheckBox);
			ShowUnavailablesFilterCheckBox.Dock = DockStyle.None;
			ShowUnavailablesFilterCheckBox.Margin = new Padding(0, 5, 0, 2);
			FlowLayoutPanel showPanel = new FlowLayoutPanel() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(2, 0, 7, 0) };
			showPanel.Controls.Add(StatusFilterDropDown);
			showPanel.Controls.Add(ShowUnavailablesFilterCheckBox);
			Label showLabel = new Label() { Text = "Show:", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = label4.Font, Margin = label4.Margin };
			EnabledObjectsTable.Controls.Add(showLabel, 0, 4);
			EnabledObjectsTable.Controls.Add(showPanel, 1, 4);

			EnableCheckedButton = new Button() { Dock = DockStyle.Fill, Font = EnableAllButton.Font, Margin = new Padding(0, 2, 2, 2), UseVisualStyleBackColor = true };
			EnableCheckedButton.Click += (s, e) => SetCheckedObjectsEnabled(true);
			DisableCheckedButton = new Button() { Dock = DockStyle.Fill, Font = EnableAllButton.Font, Margin = new Padding(2), UseVisualStyleBackColor = true };
			DisableCheckedButton.Click += (s, e) => SetCheckedObjectsEnabled(false);
			Button checkShownButton = new Button() { Text = "Check Shown", Dock = DockStyle.Fill, Font = EnableAllButton.Font, Margin = new Padding(2), UseVisualStyleBackColor = true };
			checkShownButton.Click += CheckShownButton_Click;
			UncheckAllButton = new Button() { Text = "Uncheck All", Dock = DockStyle.Fill, Font = EnableAllButton.Font, Margin = new Padding(2, 2, 0, 2), UseVisualStyleBackColor = true };
			UncheckAllButton.Click += UncheckAllButton_Click;

			TableLayoutPanel actionTable = new TableLayoutPanel() { ColumnCount = 4, RowCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(4, 0, 7, 2) };
			actionTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
			actionTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
			actionTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
			actionTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
			actionTable.Controls.Add(EnableCheckedButton, 0, 0);
			actionTable.Controls.Add(DisableCheckedButton, 1, 0);
			actionTable.Controls.Add(checkShownButton, 2, 0);
			actionTable.Controls.Add(UncheckAllButton, 3, 0);

			EnabledObjectsTable.RowCount++;
			EnabledObjectsTable.RowStyles.Add(new RowStyle());
			EnabledObjectsTable.Controls.Add(actionTable, 0, EnabledObjectsTable.RowCount - 1);
			EnabledObjectsTable.SetColumnSpan(actionTable, 2);

			enabledObjectsToolTip = new ToolTip();
			string howTo = "Tick the objects to change (click their check boxes, or select rows and press Space), then press Enable or Disable Checked.\nPressing OK saves the result with the preset, so every graph that loads it starts the same way.";
			enabledObjectsToolTip.SetToolTip(EnableCheckedButton, howTo);
			enabledObjectsToolTip.SetToolTip(DisableCheckedButton, howTo);
			enabledObjectsToolTip.SetToolTip(checkShownButton, "Tick every object shown on the current tab");

			UpdateCheckedButtons();
		}

		//PRESETS LIST------------------------------------------------------------------------------------------
		private void EnableSelectionBox_Enter(object sender, EventArgs e) { PresetListBox.SelectedItem = null; }
		private void CurrentPresetLabel_Click(object sender, EventArgs e) { PresetListBox.SelectedItem = null; }

		private void PresetListBox_SelectedValueChanged(object sender, EventArgs e)
		{
			UpdateModList();
			if (PresetListBox.SelectedItem == null)
				CurrentPresetLabel.Font = new Font(CurrentPresetLabel.Font, FontStyle.Bold);
			else
				CurrentPresetLabel.Font = new Font(CurrentPresetLabel.Font, FontStyle.Regular);
		}

		private void PresetListBox_MouseDown(object sender, MouseEventArgs e)
		{
			if (e.Button != MouseButtons.Right) return;

			var index = PresetListBox.IndexFromPoint(e.Location);
			if (index != ListBox.NoMatches)
			{
				Preset rclickedPreset = ((Preset)PresetListBox.Items[index]);
				PresetListBox.SelectedIndex = index;

				if (rclickedPreset.IsCurrentlySelected)
				{
					SelectPresetMenuItem.Text = "Current Preset";
					SelectPresetMenuItem.Enabled = false;
				}
				else
				{
					SelectPresetMenuItem.Text = "Use This Preset";
					SelectPresetMenuItem.Enabled = true;
				}
				SelectPresetMenuItem.Enabled = !rclickedPreset.IsCurrentlySelected;
				if (rclickedPreset.IsDefaultPreset)
				{
					DeletePresetMenuItem.Text = "Default Preset";
					DeletePresetMenuItem.Enabled = false;
				}
				else
				{
					DeletePresetMenuItem.Text = "Delete This Preset";
					DeletePresetMenuItem.Enabled = !rclickedPreset.IsCurrentlySelected;
				}

				PresetMenuStrip.Show(Cursor.Position);
				PresetMenuStrip.Visible = true;
			}
			else
				PresetMenuStrip.Visible = false;
		}

		private void PresetListBox_MouseDoubleClick(object sender, MouseEventArgs e)
		{
			var index = PresetListBox.IndexFromPoint(e.Location);
			if (index != ListBox.NoMatches)
			{
				Options.SelectedPreset = ((Preset)PresetListBox.Items[index]);
				UpdateSettings();
				DialogResult = DialogResult.OK;
				this.Close();

			}
		}

		private void DeletePresetMenuItem_Click(object sender, EventArgs e)
		{
			Preset selectedPreset = (Preset)PresetListBox.SelectedItem;
			if (!selectedPreset.IsCurrentlySelected && !selectedPreset.IsDefaultPreset) //safety check - should always pass
			{
				if (MessageBox.Show("Are you sure you wish to delete the \"" + selectedPreset.Name + "\" preset? This is irreversible.", "Confirm Delete", MessageBoxButtons.YesNo) == DialogResult.Yes)
				{
					string jsonPath = Path.Combine(new string[] { Application.StartupPath, "Presets", selectedPreset.Name + ".pjson" });
					string customjsonPath = Path.Combine(new string[] { Application.StartupPath, "Presets", selectedPreset.Name + ".json" });
					string iconPath = Path.Combine(new string[] { Application.StartupPath, "Presets", selectedPreset.Name + ".dat" });

					if (File.Exists(jsonPath))
						File.Delete(jsonPath);
					if (File.Exists(customjsonPath))
						File.Delete(customjsonPath);
					if (File.Exists(iconPath))
						File.Delete(iconPath);
						SafeIO.DeleteFile(PresetProcessor.GetEnabledChoicesPath(selectedPreset.Name));

					PresetListBox.Items.Remove(selectedPreset);
					Options.Presets.Remove(selectedPreset);
				}
			}
		}

		private void SelectPresetMenuItem_Click(object sender, EventArgs e)
		{
			Options.SelectedPreset = (Preset)PresetListBox.SelectedItem;
			UpdateSettings();
			DialogResult = DialogResult.OK;
			this.Close();
		}

		private void Filters_Changed(object sender, EventArgs e)
		{
			UpdateFilteredLists();
		}

		//LIST VIEWS------------------------------------------------------------------------------------------

		private void ListView_KeyDown(object sender, KeyEventArgs e)
		{
			ListView view = (ListView)sender;
			if (e.KeyCode == Keys.A && (e.Modifiers & Keys.Control) != 0)
				NativeMethods.SelectAllItems(view);
			else if (e.KeyCode == Keys.Space && view.SelectedIndices.Count > 0)
			{
				ToggleChecked(view, view.FocusedItem?.Index ?? view.SelectedIndices[0]);
				e.Handled = true;
				e.SuppressKeyPress = true;
			}
		}

		private void ListView_MouseClick(object sender, MouseEventArgs e)
		{
			ListView view = (ListView)sender;
			ListViewHitTestInfo hit = view.HitTest(e.Location);
			if (hit.Item != null && (hit.Location == ListViewHitTestLocations.StateImage || e.X < hit.Item.Bounds.Left + 16))
				ToggleChecked(view, hit.Item.Index);
		}

		//the second click of a double click is reported here instead of as a click, and the check box should still flip on it
		private void ListView_MouseDoubleClick(object sender, MouseEventArgs e) { ListView_MouseClick(sender, e); }

		//flips the check on one row; if that row is part of the selection, the whole selection follows it
		private void ToggleChecked(ListView view, int row)
		{
			List<DataObjectBase> shown = objectLists[view].Shown;
			if (row < 0 || row >= shown.Count)
				return;

			bool check = !checkedObjects.Contains(shown[row]);
			List<int> rows = view.SelectedIndices.Contains(row) ? view.SelectedIndices.Cast<int>().ToList() : new List<int>() { row };
			foreach (int r in rows.Where(r => r < shown.Count))
			{
				if (check)
					checkedObjects.Add(shown[r]);
				else
					checkedObjects.Remove(shown[r]);
			}
			view.Invalidate();
			UpdateCheckedButtons();
		}

		//a fresh item every time, built from the hash sets - see the note on the enabled objects tab fields
		private void ListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
		{
			DataObjectBase dObject = objectLists[(ListView)sender].Shown[e.ItemIndex];
			bool enabled = Options.EnabledObjects.Contains(dObject);

			ListViewItem lvItem = new ListViewItem(new string[] { dObject.FriendlyName, enabled ? "Enabled" : "Disabled" }, objectIconIndices.TryGetValue(dObject, out int iconIndex) ? iconIndex : 0);
			lvItem.Tag = dObject;
			lvItem.Name = dObject.Name;
			lvItem.BackColor = dObject.Available ? AvailableObjectColor : UnavailableObjectColor;
			lvItem.ForeColor = enabled ? Color.Black : DisabledObjectTextColor;
			lvItem.Checked = true; //a new item shows no check box at all until its state has been set once, and setting false first changes nothing
			lvItem.Checked = checkedObjects.Contains(dObject);
			e.Item = lvItem;
		}

		private void AssemblerListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { ListView_RetrieveVirtualItem(sender, e); }
		private void MinerListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { ListView_RetrieveVirtualItem(sender, e); }
		private void PowerListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { ListView_RetrieveVirtualItem(sender, e); }
		private void BeaconListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { ListView_RetrieveVirtualItem(sender, e); }
		private void ModuleListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { ListView_RetrieveVirtualItem(sender, e); }
		private void RecipeListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { ListView_RetrieveVirtualItem(sender, e); }
		private void QualityListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e) { ListView_RetrieveVirtualItem(sender, e); }

		private void RecipeListView_StartHover(object sender, MouseEventArgs e)
		{
			ListViewItem lvi = ((ListView)sender).GetItemAt(e.Location.X, e.Location.Y);
			Point location = new Point(e.X + 15, e.Y);
			if (lvi != null)
			{
				RecipeToolTip.SetRecipe(lvi.Tag as Recipe);
				RecipeToolTip.Show((Control)sender, location);
			}
		}
		private void RecipeListView_EndHover(object sender, EventArgs e)
		{
			RecipeToolTip.Hide((Control)sender);
		}

		//CONFIRM / RELOAD / CANCEL------------------------------------------------------------------------------------------
		private void ConfirmButton_Click(object sender, EventArgs e)
		{
			UpdateSettings();
			DialogResult = DialogResult.OK;
			this.Close();
		}

		private void SharedSettingsCheckBox_CheckedChanged(object sender, EventArgs e)
		{
			UpdateSharedSettingsInfoLabel();
		}

		private void UpdateSharedSettingsInfoLabel()
		{
			if (!SharedSettingsCheckBox.Checked)
			{
				SharedSettingsInfoLabel.Text = "Settings are kept on this machine only (and are reset by a windows user, machine, or Foreman version change).";
				return;
			}

			string text = "Settings are kept in " + SharedSettings.FilePath + " - put Foreman on a drive both machines can reach and they share one set of settings.";
			string lastWrite = SharedSettings.GetLastWriteDescription();
			if (lastWrite != null)
				text += " (" + lastWrite + ")";
			SharedSettingsInfoLabel.Text = text;
		}

		private void CancelButton_Click(object sender, EventArgs e)
		{
			DialogResult = DialogResult.Cancel;
			this.Close();
		}

		private void UpdateSettings()
		{
			Options.QualitySteps = (uint)QualityStepsInput.Value;

			Options.LevelOfDetail = LowLodRadioButton.Checked ? ProductionGraphViewer.LOD.Low : MediumLodRadioButton.Checked ? ProductionGraphViewer.LOD.Medium : ProductionGraphViewer.LOD.High;
			Options.NodeCountForSimpleView = (int)NodeCountForSimpleViewInput.Value;
			Options.IconsOnlyIconSize = (int)IconsSizeInput.Value;

			Options.ArrowsOnLinks = ArrowsOnLinksCheckBox.Checked;
			Options.SimplePassthroughNodes = SimplePassthroughNodesCheckBox.Checked;
			Options.DynamicLinkWidth = DynamicLWCheckBox.Checked;
			Options.AbbreviateSciPacks = AbbreviateSciPackCheckBox.Checked;
			Options.ShowRecipeToolTip = ShowNodeRecipeCheckBox.Checked;
			Options.RoundAssemblerCount = RoundAssemblerCountCheckBox.Checked;
			Options.LockedRecipeEditPanelPosition = RecipeEditPanelPositionLockCheckBox.Checked;
			Options.FlagOUSuppliedNodes = FlagOUSupplyNodesCheckBox.Checked;

			Options.ShowErrorArrows = ErrorArrowsCheckBox.Checked;
			Options.ShowWarningArrows = WarningArrowsCheckBox.Checked;
			Options.ShowDisconnectedArrows = DisconnectedArrowsCheckBox.Checked;
			Options.ShowOUSuppliedArrows = OUSuppliedArrowsCheckBox.Checked;

			Options.DefaultAssemblerStyle = (AssemblerSelector.Style)AssemblerSelectorStyleDropDown.SelectedIndex;
			Options.DefaultModuleStyle = (ModuleSelector.Style)ModuleSelectorStyleDropDown.SelectedIndex;
			Options.DefaultNodeDirection = NodeDirectionDropDown.SelectedIndex == 0 ? NodeDirection.Up : NodeDirection.Down;
			Options.SmartNodeDirection = SmartNodeDirectionCheckBox.Checked;

			Options.EnableExtraProductivityForNonMiners = ShowProductivityBonusOnAllCheckBox.Checked;
			Options.DEV_ShowUnavailableItems = ShowUnavailablesCheckBox.Checked;
			Options.UseSharedSettingsFile = SharedSettingsCheckBox.Checked;
            // No longer needed, barreling filter is now automatic
            // Options.DEV_UseRecipeBWFilters = !LoadBarrelingCheckBox.Checked;

            Options.Solver_LowPriorityPower = (double)LowPriorityPowerInput.Value;
			Options.Solver_PullConsumerNodes = PullConsumerNodesCheckBox.Checked;
			Options.Solver_PullConsumerNodesPower = (double)PullConsumerNodesPowerInput.Value;

			if (Options.FlagDarkMode != FlagDarkModeCheckBox.Checked) {
				Options.FlagDarkMode = FlagDarkModeCheckBox.Checked;
				if (Options.FlagDarkMode) {
					mainForm.SetDarkMode();
				} else {
					mainForm.SetLightMode();
				}
			}
		}

		//PRESET FORMS (Import / compare)------------------------------------------------------------------------------------------

		private void ImportPresetButton_Click(object sender, EventArgs e)
		{
			using (PresetImportForm form = new PresetImportForm())
			{
				form.StartPosition = FormStartPosition.Manual;
				form.Left = this.Left + 250;
				form.Top = this.Top + 50;
				DialogResult result = form.ShowDialog();

				if (form.ImportStarted)
					GC.Collect(); //we just processed a new preset (either fully or cancelled) - this required the opening of (potentially) alot of zip files and processing of a ton of bitmaps that are now stuck in garbate. In large mod packs like A&B this could clear out 2GB+ of memory.

				if (result == DialogResult.OK && !string.IsNullOrEmpty(form.NewPresetName)) //we have added a new preset
				{
					Preset newPreset = Options.Presets.FirstOrDefault(p => p.Name.ToLower() == form.NewPresetName.ToLower()); //extra check just in case we were overwriting
					if (newPreset == null)
					{
						newPreset = new Preset(form.NewPresetName, false, false);
						Options.Presets.Add(newPreset);
						PresetListBox.Items.Add(newPreset);
					}


					if(newPreset == Options.Presets[0]) //we have overwritten the currently active preset. Must force a reload
					{
						Options.RequireReload = true;
						UpdateSettings();
						DialogResult = DialogResult.OK;
						this.Close();
					}
					else if (MessageBox.Show("Preset import complete! Do you wish to switch to the new preset?", "", MessageBoxButtons.YesNo) == DialogResult.Yes)
					{
						Options.SelectedPreset = newPreset;
						UpdateSettings();
						DialogResult = DialogResult.OK;
						this.Close();
					}
				}
			}
		}

		private void ComparePresetsButton_Click(object sender, EventArgs e)
		{
			if (Options.Presets.Count < 2)
			{
				MessageBox.Show("Can not compare presets!\n...you only have 1 preset :/");
				return;
			}

			using (PresetComparatorForm form = new PresetComparatorForm())
			{
				form.StartPosition = FormStartPosition.Manual;
				form.Left = this.Left + 50;
				form.Top = this.Top + 50;
				form.ShowDialog();
			}
		}

		//SET ENABLED STATUS------------------------------------------------------------------------------------------

		private void LoadEnabledFromSaveButton_Click(object sender, EventArgs e)
		{
			using (SaveFileLoadForm form = new SaveFileLoadForm(Options.DCache, Options.EnabledObjects))
			{
				form.StartPosition = FormStartPosition.Manual;
				form.Left = this.Left + 50;
				form.Top = this.Top + 50;
				DialogResult result = form.ShowDialog();

				if(result == DialogResult.OK)
					EnabledObjectsModified();
				else if (result == DialogResult.Abort)
					MessageBox.Show("Error while reading save file. Try running factorio, opening the save game, saving again, and retrying?");
			}
		}

		private void SetEnabledFromSciencePacksButton_Click(object sender, EventArgs e)
		{
			using (SciencePacksLoadForm form = new SciencePacksLoadForm(Options.DCache, Options.EnabledObjects))
			{
				form.StartPosition = FormStartPosition.Manual;
				form.Left = this.Left + 50;
				form.Top = this.Top + 50;
				DialogResult result = form.ShowDialog();

				if (result == DialogResult.OK)
					EnabledObjectsModified();
			}
		}

		private void EnableAllButton_Click(object sender, EventArgs e)
		{
			Options.EnabledObjects.Clear();
			Options.EnabledObjects.Add(Options.DCache.PlayerAssembler);

			foreach (Assembler assembler in Options.DCache.Assemblers.Values.Where(m => m.AssociatedItems.Any(i => i.Available)))
				Options.EnabledObjects.Add(assembler);

			foreach (Beacon beacon in Options.DCache.Beacons.Values.Where(m => m.AssociatedItems.Any(i => i.Available)))
				Options.EnabledObjects.Add(beacon);

			foreach (Module module in Options.DCache.Modules.Values.Where(m => m.AssociatedItem?.Available ?? false))
				Options.EnabledObjects.Add(module);

			foreach (Recipe recipe in Options.DCache.Recipes.Values.Where(r => r.Available))
				Options.EnabledObjects.Add(recipe);

            foreach (Quality quality in Options.DCache.Qualities.Values.Where(r => r.Available))
                Options.EnabledObjects.Add(quality);

            EnabledObjectsModified();
		}

		//after anything on the tab changes which objects are enabled: the status column and the enabled / disabled filter both follow it
		private void EnabledObjectsModified()
		{
			Options.EnabledObjectsChanged = true;
			UpdateFilteredLists();
			UpdateCheckedButtons();
		}

		private void SetCheckedObjectsEnabled(bool enable)
		{
			foreach (DataObjectBase dObject in checkedObjects)
			{
				if (enable)
					Options.EnabledObjects.Add(dObject);
				else
					Options.EnabledObjects.Remove(dObject);
			}
			checkedObjects.Clear();
			EnabledObjectsModified();
		}

		private void CheckShownButton_Click(object sender, EventArgs e)
		{
			if (!(EnabledObjectsTabControl.SelectedTab?.Controls.OfType<ListView>().FirstOrDefault() is ListView view))
				return;
			checkedObjects.UnionWith(objectLists[view].Shown);
			view.Invalidate();
			UpdateCheckedButtons();
		}

		private void UncheckAllButton_Click(object sender, EventArgs e)
		{
			checkedObjects.Clear();
			foreach (ListView view in objectLists.Keys)
				view.Invalidate();
			UpdateCheckedButtons();
		}

		private void UpdateCheckedButtons()
		{
			bool anyChecked = checkedObjects.Count > 0;
			string count = anyChecked ? " (" + checkedObjects.Count + ")" : "";
			EnableCheckedButton.Text = "Enable Checked" + count;
			DisableCheckedButton.Text = "Disable Checked" + count;
			EnableCheckedButton.Enabled = anyChecked;
			DisableCheckedButton.Enabled = anyChecked;
			UncheckAllButton.Enabled = anyChecked;
		}

		protected override void OnClosed(EventArgs e)
		{
			mhDetector.Dispose();
			enabledObjectsToolTip?.Dispose();
			base.OnClosed(e);
		}
	}
}
