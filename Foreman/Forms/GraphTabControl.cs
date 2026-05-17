using System;
using System.Drawing;
using System.Windows.Forms;

namespace Foreman
{
    /// <summary>
    /// TabControl subclass that renders a persistent "+" tab for adding new tabs
    /// and an "×" close button on each real tab. OwnerDraw inherits BackColor/ForeColor
    /// from the ChangeTheme recursive pass, so dark mode is handled automatically.
    /// </summary>
    public class GraphTabControl : TabControl
    {
        // ── Events ──────────────────────────────────────────────────────────────
        public event EventHandler AddTabRequested;
        public event EventHandler<int> CloseTabRequested;   // arg = real-tab index

        // ── Constants ───────────────────────────────────────────────────────────
        private const string PlusTabKey      = "__PLUS_TAB__";
        private const int    CloseButtonSize = 14;
        private const int    CloseButtonRMgn = 4;   // right margin inside tab rect

        // ── Fields ──────────────────────────────────────────────────────────────
        private readonly TabPage _plusTab;
        private bool _suppressEvents = false;

        // ── Constructor ─────────────────────────────────────────────────────────
        public GraphTabControl()
        {
            DrawMode = TabDrawMode.OwnerDrawFixed;
            SizeMode = TabSizeMode.Fixed;
            ItemSize = new Size(140, 22);

            _plusTab      = new TabPage();
            _plusTab.Name = PlusTabKey;
            _plusTab.Text = "+";
            TabPages.Add(_plusTab);
        }

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>Returns the ProductionGraphViewer of the active real tab, or null.</summary>
        public ProductionGraphViewer ActiveViewer
        {
            get
            {
                var tab = SelectedTab;
                if (tab == null || tab.Name == PlusTabKey) return null;
                return tab.Controls.Count > 0 ? tab.Controls[0] as ProductionGraphViewer : null;
            }
        }

        /// <summary>Number of real (non-plus) tabs.</summary>
        public int RealTabCount => TabPages.Count - 1;

        /// <summary>Returns the viewer at real-tab index i (0-based), or null.</summary>
        public ProductionGraphViewer GetViewer(int i)
        {
            if (i < 0 || i >= RealTabCount) return null;
            return TabPages[i].Controls.Count > 0
                ? TabPages[i].Controls[0] as ProductionGraphViewer
                : null;
        }

        /// <summary>Sets the header text of a real tab.</summary>
        public void SetTabTitle(int i, string title)
        {
            if (i >= 0 && i < RealTabCount)
                TabPages[i].Text = title;
        }

        /// <summary>
        /// Inserts a new real tab with the given viewer docked Fill, immediately
        /// before the "+" tab, then selects it.
        /// </summary>
        public void AddGraphTab(ProductionGraphViewer viewer, string title)
        {
            var page = new TabPage(title);
            viewer.Dock = DockStyle.Fill;
            page.Controls.Add(viewer);

            _suppressEvents = true;
            TabPages.Insert(RealTabCount, page);   // insert before + tab
            _suppressEvents = false;

            SelectedTab = page;                    // fires SelectedIndexChanged
        }

        /// <summary>
        /// Removes the real tab at index i without prompting. Caller is responsible
        /// for all save checks before calling this.
        /// </summary>
        public void RemoveGraphTab(int i)
        {
            if (i < 0 || i >= RealTabCount) return;

            _suppressEvents = true;
            TabPages.RemoveAt(i);
            _suppressEvents = false;

            // Clamp selection to a valid real tab
            int newSel = Math.Min(i, RealTabCount - 1);
            if (newSel >= 0) SelectedIndex = newSel;
        }

        // ── Tab event overrides ──────────────────────────────────────────────────

        protected override void OnSelecting(TabControlCancelEventArgs e)
        {
            if (e.TabPage?.Name == PlusTabKey)
            {
                e.Cancel = true;
                if (!_suppressEvents)
                    AddTabRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            base.OnSelecting(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                for (int i = 0; i < RealTabCount; i++)
                {
                    if (GetCloseButtonRect(i).Contains(e.Location))
                    {
                        CloseTabRequested?.Invoke(this, i);
                        return;    // suppress normal tab-select from the × click area
                    }
                }
            }
            base.OnMouseDown(e);
        }

        // ── Drawing ─────────────────────────────────────────────────────────────

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= TabPages.Count) return;
            bool isPlus     = TabPages[e.Index].Name == PlusTabKey;
            bool isSelected = e.Index == SelectedIndex;

            // Background — use ChangeTheme-provided colors automatically
            Color bgSel   = Color.FromArgb(
                Math.Min(255, BackColor.R + 28),
                Math.Min(255, BackColor.G + 28),
                Math.Min(255, BackColor.B + 28));
            Color bgUnsel = BackColor;
            Color fg      = ForeColor;

            using (var bgBrush = new SolidBrush(isSelected ? bgSel : bgUnsel))
                e.Graphics.FillRectangle(bgBrush, e.Bounds);

            using (var borderPen = new Pen(Color.FromArgb(80, fg.R, fg.G, fg.B)))
                e.Graphics.DrawRectangle(borderPen,
                    e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);

            if (isPlus)
            {
                // Centre the "+" glyph
                using (var boldFont = new Font(Font, FontStyle.Bold))
                using (var fgBrush  = new SolidBrush(fg))
                {
                    SizeF sz = e.Graphics.MeasureString("+", boldFont);
                    float x  = e.Bounds.X + (e.Bounds.Width  - sz.Width)  / 2f;
                    float y  = e.Bounds.Y + (e.Bounds.Height - sz.Height) / 2f;
                    e.Graphics.DrawString("+", boldFont, fgBrush, x, y);
                }
            }
            else
            {
                // Tab text — truncated to leave room for the × button
                Rectangle textRect = new Rectangle(
                    e.Bounds.X + 4,
                    e.Bounds.Y,
                    e.Bounds.Width - 4 - CloseButtonSize - CloseButtonRMgn - 2,
                    e.Bounds.Height);

                TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text, Font, textRect,
                    fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis);

                // × close button
                Rectangle cr    = GetCloseButtonRect(e.Index);
                bool closeHover = cr.Contains(PointToClient(Cursor.Position));
                using (var xPen = new Pen(closeHover ? Color.Red : fg, 1.5f))
                {
                    e.Graphics.DrawLine(xPen, cr.Left, cr.Top,   cr.Right,  cr.Bottom);
                    e.Graphics.DrawLine(xPen, cr.Right, cr.Top,  cr.Left,   cr.Bottom);
                }
            }
        }

        // Invalidate tab area on mouse move so the × hover colour updates
        protected override void OnMouseMove(MouseEventArgs e)
        {
            Invalidate(new Rectangle(0, 0, Width, ItemSize.Height + 4));
            base.OnMouseMove(e);
        }

        private Rectangle GetCloseButtonRect(int tabIndex)
        {
            Rectangle r = GetTabRect(tabIndex);
            return new Rectangle(
                r.Right  - CloseButtonSize - CloseButtonRMgn,
                r.Top    + (r.Height - CloseButtonSize) / 2,
                CloseButtonSize, CloseButtonSize);
        }
    }
}
