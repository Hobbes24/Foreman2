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
            public string Title              = null;   // header text for tabs that have no save file yet
            public string SnapshotFile       = null;   // this tab's snapshot inside the session directory
            public int?   LastSnapshotHash   = null;   // avoids rewriting an unchanged snapshot every autosave tick
        }
        /// <summary>
        /// Session entries that could not be loaded this run. They are carried through every session write
        /// untouched, so the only copy of that work is never overwritten and gets another chance next launch.
        /// </summary>
        private readonly List<SessionTabInfo> unrestoredTabs = new List<SessionTabInfo>();
        private readonly List<GraphTabState> tabStates = new List<GraphTabState>();
        private int _newTabCounter = 0;
        private bool _suppressToolbarEvents = false;

        // ── Session (crash/exit recovery) ──────────────────────────────────────
        private const int SessionAutosaveIntervalMs = 60000;
        private Timer sessionAutosaveTimer = null;
        private bool sessionRestoreInProgress = false;
        /// <summary>The running form - used by the global error handler to autosave before reporting a crash.</summary>
        public static MainForm Instance { get; private set; }

        // Convenience accessors
        internal ProductionGraphViewer ActiveViewer => GraphTabControl.ActiveViewer;
        private GraphTabState ActiveTabState =>
            GraphTabControl.SelectedIndex >= 0 && GraphTabControl.SelectedIndex < tabStates.Count
                ? tabStates[GraphTabControl.SelectedIndex] : null;

        public MainForm()
		{
			InitializeComponent();
			BuildLinkTracingControls();
			this.DoubleBuffered = true;
			DefaultAppName = this.Text;
			SetStyle(ControlStyles.SupportsTransparentBackColor, true);
			if (Properties.Settings.Default.FlagDarkMode) {
				SetDarkMode();
			}
			Instance = this;
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
            firstViewer.DirtyStateChanged += Viewer_DirtyStateChanged;

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
                Title               = "New Graph 1",
            };
            tabStates.Add(firstState);
            GraphTabControl.AddGraphTab(firstViewer, "New Graph 1");

            SafeSettings.Save();

            RestoreSession();

            ActiveViewer?.Invalidate();
            ActiveViewer?.Focus();
        }

        //---------------------------------------------------------Tab session restore

        /// <summary>
        /// Restores every tab that was open when the program last shut down (or crashed), preferring the
        /// locally stored snapshot for tabs with unsaved changes so nothing is lost even when the original
        /// save location is unreachable.
        /// </summary>
        private async void RestoreSession()
        {
            sessionRestoreInProgress = true;
            try
            {
                SessionState session = SessionManager.Load();
                if (session != null && session.Tabs.Count > 0)
                    await RestoreFromSession(session);
                else
                    await RestoreLegacyTabs();
            }
            catch (Exception ex)
            {
                ErrorLogging.LogLine("Error restoring the previous session: " + ex);
            }
            finally
            {
                sessionRestoreInProgress = false;
            }

            SyncToolbarToActiveTab();
            ActiveViewer?.Invalidate();
            ActiveViewer?.Focus();

            StartSessionAutosave();
            SaveSession();
        }

        private async Task RestoreFromSession(SessionState session)
        {
            bool firstSlotUsed = false;
            List<string> unrestored = new List<string>();
            foreach (SessionTabInfo tab in session.Tabs)
            {
                int tabIndex;
                if (!firstSlotUsed)
                {
                    tabIndex = 0;              // reuse the tab created during startup
                    firstSlotUsed = true;
                }
                else
                {
                    CreateNewTab(tab.Title);
                    tabIndex = GraphTabControl.SelectedIndex;
                }
                if (tabIndex < 0 || tabIndex >= tabStates.Count) continue;

                GraphTabState state = tabStates[tabIndex];
                state.SaveFilePath        = string.IsNullOrEmpty(tab.SaveFilePath) ? null : tab.SaveFilePath;
                state.SnapshotFile        = tab.SnapshotFile;
                state.Title               = tab.Title;
                state.MinorGridlinesIndex = tab.MinorGridlinesIndex;
                state.MajorGridlinesIndex = tab.MajorGridlinesIndex;
                state.ShowGridlines       = tab.ShowGridlines;

                string snapshotJson = SessionManager.ReadSnapshot(tab.SnapshotFile);
                bool saveFileReachable = state.SaveFilePath != null && SafeIO.FileExists(state.SaveFilePath);
                bool loaded = false;

                // Unsaved work (or an unreachable save file) -> restore from the local snapshot
                if (tab.IsDirty || state.SaveFilePath == null || !saveFileReachable)
                    loaded = await LoadGraphFromJsonText(tabIndex, snapshotJson, tab.IsDirty || state.SaveFilePath == null);

                if (!loaded && saveFileReachable)
                    loaded = await LoadGraphAsync(tabIndex, state.SaveFilePath);

                if (!loaded && snapshotJson != null)   // save file failed to load - fall back to the snapshot
                    loaded = await LoadGraphFromJsonText(tabIndex, snapshotJson, true);

                if (!loaded)
                {
                    // Nothing could be loaded (unreadable file, missing preset, ...). Keep the original entry
                    // aside so its snapshot is neither overwritten nor pruned, and give this tab a fresh one.
                    unrestoredTabs.Add(tab);
                    unrestored.Add(tab.Title ?? $"Tab {tabIndex + 1}");
                    state.SnapshotFile     = null;
                    state.LastSnapshotHash = null;
                }

                UpdateTabTitle(tabIndex);
            }

            if (session.ActiveTabIndex >= 0 && session.ActiveTabIndex < GraphTabControl.RealTabCount)
                GraphTabControl.SelectedIndex = session.ActiveTabIndex;

            if (unrestored.Count > 0)
                MessageBox.Show(
                    "These tabs could not be restored:\n  " + string.Join("\n  ", unrestored) +
                    "\n\nTheir saved state is kept in:\n" + (SessionManager.SessionDirectory ?? "(session storage unavailable)") +
                    "\n\nSee errorlog.txt for details.",
                    "Session restore incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>Pre-session-file behaviour: reopen the paths stored in the settings file.</summary>
        private async Task RestoreLegacyTabs()
        {
            List<string> paths = null;
            string json = Properties.Settings.Default.LastOpenTabs;
            if (!string.IsNullOrEmpty(json))
            {
                try { paths = JsonConvert.DeserializeObject<List<string>>(json); }
                catch { }
            }

            if (paths == null || paths.Count == 0)
            {
                string lastFile = Properties.Settings.Default.LastOpenFile;
                if (SafeIO.FileExists(lastFile))
                    await LoadGraphAsync(0, lastFile);
                return;
            }

            bool firstSlotUsed = false;
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (!SafeIO.FileExists(path)) continue;

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
        }

        /// <summary>
        /// Content to load for a save file. If a backup sits next to it holding newer (unsaved) work, the
        /// user is offered that instead - the backup is left on disk either way until the graph is saved.
        /// </summary>
        /// <summary>"664 nodes, 1016 links" for a graph json - used to describe recovery options to the user.</summary>
        private string DescribeGraphSize(string graphJson)
        {
            if (string.IsNullOrEmpty(graphJson)) return "unreadable";
            try
            {
                JObject graph = (JObject)JObject.Parse(graphJson)["ProductionGraph"];
                int nodes = (graph?["Nodes"] as JArray)?.Count ?? 0;
                int links = (graph?["NodeLinks"] as JArray)?.Count ?? 0;
                return $"{nodes} nodes, {links} links";
            }
            catch (Exception ex)
            {
                ErrorLogging.LogLine("Could not measure a graph for the recovery prompt: " + ex.Message);
                return "unreadable";
            }
        }

        private string GetJsonToLoad(string path, out bool fromBackup)
        {
            fromBackup = false;
            DateTime? backupTime = GraphBackup.GetTimestamp(path);
            DateTime? fileTime   = SafeIO.GetLastWriteTime(path);

            if (backupTime != null && (fileTime == null || backupTime > fileTime))
            {
                string backupJson = GraphBackup.Read(path);
                if (backupJson != null)
                {
                    //node counts turn this into an informed choice - a backup holding far less than the saved
                    //file is a warning sign, not something to accept blind
                    string backupSize = DescribeGraphSize(backupJson);
                    string fileSize   = DescribeGraphSize(SafeIO.ReadAllText(path));

                    DialogResult r = MessageBox.Show(
                        $"\"{Path.GetFileName(path)}\" has unsaved changes from an earlier session.\n\n" +
                        $"Backup:     {backupTime:g}   ({backupSize})\n" +
                        $"Saved file: {(fileTime != null ? fileTime.Value.ToString("g") : "missing")}   ({fileSize})\n\n" +
                        "Load the backup with those changes?\n" +
                        "(Choosing No opens the saved file; the backup stays on disk until you save.)",
                        "Unsaved changes found", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r == DialogResult.Yes)
                    {
                        fromBackup = true;
                        return backupJson;
                    }
                }
            }
            return SafeIO.ReadAllText(path);
        }

        private async Task<bool> LoadGraphAsync(int tabIndex, string path)
        {
            if (tabIndex < 0 || tabIndex >= tabStates.Count) return false;
            var state  = tabStates[tabIndex];
            var viewer = GraphTabControl.GetViewer(tabIndex);
            if (viewer == null) return false;

            string json = GetJsonToLoad(path, out bool fromBackup);
            if (json == null) return false;
            try
            {
                await viewer.LoadFromJson(JObject.Parse(json), false, true);
                if (fromBackup) viewer.MarkDirty();   // recovered work is not in the save file yet
                else viewer.MarkClean();
                state.SaveFilePath = path;
                UpdateTabTitle(tabIndex);
                try { Properties.Settings.Default.LastSaveFileLocation = Path.GetDirectoryName(path); }
                catch (Exception ex) { ErrorLogging.LogLine($"Could not resolve directory of '{path}': {ex.Message}"); }
                return true;
            }
            catch (Exception ex)
            {
                ErrorLogging.LogLine($"Error restoring tab from '{path}': {ex.Message}");
                return false;
            }
        }

        private async Task<bool> LoadGraphFromJsonText(int tabIndex, string graphJson, bool markDirty)
        {
            if (string.IsNullOrEmpty(graphJson)) return false;
            var viewer = GraphTabControl.GetViewer(tabIndex);
            if (viewer == null) return false;
            try
            {
                await viewer.LoadFromJson(JObject.Parse(graphJson), false, true);
                if (markDirty) viewer.MarkDirty();
                else viewer.MarkClean();
                return true;
            }
            catch (Exception ex)
            {
                ErrorLogging.LogLine($"Error restoring tab {tabIndex + 1} from its session snapshot: {ex.Message}");
                return false;
            }
        }

        //---------------------------------------------------------Session autosave

        private void StartSessionAutosave()
        {
            if (sessionAutosaveTimer != null) return;
            sessionAutosaveTimer = new Timer { Interval = SessionAutosaveIntervalMs };
            sessionAutosaveTimer.Tick += SessionAutosaveTimer_Tick;
            sessionAutosaveTimer.Start();
        }

        private void SessionAutosaveTimer_Tick(object sender, EventArgs e)
        {
            for (int i = 0; i < GraphTabControl.RealTabCount; i++)
                UpdateTabTitle(i);
            SaveSession();
        }

        /// <summary>
        /// Writes every open tab (graph, save path, dirty state, gridline options) to the local session
        /// directory. Never throws: if the session store is unavailable the call is a logged no-op.
        /// </summary>
        private void SaveSession()
        {
            if (sessionRestoreInProgress || !SessionManager.IsAvailable) return;
            try
            {
                SessionState session = new SessionState
                {
                    SavedUtc       = DateTime.UtcNow,
                    ActiveTabIndex = Math.Max(0, Math.Min(GraphTabControl.SelectedIndex, GraphTabControl.RealTabCount - 1)),
                };

                for (int i = 0; i < GraphTabControl.RealTabCount && i < tabStates.Count; i++)
                {
                    var viewer = GraphTabControl.GetViewer(i);
                    var state  = tabStates[i];
                    if (viewer == null) continue;

                    // Two states where persisting would replace good data with nothing: a graph that is mid-load
                    // (cleared, nodes not back yet) and a file-backed tab that is somehow empty. Keep what is
                    // already stored and try again on the next pass.
                    bool midLoad     = viewer.IsLoading;
                    bool emptyBacked = state.SaveFilePath != null && !viewer.Graph.Nodes.Any();
                    if (midLoad || emptyBacked)
                    {
                        if (emptyBacked && !midLoad)
                            ErrorLogging.LogLine($"Tab {i + 1} ('{GetTabBaseTitle(i)}') has a save file but no nodes - keeping the previous snapshot and backup.");
                        session.Tabs.Add(new SessionTabInfo
                        {
                            Title               = GetTabBaseTitle(i),
                            SaveFilePath        = state.SaveFilePath,
                            SnapshotFile        = state.SnapshotFile,
                            IsDirty             = viewer.IsDirty,
                            MinorGridlinesIndex = state.MinorGridlinesIndex,
                            MajorGridlinesIndex = state.MajorGridlinesIndex,
                            ShowGridlines       = state.ShowGridlines,
                        });
                        continue;
                    }

                    string graphJson = null;
                    try { graphJson = GetGraphJson(viewer); }
                    catch (Exception ex) { ErrorLogging.LogLine($"Could not serialize tab {i + 1} for the session: {ex.Message}"); }

                    if (graphJson != null)
                    {
                        int hash = graphJson.GetHashCode();
                        if (state.SnapshotFile == null || state.LastSnapshotHash != hash)
                        {
                            string snapshotFile = SessionManager.WriteSnapshot(state.SnapshotFile, graphJson);
                            if (snapshotFile != null)
                            {
                                state.SnapshotFile     = snapshotFile;
                                state.LastSnapshotHash = hash;
                            }
                        }

                        // Second copy for tabs backed by a file: a .bak right next to the graph itself,
                        // so unsaved work is recoverable even without this machine's session store.
                        if (state.SaveFilePath != null && viewer.IsDirty)
                            GraphBackup.Write(state.SaveFilePath, graphJson);
                    }

                    session.Tabs.Add(new SessionTabInfo
                    {
                        Title               = GetTabBaseTitle(i),
                        SaveFilePath        = state.SaveFilePath,
                        SnapshotFile        = state.SnapshotFile,
                        IsDirty             = viewer.IsDirty,
                        MinorGridlinesIndex = state.MinorGridlinesIndex,
                        MajorGridlinesIndex = state.MajorGridlinesIndex,
                        ShowGridlines       = state.ShowGridlines,
                    });
                }

                //tabs that failed to restore ride along untouched so their snapshots survive to the next launch
                session.Tabs.AddRange(unrestoredTabs);

                SessionManager.Save(session);
                SessionManager.PruneUnusedSnapshots(session.Tabs.Select(t => t.SnapshotFile));
            }
            catch (Exception ex)
            {
                ErrorLogging.LogLine("Could not save the session state: " + ex.Message);
            }
        }

        /// <summary>Called by the global error handler - preserves open tabs before a crash is reported.</summary>
        public void TrySaveSessionAfterError()
        {
            try
            {
                if (InvokeRequired)
                    Invoke((MethodInvoker)(() => SaveSession()));
                else
                    SaveSession();
            }
            catch (Exception ex)
            {
                ErrorLogging.LogLine("Emergency session save failed: " + ex.Message);
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
            viewer.DirtyStateChanged += Viewer_DirtyStateChanged;

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
                Title               = tabTitle,
            };
            tabStates.Add(state);
            GraphTabControl.AddGraphTab(viewer, tabTitle);
            SaveSession();
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
            SessionManager.DeleteSnapshot(tabStates[index].SnapshotFile);
            tabStates.RemoveAt(index);
            GraphTabControl.RemoveGraphTab(index);
            UpdateTitleBar();
            SaveSession();
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
            string unsavedMarker = (ActiveViewer?.IsDirty ?? false) ? " *" : "";
            this.Text = $"{DefaultAppName} ({Properties.Settings.Default.CurrentPresetName}) - {path}{unsavedMarker}";
        }

        /// <summary>Keeps the unsaved marker on the tab header and title bar in step with the graph.</summary>
        private void Viewer_DirtyStateChanged(object sender, EventArgs e)
        {
            for (int i = 0; i < GraphTabControl.RealTabCount; i++)
            {
                if (!ReferenceEquals(GraphTabControl.GetViewer(i), sender)) continue;
                UpdateTabTitle(i);
                if (i == GraphTabControl.SelectedIndex)
                    UpdateTitleBar();
                return;
            }
        }

        /// <summary>Tab header without the unsaved-changes marker.</summary>
        private string GetTabBaseTitle(int tabIndex)
        {
            if (tabIndex < 0 || tabIndex >= tabStates.Count) return $"New Graph {tabIndex + 1}";
            string path = tabStates[tabIndex].SaveFilePath;
            if (path != null)
            {
                try { return Path.GetFileNameWithoutExtension(path); }
                catch { return path; }
            }
            return tabStates[tabIndex].Title ?? $"New Graph {tabIndex + 1}";
        }

        private void UpdateTabTitle(int tabIndex)
        {
            if (tabIndex < 0 || tabIndex >= tabStates.Count) return;
            string label = GetTabBaseTitle(tabIndex);
            var viewer = GraphTabControl.GetViewer(tabIndex);
            //marker goes in front: tabs are a fixed width and long names are drawn with an ellipsis,
            //which would swallow a trailing marker exactly on the graphs most likely to be unsaved
            if (viewer != null && viewer.IsDirty)
                label = "* " + label;
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
            // No save prompts on exit: every tab - saved, modified or brand new - is written to the local
            // session store below and comes back exactly as it was on the next launch.
            sessionAutosaveTimer?.Stop();
            SaveSession();

            // Persist the file path of every saved tab as well (used if the session store is unavailable)
            try
            {
                var openPaths = tabStates
                    .Select(s => s.SaveFilePath ?? "")
                    .ToList();
                Properties.Settings.Default.LastOpenTabs = JsonConvert.SerializeObject(openPaths);
                Properties.Settings.Default.LastOpenFile = ActiveTabState?.SaveFilePath ?? "";
            }
            catch (Exception ex) { ErrorLogging.LogLine("Could not record the open tab list: " + ex.Message); }
            SafeSettings.Save();
        }

        private void SaveGraphAs() => SaveGraphAs(GraphTabControl.SelectedIndex);
        private void SaveGraphAs(int tabIndex)
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.DefaultExt    = ".fjson";
                dialog.Filter        = "Foreman files (*.fjson)|*.fjson|All files|*.*";
                string lastSaveDir   = Properties.Settings.Default.LastSaveFileLocation;
                string defaultDir    = GetSavedGraphsDirectory();
                dialog.InitialDirectory = SafeIO.DirectoryExists(lastSaveDir) ? lastSaveDir : defaultDir;
                dialog.AddExtension    = true;
                dialog.OverwritePrompt = true;
                dialog.FileName        = "Flowchart.fjson";
                if (dialog.ShowDialog() != DialogResult.OK) return;
                if (SaveGraph(tabIndex, dialog.FileName))
                {
                    try { Properties.Settings.Default.LastSaveFileLocation = Path.GetDirectoryName(dialog.FileName); }
                    catch (Exception ex) { ErrorLogging.LogLine($"Could not resolve directory of '{dialog.FileName}': {ex.Message}"); }
                    SafeSettings.Save();
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
        /// <summary>The default "Saved Graphs" folder - returns the startup path itself if it cannot be created.</summary>
        private string GetSavedGraphsDirectory()
        {
            try
            {
                string dir = Path.Combine(Application.StartupPath, "Saved Graphs");
                return SafeIO.EnsureDirectory(dir) ? dir : Application.StartupPath;
            }
            catch (Exception ex)
            {
                ErrorLogging.LogLine("Could not resolve the Saved Graphs folder: " + ex.Message);
                return "";
            }
        }

        private bool SaveGraph(int tabIndex, string path)
        {
            if (tabIndex < 0 || tabIndex >= tabStates.Count) return false;
            var state  = tabStates[tabIndex];
            var viewer = GraphTabControl.GetViewer(tabIndex);
            if (viewer == null) return false;
            string previousPath = state.SaveFilePath;
            try
            {
                string json = GetGraphJson(viewer);
                File.WriteAllText(path, json);
                state.SaveFilePath = path;
                viewer.MarkClean();

                // the saved file now holds this content - drop the backups that were standing in for it
                GraphBackup.Delete(path);
                if (previousPath != null && previousPath != path)
                    GraphBackup.Delete(previousPath);

                UpdateTabTitle(tabIndex);
                if (tabIndex == GraphTabControl.SelectedIndex)
                    UpdateTitleBar();
                SaveSession();
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
                string lastLoadDir = Properties.Settings.Default.LastSaveFileLocation;
                dialog.InitialDirectory = SafeIO.DirectoryExists(lastLoadDir) ? lastLoadDir : GetSavedGraphsDirectory();
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog() != DialogResult.OK) return;
                LoadGraph(dialog.FileName);
            }
        }

        private async void LoadGraph(string path)
        {
            int tabIndex = GraphTabControl.SelectedIndex;
            var state    = ActiveTabState;
            if (state == null || ActiveViewer == null) return;

            string json = GetJsonToLoad(path, out bool fromBackup);
            if (json == null)
            {
                MessageBox.Show("Could not read this file. See log for more details");
                return;
            }
            try
            {
                await ActiveViewer.LoadFromJson(JObject.Parse(json), false, true);
                if (fromBackup) ActiveViewer.MarkDirty();   // recovered work is not in the save file yet
                else ActiveViewer.MarkClean();
                state.SaveFilePath = path;
                UpdateTabTitle(tabIndex);
                try { Properties.Settings.Default.LastSaveFileLocation = Path.GetDirectoryName(path); }
                catch (Exception ex) { ErrorLogging.LogLine($"Could not resolve directory of '{path}': {ex.Message}"); }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not load this file. See log for more details");
                ErrorLogging.LogLine($"Error loading file '{path}'. Error: '{ex.Message}'");
                ErrorLogging.LogLine($"Full error output: {ex}");
            }

            var v = ActiveViewer;
            if (v == null) return;
            Properties.Settings.Default.EnableExtraProductivityForNonMiners = v.Graph.EnableExtraProductivityForNonMiners;
            Properties.Settings.Default.DefaultRateUnit        = (int)v.Graph.SelectedRateUnit;
            Properties.Settings.Default.DefaultAssemblerOption = (int)v.Graph.AssemblerSelector.DefaultSelectionStyle;
            Properties.Settings.Default.DefaultModuleOption    = (int)v.Graph.ModuleSelector.DefaultSelectionStyle;
            Properties.Settings.Default.DefaultNodeDirection   = (int)v.Graph.DefaultNodeDirection;
            SafeSettings.Save();
            SyncToolbarToActiveTab();
            v.Invalidate();
            UpdateTitleBar();
            SaveSession();
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
            state.Title = $"New Graph {GraphTabControl.SelectedIndex + 1}";
            UpdateTabTitle(GraphTabControl.SelectedIndex);
            SafeSettings.Save();
            UpdateTitleBar();
            SaveSession();
        }

		private void ImportGraph()
		{
			OpenFileDialog dialog = new OpenFileDialog();
			dialog.Filter = "Foreman files (*.fjson)|*.fjson|Old Foreman files (*.json)|*.json";
            string lastLoadDir = Properties.Settings.Default.LastSaveFileLocation;
            dialog.InitialDirectory = SafeIO.DirectoryExists(lastLoadDir) ? lastLoadDir : GetSavedGraphsDirectory();
            dialog.CheckFileExists = true;
			if (dialog.ShowDialog() != DialogResult.OK)
				return;

			ImportGraph(dialog.FileName);
		}

        private void ImportGraph(string path)
        {
            var v = ActiveViewer;
            if (v == null) return;
            string json = SafeIO.ReadAllText(path);
            if (json == null)
            {
                MessageBox.Show("Could not read this file. See log for more details");
                return;
            }
            try
            {
                v.ImportNodesFromJson(
                    (JObject)JObject.Parse(json)["ProductionGraph"],
                    v.ScreenToGraph(new Point(v.Width / 2, v.Height / 2)), true);
                Properties.Settings.Default.LastSaveFileLocation = Path.GetDirectoryName(path);
                SafeSettings.Save();
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
            if (tabIndex < 0 || tabIndex >= tabStates.Count) return true;
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

            if (!SafeIO.FileExists(state.SaveFilePath))
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
			string[] presetFiles;
			try { presetFiles = Directory.GetFiles(Path.Combine(Application.StartupPath, "Presets"), "*.pjson"); }
			catch (Exception ex) //the install location can be a network share that just went away
			{
				ErrorLogging.LogLine("Could not read the Presets folder: " + ex.Message);
				MessageBox.Show("The Presets folder could not be read (" + ex.Message + ").\nCheck that Foreman's install location is still accessible.");
				return null;
			}
			foreach (string presetFile in presetFiles)
				if (SafeIO.FileExists(Path.ChangeExtension(presetFile, "dat")))
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

			SafeSettings.Save();
			return presets;
		}

        private async void SettingsButton_Click(object sender, EventArgs e)
        {
            var activeViewer = ActiveViewer;
            if (activeViewer == null) return;

            SettingsForm.SettingsFormOptions options = new SettingsForm.SettingsFormOptions(activeViewer.DCache);

            options.Presets        = GetValidPresetsList();
            if (options.Presets == null || options.Presets.Count == 0)
            {
                MessageBox.Show("No presets are available - settings cannot be opened.");
                return;
            }
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
                    SafeSettings.Save();

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
            SafeSettings.Save();
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
            SafeSettings.Save();
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
            SafeSettings.Save();
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
            SafeSettings.Save();
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
            SafeSettings.Save();
            if (ActiveTabState != null) ActiveTabState.ShowGridlines = GridlinesCheckbox.Checked;
        }

        private void AlignSelectionButton_Click(object sender, EventArgs e)
        {
            ActiveViewer?.AlignSelected();
        }

        //---------------------------------------------------------link tracing controls
        //selection highlighting is a working preference rather than a property of any one graph, so it lives in
        //application settings and applies to every open tab at once

        private CheckBox linkTracingCheckbox;
        private ComboBox linkTraceWidthDropDown;

        private static readonly int[] linkTraceWidths = new int[] { 6, 10, 14, 20, 28, 40 };

        private void BuildLinkTracingControls()
        {
            GridlinesTable.RowCount += 2;
            GridlinesTable.RowStyles.Add(new RowStyle());
            GridlinesTable.RowStyles.Add(new RowStyle());

            linkTracingCheckbox = new CheckBox()
            {
                Text = "Trace selected links",
                AutoSize = true,
                Checked = Properties.Settings.Default.ShowLinkTracing
            };
            linkTracingCheckbox.CheckedChanged += LinkTracingCheckbox_CheckedChanged;
            GridlinesTable.Controls.Add(linkTracingCheckbox, 0, 3);
            GridlinesTable.SetColumnSpan(linkTracingCheckbox, 2);

            GridlinesTable.Controls.Add(new Label() { Text = "Trace size", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);

            linkTraceWidthDropDown = new ComboBox() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(2) };
            foreach (int width in linkTraceWidths)
                linkTraceWidthDropDown.Items.Add(width.ToString());
            SelectCurrentTraceWidth();
            linkTraceWidthDropDown.SelectedIndexChanged += LinkTraceWidthDropDown_SelectedIndexChanged;
            GridlinesTable.Controls.Add(linkTraceWidthDropDown, 1, 4);
        }

        //an existing saved value need not be one of the presets, so fall back to the nearest rather than clearing it
        private void SelectCurrentTraceWidth()
        {
            int current = Properties.Settings.Default.LinkTraceWidth;
            int bestIndex = 0;
            for (int i = 1; i < linkTraceWidths.Length; i++)
                if (Math.Abs(linkTraceWidths[i] - current) < Math.Abs(linkTraceWidths[bestIndex] - current))
                    bestIndex = i;
            linkTraceWidthDropDown.SelectedIndex = bestIndex;
        }

        private void LinkTracingCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.ShowLinkTracing = linkTracingCheckbox.Checked;
            SafeSettings.Save();
            InvalidateAllViewers();
        }

        private void LinkTraceWidthDropDown_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (linkTraceWidthDropDown.SelectedIndex < 0)
                return;
            Properties.Settings.Default.LinkTraceWidth = linkTraceWidths[linkTraceWidthDropDown.SelectedIndex];
            SafeSettings.Save();
            InvalidateAllViewers();
        }

        private void InvalidateAllViewers()
        {
            foreach (TabPage page in GraphTabControl.TabPages)
                foreach (Control control in page.Controls)
                    if (control is ProductionGraphViewer viewer)
                        viewer.Invalidate();
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
