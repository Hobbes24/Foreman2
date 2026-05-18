using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static Foreman.NativeMethods;

namespace Foreman
{
	public partial class MainForm : Form
	{
		internal const string DefaultPreset = "Factorio 2.0 Vanilla";
		internal string DefaultAppName;

        // ── Per-tab state ──────────────────────────────────────────────────────
        private class GraphTabState
        {
            public string SaveFilePath  = null;
            public GraphSummaryForm SummaryForm = null;
            public int  MinorGridlinesIndex  = 0;
            public int  MajorGridlinesIndex  = 0;
            public bool ShowGridlines        = false;
        }
        private readonly List<GraphTabState> tabStates = new List<GraphTabState>();
        private int _newTabCounter = 0;
        private bool _suppressToolbarEvents = false;

        // Convenience accessors
        private ProductionGraphViewer ActiveViewer => GraphTabControl.ActiveViewer;
        private GraphTabState ActiveTabState =>
            GraphTabControl.SelectedIndex >= 0 && GraphTabControl.SelectedIndex < tabStates.Count
                ? tabStates[GraphTabControl.SelectedIndex] : null;

        public MainForm()
		{
			InitializeComponent();
			this.DoubleBuffered = true;
			DefaultAppName = this.Text;
			SetStyle(ControlStyles.SupportsTransparentBackColor, true);
			if (Properties.Settings.Default.FlagDarkMode) {
				SetDarkMode();
			}
		}

		public void SetDarkMode() {
			int trueVal = 1;
			DwmSetWindowAttribute(this.Handle, DwmWindowAttribute.DWMWA_USE_IMMERSIVE_DARK_MODE, ref trueVal, Marshal.SizeOf(typeof(int)));
			ChangeTheme(Color.FromArgb(23, 23, 23), Color.FromArgb(124, 124, 124), this);
		}

		public void SetLightMode() {
			int falseVal = 0;
			DwmSetWindowAttribute(this.Handle, DwmWindowAttribute.DWMWA_USE_IMMERSIVE_DARK_MODE, ref falseVal, Marshal.SizeOf(typeof(int)));
			ChangeTheme(DefaultBackColor, DefaultForeColor, this);
		}

		private static void ChangeTheme(Color bg, Color fg, Control root) {
			root.BackColor = bg;
			root.ForeColor = fg;
			ChangeTheme(bg, fg, root.Controls);
		}

		private static void ChangeTheme(Color bg, Color fg, Control.ControlCollection container) {
			foreach (Control component in container) {
				ChangeTheme(bg, fg, component.Controls);
				component.BackColor = bg;
				component.ForeColor = fg;
				if (component is Button b) {
					b.UseVisualStyleBackColor = true;
					b.FlatStyle = FlatStyle.Flat;
				} else if (component is ProductionGraphViewer pgv) {
					GridManager.SetGridColors(bg, fg);
				}
			}

		}

        private void MainForm_Load(object sender, EventArgs e)
        {
            WindowState = FormWindowState.Maximized;
            Properties.Settings.Default.ForemanVersion = VersionUpdater.CurrentVersion;

            if (!Enum.IsDefined(typeof(ProductionGraph.RateUnit), Properties.Settings.Default.DefaultRateUnit))
                Properties.Settings.Default.DefaultRateUnit = (int)ProductionGraph.RateUnit.Per1Sec;
            if (!Enum.IsDefined(typeof(ModuleSelector.Style), Properties.Settings.Default.DefaultModuleOption))
                Properties.Settings.Default.DefaultModuleOption = (int)ModuleSelector.Style.None;
            if (!Enum.IsDefined(typeof(AssemblerSelector.Style), Properties.Settings.Default.DefaultAssemblerOption))
                Properties.Settings.Default.DefaultAssemblerOption = (int)AssemblerSelector.Style.WorstNonBurner;
            if (!Enum.IsDefined(typeof(ProductionGraphViewer.LOD), Properties.Settings.Default.LevelOfDetail))
                Properties.Settings.Default.LevelOfDetail = (int)ProductionGraphViewer.LOD.Medium;
            if (!Enum.IsDefined(typeof(NodeDirection), Properties.Settings.Default.DefaultNodeDirection))
                Properties.Settings.Default.DefaultNodeDirection = (int)NodeDirection.Up;
            if (Properties.Settings.Default.IconsSize < 8)   Properties.Settings.Default.IconsSize = 8;
            if (Properties.Settings.Default.IconsSize > 256) Properties.Settings.Default.IconsSize = 256;

            // Wire up tab control events before adding first tab
            GraphTabControl.AddTabRequested      += GraphTabControl_AddTabRequested;
            GraphTabControl.CloseTabRequested    += GraphTabControl_CloseTabRequested;
            GraphTabControl.SelectedIndexChanged += GraphTabControl_SelectedIndexChanged;

            // Populate rate dropdown before any tab is created (SyncToolbarToActiveTab reads it)
            RateOptionsDropDown.Items.AddRange(ProductionGraph.RateUnitNames);

            // Create the first tab
            _newTabCounter = 1;
            var firstViewer = new ProductionGraphViewer();
            firstViewer.KeyDown += GraphViewer_KeyDown;

            List<Preset> validPresets = GetValidPresetsList();
            if (validPresets != null && validPresets.Count > 0)
            {
                Properties.Settings.Default.CurrentPresetName = validPresets[0].Name;
                firstViewer.LoadPreset(validPresets[0]);
            }
            ApplyViewerSettings(firstViewer);

            var firstState = new GraphTabState
            {
                MinorGridlinesIndex = Properties.Settings.Default.MinorGridlines,
                MajorGridlinesIndex = Properties.Settings.Default.MajorGridlines,
                ShowGridlines       = Properties.Settings.Default.AltGridlines,
            };
            tabStates.Add(firstState);
            GraphTabControl.AddGraphTab(firstViewer, "New Graph 1");

            Properties.Settings.Default.Save();

            RestoreSavedTabs();

            ActiveViewer?.Invalidate();
            ActiveViewer?.Focus();
        }

        //---------------------------------------------------------Tab session restore

        private async void RestoreSavedTabs()
        {
            List<string> paths = null;
            string json = Properties.Settings.Default.LastOpenTabs;
            if (!string.IsNullOrEmpty(json))
            {
                try { paths = JsonConvert.DeserializeObject<List<string>>(json); }
                catch { }
            }

            // Fall back to the legacy single-file setting if no tab list is stored yet
            if (paths == null || paths.Count == 0)
            {
                string lastFile = Properties.Settings.Default.LastOpenFile;
                if (!string.IsNullOrEmpty(lastFile) && File.Exists(lastFile))
                    await LoadGraphAsync(0, lastFile);
                return;
            }

            bool firstSlotUsed = false;
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

                if (!firstSlotUsed)
                {
                    // Load into the tab that was already created during startup
                    await LoadGraphAsync(0, path);
                    firstSlotUsed = true;
                }
                else
                {
                    // Create a new tab then load into it
                    CreateNewTab();
                    await LoadGraphAsync(GraphTabControl.SelectedIndex, path);
                }
            }

            SyncToolbarToActiveTab();
            ActiveViewer?.Invalidate();
            ActiveViewer?.Focus();
        }

        private async Task LoadGraphAsync(int tabIndex, string path)
        {
            var state  = tabStates[tabIndex];
            var viewer = GraphTabControl.GetViewer(tabIndex);
            if (viewer == null) return;
            try
            {
                await viewer.LoadFromJson(JObject.Parse(File.ReadAllText(path)), false, true);
                state.SaveFilePath = path;
                UpdateTabTitle(tabIndex);
                Properties.Settings.Default.LastSaveFileLocation = Path.GetDirectoryName(path);
            }
            catch (Exception ex)
            {
                ErrorLogging.LogLine($"Error restoring tab from '{path}': {ex.Message}");
            }
        }

        //---------------------------------------------------------ApplyViewerSettings

        private void ApplyViewerSettings(ProductionGraphViewer v)
        {
            v.Graph.SelectedRateUnit                        = (ProductionGraph.RateUnit)Properties.Settings.Default.DefaultRateUnit;
            v.Graph.ModuleSelector.DefaultSelectionStyle    = (ModuleSelector.Style)Properties.Settings.Default.DefaultModuleOption;
            v.Graph.AssemblerSelector.DefaultSelectionStyle = (AssemblerSelector.Style)Properties.Settings.Default.DefaultAssemblerOption;
            v.Graph.DefaultNodeDirection                    = (NodeDirection)Properties.Settings.Default.DefaultNodeDirection;
            v.Graph.EnableExtraProductivityForNonMiners     = Properties.Settings.Default.EnableExtraProductivityForNonMiners;
            v.Graph.DefaultToSimplePassthroughNodes         = Properties.Settings.Default.SimplePassthroughNodes;
            v.Graph.LowPriorityPower                       = 2f;
            v.Graph.PullOutputNodes                        = false;
            v.Graph.PullOutputNodesPower                   = 1f;
            v.ArrowsOnLinks                                = Properties.Settings.Default.ArrowsOnLinks;
            v.DynamicLinkWidth                             = Properties.Settings.Default.DynamicLineWidth;
            v.ShowRecipeToolTip                            = Properties.Settings.Default.ShowRecipeToolTip;
            v.LockedRecipeEditPanelPosition                = Properties.Settings.Default.LockedRecipeEditorPosition;
            v.LevelOfDetail                                = (ProductionGraphViewer.LOD)Properties.Settings.Default.LevelOfDetail;
            v.NodeCountForSimpleView                       = Properties.Settings.Default.NodeCountForSimpleView;
            v.FlagOUSuppliedNodes                          = Properties.Settings.Default.FlagOUSuppliedNodes;
            v.IconsOnly                                    = Properties.Settings.Default.IconsOnlyView;
            v.IconsSize                                    = Properties.Settings.Default.IconsSize;
            v.SmartNodeDirection                           = Properties.Settings.Default.SmartNodeDirection;
            v.ArrowRenderer.ShowErrorArrows                = Properties.Settings.Default.ShowErrorArrows;
            v.ArrowRenderer.ShowWarningArrows              = Properties.Settings.Default.ShowWarningArrows;
            v.ArrowRenderer.ShowDisconnectedArrows         = Properties.Settings.Default.ShowDisconnectedArrows;
            v.ArrowRenderer.ShowOUNodeArrows               = Properties.Settings.Default.ShowOUSuppliedArrows;
        }

        //---------------------------------------------------------Tab management

        private void CreateNewTab(string title = null)
        {
            _newTabCounter++;
            string tabTitle = title ?? $"New Graph {_newTabCounter}";
            var viewer = new ProductionGraphViewer();
            viewer.KeyDown += GraphViewer_KeyDown;

            var srcViewer = ActiveViewer;
            if (srcViewer?.DCache != null)
            {
                viewer.ShareDCacheFrom(srcViewer);
                viewer.SavedPresetNames = new List<string>(srcViewer.SavedPresetNames);
            }
            ApplyViewerSettings(viewer);

            if (Properties.Settings.Default.FlagDarkMode)
                ChangeTheme(Color.FromArgb(23, 23, 23), Color.FromArgb(124, 124, 124), viewer);

            var state = new GraphTabState
            {
                MinorGridlinesIndex = Properties.Settings.Default.MinorGridlines,
                MajorGridlinesIndex = Properties.Settings.Default.MajorGridlines,
                ShowGridlines       = Properties.Settings.Default.AltGridlines,
            };
            tabStates.Add(state);
            GraphTabControl.AddGraphTab(viewer, tabTitle);
        }

        private void CloseTab(int index)
        {
            if (GraphTabControl.RealTabCount <= 1)
            {
                MessageBox.Show("At least one tab must remain open.", "Cannot close tab",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!TestTabSavedStatus(index)) return;
            tabStates[index].SummaryForm?.Close();
            tabStates.RemoveAt(index);
            GraphTabControl.RemoveGraphTab(index);
            UpdateTitleBar();
        }

        private void SyncToolbarToActiveTab()
        {
            var v     = ActiveViewer;
            var state = ActiveTabState;
            if (v == null || state == null) return;

            _suppressToolbarEvents = true;
            try
            {
                RateOptionsDropDown.SelectedIndex    = (int)v.Graph.SelectedRateUnit;
                PauseUpdatesCheckbox.Checked         = v.Graph.PauseUpdates;
                IconViewCheckBox.Checked             = v.IconsOnly;
                GridlinesCheckbox.Checked            = state.ShowGridlines;
                MinorGridlinesDropDown.SelectedIndex = state.MinorGridlinesIndex;
                MajorGridlinesDropDown.SelectedIndex = state.MajorGridlinesIndex;
            }
            finally { _suppressToolbarEvents = false; }

            ApplyGridlinesToActiveViewer();
            UpdateTitleBar();
        }

        private void ApplyGridlinesToActiveViewer()
        {
            var v = ActiveViewer;
            if (v == null) return;
            int minor = 0, major = 0;
            if (MinorGridlinesDropDown.SelectedIndex > 0)
                minor = 6 * (int)Math.Pow(2, MinorGridlinesDropDown.SelectedIndex - 1);
            if (MajorGridlinesDropDown.SelectedIndex > 0)
                major = 6 * (int)Math.Pow(2, MajorGridlinesDropDown.SelectedIndex - 1);
            v.Grid.CurrentGridUnit      = minor;
            v.Grid.CurrentMajorGridUnit = major;
            v.Grid.ShowGrid             = GridlinesCheckbox.Checked;
            v.Invalidate();
        }

        private void UpdateTitleBar()
        {
            var state = ActiveTabState;
            string path = state?.SaveFilePath ?? "Untitled";
            this.Text = $"{DefaultAppName} ({Properties.Settings.Default.CurrentPresetName}) - {path}";
        }

        private void UpdateTabTitle(int tabIndex)
        {
            if (tabIndex < 0 || tabIndex >= tabStates.Count) return;
            string path = tabStates[tabIndex].SaveFilePath;
            string label = path != null
                ? Path.GetFileNameWithoutExtension(path)
                : $"New Graph {tabIndex + 1}";
            GraphTabControl.SetTabTitle(tabIndex, label);
        }

        private void GraphTabControl_AddTabRequested(object sender, EventArgs e) => CreateNewTab();

        private void GraphTabControl_CloseTabRequested(object sender, int tabIndex) => CloseTab(tabIndex);

        private void GraphTabControl_SelectedIndexChanged(object sender, EventArgs e)
        {
            SyncToolbarToActiveTab();
            ActiveViewer?.Focus();
        }

        //---------------------------------------------------------Save/Load/New/Exit

        private void SaveButton_Click(object sender, EventArgs e)
        {
            var state = ActiveTabState;
            if (state == null) return;
            if (state.SaveFilePath == null || !SaveGraph(GraphTabControl.SelectedIndex, state.SaveFilePath))
                SaveGraphAs();
        }

		private void SaveAsGraphButton_Click(object sender, EventArgs e)
		{
			SaveGraphAs();
		}

		private void LoadGraphButton_Click(object sender, EventArgs e)
		{
			LoadGraph();
		}

		private void ImportGraphButton_Click(object sender, EventArgs e)
		{
			ImportGraph();
		}

		private void NewGraphButton_Click(object sender, EventArgs e)
		{
			NewGraph();
		}

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            for (int i = 0; i < GraphTabControl.RealTabCount; i++)
            {
                if (!TestTabSavedStatus(i))
                {
                    e.Cancel = true;
                    return;
                }
            }
            // Persist the file path of every saved tab so they reopen on next launch
            var openPaths = tabStates
                .Select(s => s.SaveFilePath ?? "")
                .ToList();
            Properties.Settings.Default.LastOpenTabs = JsonConvert.SerializeObject(openPaths);
            Properties.Settings.Default.LastOpenFile = ActiveTabState?.SaveFilePath ?? "";
            Properties.Settings.Default.Save();
        }

        private void SaveGraphAs() => SaveGraphAs(GraphTabControl.SelectedIndex);
        private void SaveGraphAs(int tabIndex)
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.DefaultExt    = ".fjson";
                dialog.Filter        = "Foreman files (*.fjson)|*.fjson|All files|*.*";
                string lastSaveDir   = Properties.Settings.Default.LastSaveFileLocation;
                dialog.InitialDirectory = (!string.IsNullOrEmpty(lastSaveDir) && Directory.Exists(lastSaveDir))
                    ? lastSaveDir
                    : Path.Combine(Application.StartupPath, "Saved Graphs");
                if (!Directory.Exists(Path.Combine(Application.StartupPath, "Saved Graphs")))
                    Directory.CreateDirectory(Path.Combine(Application.StartupPath, "Saved Graphs"));
                dialog.AddExtension    = true;
                dialog.OverwritePrompt = true;
                dialog.FileName        = "Flowchart.fjson";
                if (dialog.ShowDialog() != DialogResult.OK) return;
                if (SaveGraph(tabIndex, dialog.FileName))
                {
                    Properties.Settings.Default.LastSaveFileLocation = Path.GetDirectoryName(dialog.FileName);
                    Properties.Settings.Default.Save();
                }
            }
        }

        private string GetGraphJson(ProductionGraphViewer viewer)
        {
            var sb     = new StringBuilder();
            var writer = new JsonTextWriter(new StringWriter(sb));
            var ser    = JsonSerializer.Create();
            ser.Formatting = Formatting.Indented;
            viewer.Graph.SerializeNodeIdSet = null;
            ser.Serialize(writer, viewer);
            writer.Close();
            return sb.ToString();
        }
        private bool SaveGraph(int tabIndex, string path)
        {
            var state  = tabStates[tabIndex];
            var viewer = GraphTabControl.GetViewer(tabIndex);
            if (viewer == null) return false;
            try
            {
                string json = GetGraphJson(viewer);
                File.WriteAllText(path, json);
                state.SaveFilePath = path;
                viewer.MarkClean();
                UpdateTabTitle(tabIndex);
                if (tabIndex == GraphTabControl.SelectedIndex)
                    UpdateTitleBar();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save this file. See log for more details");
                ErrorLogging.LogLine($"Error saving file '{path}'. Error: '{ex.Message}'");
                ErrorLogging.LogLine($"Full error output: {ex}");
                return false;
            }
        }

        private void LoadGraph()
        {
            if (!TestGraphSavedStatus()) return;
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "Foreman files (*.fjson)|*.fjson|Old Foreman files (*.json)|*.json";
                if (!Directory.Exists(Path.Combine(Application.StartupPath, "Saved Graphs")))
                    Directory.CreateDirectory(Path.Combine(Application.StartupPath, "Saved Graphs"));
                string lastLoadDir = Properties.Settings.Default.LastSaveFileLocation;
                dialog.InitialDirectory = (!string.IsNullOrEmpty(lastLoadDir) && Directory.Exists(lastLoadDir))
                    ? lastLoadDir
                    : Path.Combine(Application.StartupPath, "Saved Graphs");
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog() != DialogResult.OK) return;
                LoadGraph(dialog.FileName);
            }
        }

        private async void LoadGraph(string path)
        {
            int tabIndex = GraphTabControl.SelectedIndex;
            var state    = ActiveTabState;
            try
            {
                await ActiveViewer.LoadFromJson(JObject.Parse(File.ReadAllText(path)), false, true);
                state.SaveFilePath = path;
                UpdateTabTitle(tabIndex);
                Properties.Settings.Default.LastSaveFileLocation = Path.GetDirectoryName(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not load this file. See log for more details");
                ErrorLogging.LogLine($"Error loading file '{path}'. Error: '{ex.Message}'");
                ErrorLogging.LogLine($"Full error output: {ex}");
            }

            var v = ActiveViewer;
            Properties.Settings.Default.EnableExtraProductivityForNonMiners = v.Graph.EnableExtraProductivityForNonMiners;
            Properties.Settings.Default.DefaultRateUnit        = (int)v.Graph.SelectedRateUnit;
            Properties.Settings.Default.DefaultAssemblerOption = (int)v.Graph.AssemblerSelector.DefaultSelectionStyle;
            Properties.Settings.Default.DefaultModuleOption    = (int)v.Graph.ModuleSelector.DefaultSelectionStyle;
            Properties.Settings.Default.DefaultNodeDirection   = (int)v.Graph.DefaultNodeDirection;
            Properties.Settings.Default.Save();
            SyncToolbarToActiveTab();
            v.Invalidate();
            UpdateTitleBar();
        }

        private void NewGraph()
        {
            if (!TestGraphSavedStatus()) return;
            var v     = ActiveViewer;
            var state = ActiveTabState;
            if (v == null || state == null) return;

            v.ClearGraph();
            v.SavedPresetNames.Clear();

            List<Preset> validPresets = GetValidPresetsList();
            if (validPresets != null && validPresets.Count > 0)
            {
                Properties.Settings.Default.CurrentPresetName = validPresets[0].Name;
                v.LoadPreset(validPresets[0]);
            }
            else
            {
                Properties.Settings.Default.CurrentPresetName = "No Preset!";
            }
            ApplyViewerSettings(v);
            state.SaveFilePath = null;
            UpdateTabTitle(GraphTabControl.SelectedIndex);
            Properties.Settings.Default.Save();
            UpdateTitleBar();
        }

		private void ImportGraph()
		{
			OpenFileDialog dialog = new OpenFileDialog();
			dialog.Filter = "Foreman files (*.fjson)|*.fjson|Old Foreman files (*.json)|*.json";
			if (!Directory.Exists(Path.Combine(Application.StartupPath, "Saved Graphs")))
				Directory.CreateDirectory(Path.Combine(Application.StartupPath, "Saved Graphs"));
            string lastLoadDir = Properties.Settings.Default.LastSaveFileLocation;
            dialog.InitialDirectory = (!string.IsNullOrEmpty(lastLoadDir) && Directory.Exists(lastLoadDir))
                ? lastLoadDir
                : Path.Combine(Application.StartupPath, "Saved Graphs");
            dialog.CheckFileExists = true;
			if (dialog.ShowDialog() != DialogResult.OK)
				return;

			ImportGraph(dialog.FileName);
		}

        private void ImportGraph(string path)
        {
            var v = ActiveViewer;
            if (v == null) return;
            try
            {
                v.ImportNodesFromJson(
                    (JObject)JObject.Parse(File.ReadAllText(path))["ProductionGraph"],
                    v.ScreenToGraph(new Point(v.Width / 2, v.Height / 2)), true);
                Properties.Settings.Default.LastSaveFileLocation = Path.GetDirectoryName(path);
                Properties.Settings.Default.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not import from this file. See log for more details");
                ErrorLogging.LogLine($"Error importing from file '{path}'. Error: '{ex.Message}'");
                ErrorLogging.LogLine($"Full error output: {ex}");
            }
        }

        private bool TestTabSavedStatus(int tabIndex)
        {
            var state  = tabStates[tabIndex];
            var viewer = GraphTabControl.GetViewer(tabIndex);
            if (viewer == null) return true;
            string tabLabel = GraphTabControl.TabPages[tabIndex].Text;

            if (state.SaveFilePath == null)
            {
                if (viewer.Graph.Nodes.Any())
                    return MessageBox.Show(
                        $"Tab \"{tabLabel}\" hasn't been saved!\nIf you continue you will lose it forever!",
                        "Are you sure?", MessageBoxButtons.OKCancel) == DialogResult.OK;
                return true;
            }

            if (!File.Exists(state.SaveFilePath))
                return MessageBox.Show(
                    $"Tab \"{tabLabel}\" save file has been deleted!\nIf you continue you will lose it forever!",
                    "Are you sure?", MessageBoxButtons.OKCancel) == DialogResult.OK;

            if (viewer.IsDirty)
            {
                DialogResult r = MessageBox.Show(
                    $"Tab \"{tabLabel}\" has been modified!\nDo you wish to save before continuing?",
                    "Are you sure?", MessageBoxButtons.YesNoCancel);
                if (r == DialogResult.Cancel) return false;
                if (r == DialogResult.Yes)
                {
                    if (state.SaveFilePath != null)
                        SaveGraph(tabIndex, state.SaveFilePath);
                    else
                        SaveGraphAs(tabIndex);
                }
            }
            return true;
        }

        private bool TestGraphSavedStatus() =>
            TestTabSavedStatus(GraphTabControl.SelectedIndex);

        //---------------------------------------------------------Settings/export/additem/addrecipe

        public static List<Preset> GetValidPresetsList()
		{
			List<Preset> presets = new List<Preset>();
			List<string> existingPresetFiles = new List<string>();
			foreach (string presetFile in Directory.GetFiles(Path.Combine(Application.StartupPath, "Presets"), "*.pjson"))
				if (File.Exists(Path.ChangeExtension(presetFile, "dat")))
					existingPresetFiles.Add(Path.GetFileNameWithoutExtension(presetFile));
			existingPresetFiles.Sort();

			if (!existingPresetFiles.Contains(Properties.Settings.Default.CurrentPresetName))
			{
				MessageBox.Show("The current preset (" + Properties.Settings.Default.CurrentPresetName + ") has been removed. Switching to the default preset (Factorio 2.0 Vanilla)");
				Properties.Settings.Default.CurrentPresetName = DefaultPreset;
			}
			if (!existingPresetFiles.Contains(DefaultPreset))
			{
				MessageBox.Show("The default preset (Factorio 2.0 Vanilla) has been removed. Please re-install / re-download Foreman");
				Application.Exit();
				return null;
			}
			existingPresetFiles.Remove(Properties.Settings.Default.CurrentPresetName);
			existingPresetFiles.Remove(DefaultPreset);

			presets.Add(new Preset(Properties.Settings.Default.CurrentPresetName, true, Properties.Settings.Default.CurrentPresetName == DefaultPreset));
			if (Properties.Settings.Default.CurrentPresetName != DefaultPreset)
				presets.Add(new Preset(DefaultPreset, false, true));
			foreach (string presetName in existingPresetFiles)
				presets.Add(new Preset(presetName, false, false));

			Properties.Settings.Default.Save();
			return presets;
		}

        private async void SettingsButton_Click(object sender, EventArgs e)
        {
            var activeViewer = ActiveViewer;
            if (activeViewer == null) return;

            SettingsForm.SettingsFormOptions options = new SettingsForm.SettingsFormOptions(activeViewer.DCache);

            options.Presets        = GetValidPresetsList();
            options.SelectedPreset = options.Presets[0];

            options.QualitySteps           = activeViewer.Graph.MaxQualitySteps;
            options.LevelOfDetail          = activeViewer.LevelOfDetail;
            options.NodeCountForSimpleView = activeViewer.NodeCountForSimpleView;
            options.IconsOnlyIconSize      = activeViewer.IconsSize;

            options.ArrowsOnLinks               = activeViewer.ArrowsOnLinks;
            options.SimplePassthroughNodes      = activeViewer.Graph.DefaultToSimplePassthroughNodes;
            options.DynamicLinkWidth            = activeViewer.DynamicLinkWidth;
            options.ShowRecipeToolTip           = activeViewer.ShowRecipeToolTip;
            options.LockedRecipeEditPanelPosition = activeViewer.LockedRecipeEditPanelPosition;
            options.FlagOUSuppliedNodes         = activeViewer.FlagOUSuppliedNodes;
            options.FlagDarkMode               = Properties.Settings.Default.FlagDarkMode;

            options.DefaultAssemblerStyle = activeViewer.Graph.AssemblerSelector.DefaultSelectionStyle;
            options.DefaultModuleStyle    = activeViewer.Graph.ModuleSelector.DefaultSelectionStyle;
            options.DefaultNodeDirection  = activeViewer.Graph.DefaultNodeDirection;
            options.SmartNodeDirection    = activeViewer.SmartNodeDirection;

            options.ShowErrorArrows       = activeViewer.ArrowRenderer.ShowErrorArrows;
            options.ShowWarningArrows     = activeViewer.ArrowRenderer.ShowWarningArrows;
            options.ShowDisconnectedArrows = activeViewer.ArrowRenderer.ShowDisconnectedArrows;
            options.ShowOUSuppliedArrows  = activeViewer.ArrowRenderer.ShowOUNodeArrows;

            options.RoundAssemblerCount = Properties.Settings.Default.RoundAssemblerCount;
            options.AbbreviateSciPacks  = Properties.Settings.Default.AbbreviateSciPacks;

            options.EnableExtraProductivityForNonMiners = activeViewer.Graph.EnableExtraProductivityForNonMiners;
            options.DEV_ShowUnavailableItems            = Properties.Settings.Default.ShowUnavailable;
            options.DEV_UseRecipeBWFilters              = Properties.Settings.Default.UseRecipeBWfilters;

            options.Solver_LowPriorityPower        = activeViewer.Graph.LowPriorityPower;
            options.Solver_PullConsumerNodes        = activeViewer.Graph.PullOutputNodes;
            options.Solver_PullConsumerNodesPower   = activeViewer.Graph.PullOutputNodesPower;

            if (activeViewer.DCache != null)
            {
                options.EnabledObjects.UnionWith(activeViewer.DCache.Recipes.Values.Where(r => r.Enabled));
                options.EnabledObjects.UnionWith(activeViewer.DCache.Assemblers.Values.Where(r => r.Enabled));
                options.EnabledObjects.UnionWith(activeViewer.DCache.Beacons.Values.Where(r => r.Enabled));
                options.EnabledObjects.UnionWith(activeViewer.DCache.Modules.Values.Where(r => r.Enabled));
                options.EnabledObjects.UnionWith(activeViewer.DCache.Qualities.Values.Where(r => r.Enabled));
            }
            options.FilePresetNames = activeViewer.SavedPresetNames.Count > 0
                ? new List<string>(activeViewer.SavedPresetNames)
                : null;

            using (SettingsForm form = new SettingsForm(options, this))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Left = this.Left + 50;
                form.Top  = this.Top  + 50;
                if (form.ShowDialog() == DialogResult.OK)
                {
                    if (options.SelectedPreset != options.Presets[0] || options.DEV_UseRecipeBWFilters != Properties.Settings.Default.UseRecipeBWfilters || options.RequireReload)
                    {
                        Properties.Settings.Default.CurrentPresetName  = form.Options.SelectedPreset.Name;
                        Properties.Settings.Default.UseRecipeBWfilters = true;

                        if (activeViewer.DCache == null)
                        {
                            activeViewer.LoadPreset(form.Options.SelectedPreset);
                            UpdateTitleBar();
                        }
                        else
                        {
                            // Reload every tab with the new preset
                            for (int _ti = 0; _ti < GraphTabControl.RealTabCount; _ti++)
                            {
                                var _tv = GraphTabControl.GetViewer(_ti);
                                if (_tv == null) continue;
                                string _snapshot = JsonConvert.SerializeObject(_tv);
                                await _tv.LoadFromJson(JObject.Parse(_snapshot), true, false);
                            }
                            SyncToolbarToActiveTab();
                            UpdateTitleBar();
                        }
                    }
                    else
                    {
                        if (activeViewer.DCache != null)
                        {
                            foreach (Recipe recipe in activeViewer.DCache.Recipes.Values)
                                recipe.Enabled = options.EnabledObjects.Contains(recipe);
                            foreach (Assembler assembler in activeViewer.DCache.Assemblers.Values)
                                assembler.Enabled = options.EnabledObjects.Contains(assembler);
                            foreach (Beacon beacon in activeViewer.DCache.Beacons.Values)
                                beacon.Enabled = options.EnabledObjects.Contains(beacon);
                            foreach (Module module in activeViewer.DCache.Modules.Values)
                                module.Enabled = options.EnabledObjects.Contains(module);
                            foreach (Quality quality in activeViewer.DCache.Qualities.Values)
                                quality.Enabled = options.EnabledObjects.Contains(quality);
                            activeViewer.DCache.DefaultQuality.Enabled = true;
                            activeViewer.DCache.RocketAssembler.Enabled = activeViewer.DCache.Assemblers["rocket-silo"]?.Enabled ?? false;
                        }
                    }

                    // Apply viewer settings to ALL tabs
                    Properties.Settings.Default.LevelOfDetail             = (int)options.LevelOfDetail;
                    Properties.Settings.Default.NodeCountForSimpleView     = options.NodeCountForSimpleView;
                    Properties.Settings.Default.IconsSize                  = options.IconsOnlyIconSize;
                    Properties.Settings.Default.ArrowsOnLinks              = options.ArrowsOnLinks;
                    Properties.Settings.Default.SimplePassthroughNodes     = options.SimplePassthroughNodes;
                    Properties.Settings.Default.DynamicLineWidth           = options.DynamicLinkWidth;
                    Properties.Settings.Default.ShowRecipeToolTip          = options.ShowRecipeToolTip;
                    Properties.Settings.Default.LockedRecipeEditorPosition = options.LockedRecipeEditPanelPosition;
                    Properties.Settings.Default.FlagOUSuppliedNodes        = options.FlagOUSuppliedNodes;
                    Properties.Settings.Default.FlagDarkMode               = options.FlagDarkMode;
                    Properties.Settings.Default.DefaultAssemblerOption     = (int)options.DefaultAssemblerStyle;
                    Properties.Settings.Default.DefaultModuleOption        = (int)options.DefaultModuleStyle;
                    Properties.Settings.Default.DefaultNodeDirection       = (int)options.DefaultNodeDirection;
                    Properties.Settings.Default.SmartNodeDirection         = options.SmartNodeDirection;
                    Properties.Settings.Default.ShowErrorArrows            = options.ShowErrorArrows;
                    Properties.Settings.Default.ShowWarningArrows          = options.ShowWarningArrows;
                    Properties.Settings.Default.ShowDisconnectedArrows     = options.ShowDisconnectedArrows;
                    Properties.Settings.Default.ShowOUSuppliedArrows       = options.ShowOUSuppliedArrows;
                    Properties.Settings.Default.RoundAssemblerCount        = options.RoundAssemblerCount;
                    Properties.Settings.Default.AbbreviateSciPacks         = options.AbbreviateSciPacks;
                    Properties.Settings.Default.EnableExtraProductivityForNonMiners = options.EnableExtraProductivityForNonMiners;
                    Properties.Settings.Default.ShowUnavailable            = options.DEV_ShowUnavailableItems;
                    Properties.Settings.Default.Save();

                    for (int _ti = 0; _ti < GraphTabControl.RealTabCount; _ti++)
                    {
                        var _tv = GraphTabControl.GetViewer(_ti);
                        if (_tv == null) continue;
                        _tv.LevelOfDetail          = options.LevelOfDetail;
                        _tv.NodeCountForSimpleView  = options.NodeCountForSimpleView;
                        _tv.IconsSize               = options.IconsOnlyIconSize;
                        _tv.ArrowsOnLinks           = options.ArrowsOnLinks;
                        _tv.Graph.DefaultToSimplePassthroughNodes = options.SimplePassthroughNodes;
                        _tv.DynamicLinkWidth        = options.DynamicLinkWidth;
                        _tv.ShowRecipeToolTip       = options.ShowRecipeToolTip;
                        _tv.LockedRecipeEditPanelPosition = options.LockedRecipeEditPanelPosition;
                        _tv.FlagOUSuppliedNodes     = options.FlagOUSuppliedNodes;
                        _tv.Graph.AssemblerSelector.DefaultSelectionStyle = options.DefaultAssemblerStyle;
                        _tv.Graph.ModuleSelector.DefaultSelectionStyle    = options.DefaultModuleStyle;
                        _tv.Graph.DefaultNodeDirection = options.DefaultNodeDirection;
                        _tv.SmartNodeDirection      = options.SmartNodeDirection;
                        _tv.ArrowRenderer.ShowErrorArrows        = options.ShowErrorArrows;
                        _tv.ArrowRenderer.ShowWarningArrows      = options.ShowWarningArrows;
                        _tv.ArrowRenderer.ShowDisconnectedArrows = options.ShowDisconnectedArrows;
                        _tv.ArrowRenderer.ShowOUNodeArrows       = options.ShowOUSuppliedArrows;
                        _tv.Graph.EnableExtraProductivityForNonMiners = options.EnableExtraProductivityForNonMiners;
                        _tv.Graph.LowPriorityPower      = options.Solver_LowPriorityPower;
                        _tv.Graph.PullOutputNodesPower  = options.Solver_PullConsumerNodesPower;
                        _tv.Graph.PullOutputNodes       = options.Solver_PullConsumerNodes;
                        _tv.Graph.MaxQualitySteps       = options.QualitySteps;
                        _tv.Graph.UpdateNodeMaxQualities();
                        _tv.Graph.UpdateNodeStates(true);
                        _tv.Graph.UpdateNodeValues();
                        if (tabStates[_ti].SaveFilePath != null)
                            _tv.MarkDirty();
                    }

                    if (options.RequireReload)
                        SettingsButton_Click(this, EventArgs.Empty);

                    if (options.FilePresetNames != null)
                        activeViewer.SavedPresetNames = new List<string>(options.FilePresetNames);
                }
            }
        }

        private void ExportImageButton_Click(object sender, EventArgs e)
        {
            var v = ActiveViewer;
            if (v == null) return;
            ImageExportForm form = new ImageExportForm(v);
            form.StartPosition = FormStartPosition.Manual;
            form.Left = this.Left + 50;
            form.Top  = this.Top  + 50;
            form.ShowDialog();
        }

        private void AddRecipeButton_Click(object sender, EventArgs e)
        {
            var v = ActiveViewer;
            if (v == null) return;
            Point location = v.ScreenToGraph(new Point(v.Width / 2, v.Height / 2));
            v.AddNewNode(new Point(15, 15), new ItemQualityPair("adding disconnected recipe node"), location, NewNodeType.Disconnected);
        }

        private void AddShapeButton_Click(object sender, EventArgs e)
        {
            var v = ActiveViewer;
            if (v == null) return;
            Point location = v.ScreenToGraph(new Point(v.Width / 2, v.Height / 2));
            v.AddShapeAnnotation(location);
        }

        private void AddTextButton_Click(object sender, EventArgs e)
        {
            var v = ActiveViewer;
            if (v == null) return;
            Point location = v.ScreenToGraph(new Point(v.Width / 2, v.Height / 2));
            v.AddTextAnnotation(location);
        }

        private void AddItemButton_Click(object sender, EventArgs e)
        {
            var v = ActiveViewer;
            if (v == null) return;
            Point location = v.ScreenToGraph(new Point(v.Width / 2, v.Height / 2));
            v.AddItem(new Point(15, 15), location);
        }

		private void HelpButton_Click(object sender, EventArgs e)
		{
			System.Diagnostics.Process.Start("https://github.com/DanielKote/Foreman2");
		}

		//---------------------------------------------------------Key & Mouse events

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.S && (Control.ModifierKeys & Keys.Control) == Keys.Control)
            {
                var state = ActiveTabState;
                if (state == null) return;
                if (state.SaveFilePath == null || !SaveGraph(GraphTabControl.SelectedIndex, state.SaveFilePath))
                    SaveGraphAs();
            }
        }

		//---------------------------------------------------------Production Graph properties

        private void RateOptionsDropDown_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_suppressToolbarEvents) return;
            var v = ActiveViewer;
            if (v == null) return;
            Properties.Settings.Default.DefaultRateUnit = RateOptionsDropDown.SelectedIndex;
            v.Graph.SelectedRateUnit = (ProductionGraph.RateUnit)RateOptionsDropDown.SelectedIndex;
            v.MarkDirty();
            Properties.Settings.Default.Save();
            v.Graph.UpdateNodeValues();
        }

        private void PauseUpdatesCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            if (_suppressToolbarEvents) return;
            var v = ActiveViewer;
            if (v == null) return;
            v.Graph.PauseUpdates = PauseUpdatesCheckbox.Checked;
            if (!v.Graph.PauseUpdates)
                v.Graph.UpdateNodeValues();
            else
                v.Invalidate();
        }

        private void IconViewCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (_suppressToolbarEvents) return;
            var v = ActiveViewer;
            if (v == null) return;
            v.IconsOnly = IconViewCheckBox.Checked;
            Properties.Settings.Default.IconsOnlyView = IconViewCheckBox.Checked;
            Properties.Settings.Default.Save();
            v.Invalidate();
        }

        private void GraphSummaryButton_Click(object sender, EventArgs e)
        {
            var state  = ActiveTabState;
            var viewer = ActiveViewer;
            if (state == null || viewer == null) return;
            if (state.SummaryForm == null || state.SummaryForm.IsDisposed)
            {
                state.SummaryForm = new GraphSummaryForm(viewer.Graph, viewer);
                state.SummaryForm.StartPosition = FormStartPosition.Manual;
                state.SummaryForm.Left = this.Left + 50;
                state.SummaryForm.Top  = this.Top  + 50;
                state.SummaryForm.FormClosed += (s, args) => state.SummaryForm = null;
                state.SummaryForm.Show(this);
            }
            else
            {
                state.SummaryForm.BringToFront();
            }
        }

		//---------------------------------------------------------Gridlines

        private void MinorGridlinesDropDown_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_suppressToolbarEvents) return;
            var v = ActiveViewer;
            if (v == null) return;
            int updatedGridUnit = 0;
            if (MinorGridlinesDropDown.SelectedIndex > 0)
                updatedGridUnit = 6 * (int)Math.Pow(2, MinorGridlinesDropDown.SelectedIndex - 1);
            if (v.Grid.CurrentGridUnit != updatedGridUnit)
            {
                v.Grid.CurrentGridUnit = updatedGridUnit;
                v.Invalidate();
            }
            Properties.Settings.Default.MinorGridlines = MinorGridlinesDropDown.SelectedIndex;
            Properties.Settings.Default.Save();
            if (ActiveTabState != null) ActiveTabState.MinorGridlinesIndex = MinorGridlinesDropDown.SelectedIndex;
        }

        private void MajorGridlinesDropDown_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_suppressToolbarEvents) return;
            var v = ActiveViewer;
            if (v == null) return;
            int updatedGridUnit = 0;
            if (MajorGridlinesDropDown.SelectedIndex > 0)
                updatedGridUnit = 6 * (int)Math.Pow(2, MajorGridlinesDropDown.SelectedIndex - 1);
            if (v.Grid.CurrentMajorGridUnit != updatedGridUnit)
            {
                v.Grid.CurrentMajorGridUnit = updatedGridUnit;
                v.Invalidate();
            }
            Properties.Settings.Default.MajorGridlines = MajorGridlinesDropDown.SelectedIndex;
            Properties.Settings.Default.Save();
            if (ActiveTabState != null) ActiveTabState.MajorGridlinesIndex = MajorGridlinesDropDown.SelectedIndex;
        }

        private void GridlinesCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            if (_suppressToolbarEvents) return;
            var v = ActiveViewer;
            if (v == null) return;
            if (v.Grid.ShowGrid != GridlinesCheckbox.Checked)
            {
                v.Grid.ShowGrid = GridlinesCheckbox.Checked;
                v.Invalidate();
            }
            Properties.Settings.Default.AltGridlines = GridlinesCheckbox.Checked;
            Properties.Settings.Default.Save();
            if (ActiveTabState != null) ActiveTabState.ShowGridlines = GridlinesCheckbox.Checked;
        }

        private void AlignSelectionButton_Click(object sender, EventArgs e)
        {
            ActiveViewer?.AlignSelected();
        }

        private void GraphViewer_KeyDown(object sender, KeyEventArgs e)
        {
            var v = sender as ProductionGraphViewer ?? ActiveViewer;
            if (v == null) return;
            if (e.KeyCode == Keys.Space)
            {
                v.Grid.ShowGrid = !v.Grid.ShowGrid;
                if (v == ActiveViewer)
                {
                    _suppressToolbarEvents = true;
                    GridlinesCheckbox.Checked = v.Grid.ShowGrid;
                    _suppressToolbarEvents = false;
                    if (ActiveTabState != null)
                        ActiveTabState.ShowGridlines = v.Grid.ShowGrid;
                }
            }
        }

		//---------------------------------------------------------double buffering commands

		public static void SetDoubleBuffered(Control c)
		{
			if (SystemInformation.TerminalServerSession)
				return;
			System.Reflection.PropertyInfo aProp = typeof(Control).GetProperty("DoubleBuffered",
				System.Reflection.BindingFlags.NonPublic |
				System.Reflection.BindingFlags.Instance);
			aProp.SetValue(c, true, null);
		}

		protected override CreateParams CreateParams
		{
			get
			{
				CreateParams cp = base.CreateParams;
				cp.ExStyle |= 0x02000000;
				return cp;
			}
		}
	}

	public class Preset : IEquatable<Preset>
	{
		public string Name { get; set; }
		public bool IsCurrentlySelected { get; set; }
		public bool IsDefaultPreset { get; set; }

		public Preset(string name, bool isCurrentlySelected, bool isDefaultPreset)
		{
			Name = name;
			IsCurrentlySelected = isCurrentlySelected;
			IsDefaultPreset = isDefaultPreset;
		}

		public bool Equals(Preset other)
		{
			return this == other;
		}
	}
}
