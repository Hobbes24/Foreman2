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
        private int  _hotCloseTab = -1;   // real-tab index whose × is under the pointer, -1 for none

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
            // Defensive guard: never allow the + tab to become selected
            if (e.TabPage?.Name == PlusTabKey)
            {
                e.Cancel = true;
                return;
            }
            base.OnSelecting(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // Check close buttons on real tabs first
                for (int i = 0; i < RealTabCount; i++)
                {
                    if (GetCloseButtonRect(i).Contains(e.Location))
                    {
                        CloseTabRequested?.Invoke(this, i);
                        return;    // suppress normal tab-select from the × click area
                    }
                }
                // Intercept + tab clicks before base processes them so selection never changes
                int plusIdx = RealTabCount;  // + tab is always the last one
                if (plusIdx < TabPages.Count && GetTabRect(plusIdx).Contains(e.Location))
                {
                    if (!_suppressEvents)
                        AddTabRequested?.Invoke(this, EventArgs.Empty);
                    return;
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

                // × close button. the hot tab is tracked in OnMouseMove rather than re-tested against the live cursor
                // here, so what is drawn always matches what triggered the repaint
                Rectangle cr    = GetCloseButtonRect(e.Index);
                bool closeHover = e.Index == _hotCloseTab;
                using (var xPen = new Pen(closeHover ? Color.Red : fg, 1.5f))
                {
                    e.Graphics.DrawLine(xPen, cr.Left, cr.Top,   cr.Right,  cr.Bottom);
                    e.Graphics.DrawLine(xPen, cr.Right, cr.Top,  cr.Left,   cr.Bottom);
                }
            }
        }

        // The × changes colour on hover, so the strip does have to repaint when that changes - but only then. This
        // used to invalidate the whole header band on every single mouse move, which at a modern pointer's poll rate
        // is a continuous repaint storm. A native tab strip paints straight to the screen instead of through a back
        // buffer (DoubleBuffered does not cover owner-drawn tabs), so each of those repaints was a visible flash.
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hot = -1;
            for (int i = 0; i < RealTabCount; i++)
            {
                if (GetCloseButtonRect(i).Contains(e.Location))
                {
                    hot = i;
                    break;
                }
            }

            if (hot != _hotCloseTab)
            {
                InvalidateTab(_hotCloseTab);   // repaint the one being left
                InvalidateTab(hot);            // and the one being entered
                _hotCloseTab = hot;
            }

            base.OnMouseMove(e);
        }

        // The pointer can leave without a final move over the tab it was on, which would strand that × red
        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hotCloseTab != -1)
            {
                InvalidateTab(_hotCloseTab);
                _hotCloseTab = -1;
            }
            base.OnMouseLeave(e);
        }

        // OnDrawItem redraws a whole tab at a time, so the update region has to be the tab, not just the button
        private void InvalidateTab(int tabIndex)
        {
            if (tabIndex < 0 || tabIndex >= RealTabCount)
                return;
            Invalidate(GetTabRect(tabIndex));
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
