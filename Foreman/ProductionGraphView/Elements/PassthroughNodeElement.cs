using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Foreman
{
	public class PassthroughNodeElement : BaseNodeElement
	{
		protected override Brush CleanBgBrush { get { return passthroughBGBrush; } }
		private static Brush passthroughBGBrush = new SolidBrush(Color.FromArgb(200, 200, 200));

		private string ItemName { get { return DisplayedNode.PassthroughItem.FriendlyName; } }

		private new readonly ReadOnlyPassthroughNode DisplayedNode;

		private const int GutterThickness = PassthroughNodeWidth; //a gutter is as thick as a passthrough node is wide
		private const int GutterCornerRadius = GutterThickness / 3;
		private const int GutterArrowNear = 7;
		private const int GutterArrowFar = 17;
		private const int GutterArrowHalfBase = 5;
		private const int GutterArrowHitSlack = 4; //grab tolerance around the drawn arrow
		private const int GutterMarkerRadius = 4;
		private const int GutterHandleGrab = 16;   //how close to an end a click counts as grabbing that end
		private const int GutterHandleLength = 10; //drawn size of the grab bar at each end
		private static readonly Color gutterOutlineColor = Color.FromArgb(80, 80, 80);

		//DisplayedNode is still null while the base constructor runs, and that constructor reads Width/Height - hence the guard
		private bool IsGutter { get { return DisplayedNode != null && DisplayedNode.IsGutter; } }
		private bool GutterIsVertical { get { return DisplayedNode.GutterOrientation == GutterOrientation.Vertical; } }

		//resize drag state. 0 = not resizing, -1 = dragging the low end, +1 = dragging the high end
		private int resizeHandle;
		private int resizeFixedEnd;
		private bool resizeUndoPushed;

		//marker drag state - the far node whose connection is being slid along the gutter, null when not dragging one
		private ReadOnlyBaseNode markerDragFarNode;
		private bool markerUndoPushed;

		//a gutter occupies its entire span, so Bounds, hit testing and off-screen culling all follow from these two
		private int baseWidth;
		private int baseHeight;

		public override int Width
		{
			get { return IsGutter ? (GutterIsVertical ? GutterThickness : DisplayedNode.GutterLength) : baseWidth; }
			set { baseWidth = value; }
		}
		public override int Height
		{
			get { return IsGutter ? (GutterIsVertical ? DisplayedNode.GutterLength : GutterThickness) : baseHeight; }
			set { baseHeight = value; }
		}

		public PassthroughNodeElement(ProductionGraphViewer graphViewer, ReadOnlyPassthroughNode node) : base(graphViewer, node)
		{
			Width = PassthroughNodeWidth;
			Height = BaseSimpleHeight;
			DisplayedNode = node;
		}

		//which side of the gutter a far node sits on, so a link and its arrow land on the side they actually come from
		private int GutterSideFor(Point reference)
		{
			Point center = LocalToGraph(new Point(0, 0));
			int delta = GutterIsVertical ? reference.X - center.X : reference.Y - center.Y;
			return delta < 0 ? -1 : 1;
		}

		//every link to a gutter attaches at its own point: project the far end onto the gutter's axis, clamp to its ends,
		//then push it out to the body edge on the side the link comes from. that is what keeps each link a short
		//perpendicular stub instead of a long diagonal
		private Point GutterPointFor(Point reference)
		{
			Point center = LocalToGraph(new Point(0, 0));
			int half = DisplayedNode.GutterLength / 2;
			int edge = (GutterThickness / 2) * GutterSideFor(reference);
			if (GutterIsVertical)
				return new Point(center.X + edge, Math.Min(center.Y + half, Math.Max(center.Y - half, reference.Y)));
			return new Point(Math.Min(center.X + half, Math.Max(center.X - half, reference.X)), center.Y + edge);
		}

		//as above, but honouring a manual offset if this link has been dragged to a spot. every visual derived from this
		//- the marker, the arrow, the tooltip hit region, the link endpoint - moves together because they all call it
		private Point GutterPointFor(ReadOnlyBaseNode farNode)
		{
			Point center = LocalToGraph(new Point(0, 0));
			int half = DisplayedNode.GutterLength / 2;
			int edge = (GutterThickness / 2) * GutterSideFor(farNode.Location);
			int centreAlong = GutterIsVertical ? center.Y : center.X;

			int along;
			if (DisplayedNode.TryGetGutterLinkOffset(farNode.NodeID, out int manual))
				along = centreAlong + manual;
			else
				along = GutterIsVertical ? farNode.Location.Y : farNode.Location.X;
			along = Math.Min(centreAlong + half, Math.Max(centreAlong - half, along));

			return GutterIsVertical ? new Point(center.X + edge, along) : new Point(along, center.Y + edge);
		}

		public override Point GetLinkAttachPoint(ReadOnlyNodeLink link, LinkType side, Point defaultPoint, Point reference)
		{
			if (!IsGutter)
				return defaultPoint;
			//side is this node's own role in the link, so the far end is the opposite one
			return GutterPointFor(side == LinkType.Output ? link.Consumer : link.Supplier);
		}

		//a gutter's connection arrows are drawn standing off the bar, so they fall outside the node's own bounds.
		//without claiming them here the viewer never routes the mouse to this element and they cannot be hit at all
		public override bool ContainsPoint(Point graph_point)
		{
			if (base.ContainsPoint(graph_point))
				return true;
			return Visible && IsGutter && GutterLinkAt(graph_point, out _) != null;
		}

		//hit test matching what DrawGutterConnection paints: the marker on the body plus the arrow standing off it
		private bool GutterConnectionHit(Point graph_point, ReadOnlyBaseNode farNode)
		{
			Point attach = GutterPointFor(farNode);
			int side = GutterSideFor(farNode.Location);
			int along = GutterIsVertical ? graph_point.Y - attach.Y : graph_point.X - attach.X;
			int across = (GutterIsVertical ? graph_point.X - attach.X : graph_point.Y - attach.Y) * side;
			//the region starts outside the bar rather than at the marker on its edge - overlapping the body meant a click
			//meant for the gutter itself kept catching a connection instead
			return Math.Abs(along) <= GutterArrowHalfBase + GutterArrowHitSlack
				&& across >= GutterArrowNear - GutterArrowHitSlack
				&& across <= GutterArrowFar + GutterArrowHitSlack;
		}

		private ReadOnlyNodeLink GutterLinkAt(Point graph_point, out bool intoGutter)
		{
			intoGutter = false;
			if (!IsGutter)
				return null;
			foreach (ReadOnlyNodeLink link in DisplayedNode.InputLinks)
				if (GutterConnectionHit(graph_point, link.Supplier))
				{
					intoGutter = true;
					return link;
				}
			foreach (ReadOnlyNodeLink link in DisplayedNode.OutputLinks)
				if (GutterConnectionHit(graph_point, link.Consumer))
					return link;
			return null;
		}

		//node ToString is built for logging and carries a "RO: " wrapper prefix - strip it for display
		private static string DescribeFarNode(ReadOnlyBaseNode node)
		{
			string text = node.ToString();
			return text.StartsWith("RO: ") ? text.Substring(4) : text;
		}

		//which resize handle a point falls on: -1 for the low end, +1 for the high end, 0 for neither
		private int GutterHandleAt(Point graph_point)
		{
			if (!IsGutter)
				return 0;
			Point center = LocalToGraph(new Point(0, 0));
			int half = DisplayedNode.GutterLength / 2;
			int along = GutterIsVertical ? graph_point.Y : graph_point.X;
			int across = GutterIsVertical ? graph_point.X : graph_point.Y;
			int centerAlong = GutterIsVertical ? center.Y : center.X;
			int centerAcross = GutterIsVertical ? center.X : center.Y;

			if (Math.Abs(across - centerAcross) > GutterThickness / 2)
				return 0;
			if (Math.Abs(along - (centerAlong - half)) <= GutterHandleGrab)
				return -1;
			if (Math.Abs(along - (centerAlong + half)) <= GutterHandleGrab)
				return 1;
			return 0;
		}

		//zoomed in, a gutter end looks the same whether it will move or resize - the cursor is what tells them apart.
		//mirrors the click rules exactly: item tabs win, so hovering an end icon stays the normal pointer
		public override Cursor GetCursorForPoint(Point graph_point)
		{
			if (!IsGutter || !Visible)
				return null;
			if (SubElements.OfType<ItemTabElement>().Any(t => t.ContainsPoint(graph_point)))
				return null;
			//markers slide along the same axis the ends resize along, so both get the same cursor. matches the click
			//rules exactly: on the body only the ends are grabbable, markers only outside it
			bool onMarker = !base.ContainsPoint(graph_point) && GutterLinkAt(graph_point, out _) != null;
			if (!onMarker && GutterHandleAt(graph_point) == 0)
				return null;
			return GutterIsVertical ? Cursors.SizeNS : Cursors.SizeWE;
		}

		private void ApplyGutterResize(Point graph_point)
		{
			int moving = GutterIsVertical ? graph_point.Y : graph_point.X;
			if (graphViewer.Grid.ShowGrid)
				moving = graphViewer.Grid.AlignToGrid(moving);

			//the end you are not dragging stays exactly where it is, so the gutter grows and shrinks from the grabbed end only
			int length = Math.Min(ProductionGraphViewer.MaximumGutterLength,
				Math.Max(ProductionGraphViewer.MinimumGutterLength, Math.Abs(moving - resizeFixedEnd)));
			int newCenterAlong = resizeFixedEnd + (resizeHandle * (length / 2));

			Point center = LocalToGraph(new Point(0, 0));
			PassthroughNodeController controller = (PassthroughNodeController)graphViewer.Graph.RequestNodeController(DisplayedNode);
			controller.SetLocation(GutterIsVertical ? new Point(center.X, newCenterAlong) : new Point(newCenterAlong, center.Y));
			controller.SetGutterLength(length);
			graphViewer.Invalidate();
		}

		public override void MouseDown(Point graph_point, MouseButtons button)
		{
			//claim order on a gutter: item tabs pull out new links, then connection markers slide along it, then the
			//ends resize it. the tabs sit right on top of the resize zones, which is why they have to be tested first
			bool onItemTab = SubElements.OfType<ItemTabElement>().Any(t => t.ContainsPoint(graph_point));
			resizeHandle = 0;
			markerDragFarNode = null;
			resizeUndoPushed = false;
			markerUndoPushed = false;

			if (button == MouseButtons.Left && !onItemTab)
			{
				//a click on the bar itself always belongs to the gutter - markers can only be grabbed from outside the
				//body, so a connection sitting near the edge can never steal a drag meant to move the whole thing
				bool onBody = base.ContainsPoint(graph_point);
				ReadOnlyNodeLink markerLink = null;
				bool markerIntoGutter = false;
				if (!onBody)
					markerLink = GutterLinkAt(graph_point, out markerIntoGutter);

				if (markerLink != null)
					markerDragFarNode = markerIntoGutter ? markerLink.Supplier : markerLink.Consumer;
				else
					resizeHandle = GutterHandleAt(graph_point);
			}

			if (resizeHandle != 0)
			{
				Point center = LocalToGraph(new Point(0, 0));
				int half = DisplayedNode.GutterLength / 2;
				int centerAlong = GutterIsVertical ? center.Y : center.X;
				resizeFixedEnd = centerAlong - (resizeHandle * half);
			}
			base.MouseDown(graph_point, button);
		}

		//the viewer only delivers MouseUp to an element when the gesture was a click, never after a real drag, so a resize
		//cannot rely on MouseUp to disarm it. left armed, every later mouse move - panning included - kept resizing.
		//the button state is the authority on whether a resize is still happening
		private void DisarmResizeIfButtonReleased()
		{
			if ((Control.MouseButtons & MouseButtons.Left) == 0)
			{
				resizeHandle = 0;
				markerDragFarNode = null;
			}
		}

		public bool IsResizing { get { return resizeHandle != 0 || markerDragFarNode != null; } }

		//slides one link's connection point along the gutter. the offset is stored against the far node rather than a
		//screen position, so it survives the gutter itself being moved or resized afterwards
		private void ApplyMarkerDrag(Point graph_point)
		{
			Point center = LocalToGraph(new Point(0, 0));
			int half = DisplayedNode.GutterLength / 2;
			int centreAlong = GutterIsVertical ? center.Y : center.X;
			int along = GutterIsVertical ? graph_point.Y : graph_point.X;
			if (graphViewer.Grid.ShowGrid)
				along = graphViewer.Grid.AlignToGrid(along);

			int offset = Math.Min(half, Math.Max(-half, along - centreAlong));
			((PassthroughNodeController)graphViewer.Graph.RequestNodeController(DisplayedNode)).SetGutterLinkOffset(markerDragFarNode.NodeID, offset);
			graphViewer.Invalidate();
		}

		public override void Dragged(Point graph_point)
		{
			DisarmResizeIfButtonReleased();
			if (markerDragFarNode != null)
			{
				if (!markerUndoPushed)
				{
					graphViewer.PushUndoState(); // undo: move gutter connection
					markerUndoPushed = true;
				}
				ApplyMarkerDrag(graph_point);
				return;
			}
			if (resizeHandle != 0)
			{
				if (!resizeUndoPushed)
				{
					graphViewer.PushUndoState(); // undo: resize gutter
					resizeUndoPushed = true;
				}
				ApplyGutterResize(graph_point);
				return;
			}
			base.Dragged(graph_point);
		}

		public override void MouseUp(Point graph_point, MouseButtons button, bool wasDragged)
		{
			bool wasResizing = resizeHandle != 0 || markerDragFarNode != null;
			resizeHandle = 0;
			markerDragFarNode = null;
			//pass wasDragged so a resize never falls through into the click handling that opens the edit panel
			base.MouseUp(graph_point, button, wasDragged || wasResizing);
		}

		protected override Bitmap NodeIcon() { return null; }

		//a gutter is drawn as an ordinary node that happens to be very long: same grey, same flow-status border, same
		//item icons at each end. only the corner rounding differs, via the radius overrides below
		private void DrawGutter(Graphics graphics, NodeDrawingStyle style)
		{
			EnsureGutterTabLayout();
			foreach (ItemTabElement tab in InputTabs.Concat(OutputTabs))
				tab.HideItemTab = false;

			base.Draw(graphics, style);

			if (style != NodeDrawingStyle.Regular && style != NodeDrawingStyle.PrintStyle)
				return;

			Point center = LocalToGraph(new Point(0, 0));
			int half = DisplayedNode.GutterLength / 2;
			Rectangle body = GutterIsVertical
				? new Rectangle(center.X - (GutterThickness / 2), center.Y - half, GutterThickness, DisplayedNode.GutterLength)
				: new Rectangle(center.X - half, center.Y - (GutterThickness / 2), DisplayedNode.GutterLength, GutterThickness);

			using (Brush fill = new SolidBrush(DisplayedNode.PassthroughItem.Item.AverageColor))
			using (Pen outline = new Pen(gutterOutlineColor, 1))
			{
				foreach (ReadOnlyNodeLink link in DisplayedNode.InputLinks)
					DrawGutterConnection(graphics, fill, outline, link.Supplier, true);
				foreach (ReadOnlyNodeLink link in DisplayedNode.OutputLinks)
					DrawGutterConnection(graphics, fill, outline, link.Consumer, false);
			}

			//grab bars, shown only while selected so an idle gutter stays clean
			if (Highlighted)
				using (Brush handleBrush = new SolidBrush(gutterOutlineColor))
				{
					if (GutterIsVertical)
					{
						graphics.FillRectangle(handleBrush, body.X, body.Y - (GutterHandleLength / 2), GutterThickness, GutterHandleLength);
						graphics.FillRectangle(handleBrush, body.X, body.Bottom - (GutterHandleLength / 2), GutterThickness, GutterHandleLength);
					}
					else
					{
						graphics.FillRectangle(handleBrush, body.X - (GutterHandleLength / 2), body.Y, GutterHandleLength, GutterThickness);
						graphics.FillRectangle(handleBrush, body.Right - (GutterHandleLength / 2), body.Y, GutterHandleLength, GutterThickness);
					}
				}
		}

		//the base layout puts item tabs at +-Height/2, which is already the two ends of a vertical gutter but the long
		//edges of a horizontal one. recomputed only when the shape actually changed
		private int laidOutLength = -1;
		private GutterOrientation laidOutOrientation;
		private NodeDirection laidOutDirection;

		private void EnsureGutterTabLayout()
		{
			if (laidOutLength == DisplayedNode.GutterLength && laidOutOrientation == DisplayedNode.GutterOrientation && laidOutDirection == DisplayedNode.NodeDirection)
				return;
			laidOutLength = DisplayedNode.GutterLength;
			laidOutOrientation = DisplayedNode.GutterOrientation;
			laidOutDirection = DisplayedNode.NodeDirection;

			UpdateTabOrder();
			if (GutterIsVertical)
				return;

			int half = DisplayedNode.GutterLength / 2;
			bool outputAtLowEnd = DisplayedNode.NodeDirection == NodeDirection.Up;
			foreach (ItemTabElement tab in OutputTabs)
				tab.Location = new Point(outputAtLowEnd ? -half + (tab.Width / 2) : half - (tab.Width / 2), 0);
			foreach (ItemTabElement tab in InputTabs)
				tab.Location = new Point(outputAtLowEnd ? half - (tab.Width / 2) : -half + (tab.Width / 2), 0);
		}

		protected override int BorderCornerRadius { get { return IsGutter ? GutterCornerRadius : base.BorderCornerRadius; } }
		protected override int BackgroundCornerRadius { get { return IsGutter ? GutterCornerRadius - 3 : base.BackgroundCornerRadius; } }
		protected override int OverlayCornerRadius { get { return IsGutter ? GutterCornerRadius : base.OverlayCornerRadius; } }

		//marker plus arrow where one link meets the gutter. the arrow points into the gutter when the gutter is receiving
		//that item and out of it when the gutter is feeding it - read straight off which collection the link sits in,
		//so it stays correct on its own as links are rewired
		private void DrawGutterConnection(Graphics graphics, Brush fill, Pen outline, ReadOnlyBaseNode farNode, bool intoGutter)
		{
			Point farLocation = farNode.Location;
			Point attach = GutterPointFor(farNode);
			graphics.FillEllipse(fill, attach.X - GutterMarkerRadius, attach.Y - GutterMarkerRadius, GutterMarkerRadius * 2, GutterMarkerRadius * 2);
			graphics.DrawEllipse(outline, attach.X - GutterMarkerRadius, attach.Y - GutterMarkerRadius, GutterMarkerRadius * 2, GutterMarkerRadius * 2);

			//measured from the body centre, not from the attach point, which already sits out on the edge
			int side = GutterSideFor(farLocation);

			int tipOffset = intoGutter ? GutterArrowNear : GutterArrowFar;
			int baseOffset = intoGutter ? GutterArrowFar : GutterArrowNear;

			Point tip, cornerA, cornerB;
			if (GutterIsVertical)
			{
				tip = new Point(attach.X + (side * tipOffset), attach.Y);
				cornerA = new Point(attach.X + (side * baseOffset), attach.Y - GutterArrowHalfBase);
				cornerB = new Point(attach.X + (side * baseOffset), attach.Y + GutterArrowHalfBase);
			}
			else
			{
				tip = new Point(attach.X, attach.Y + (side * tipOffset));
				cornerA = new Point(attach.X - GutterArrowHalfBase, attach.Y + (side * baseOffset));
				cornerB = new Point(attach.X + GutterArrowHalfBase, attach.Y + (side * baseOffset));
			}
			Point[] arrow = new Point[] { tip, cornerA, cornerB };
			graphics.FillPolygon(fill, arrow);
			graphics.DrawPolygon(outline, arrow);
		}

		protected override void Draw(Graphics graphics, NodeDrawingStyle style)
		{
			if (IsGutter && style != NodeDrawingStyle.IconsOnly && DisplayedNode.PassthroughItem)
			{
				DrawGutter(graphics, style);
				return;
			}

			if (style != NodeDrawingStyle.IconsOnly && DisplayedNode.SimpleDraw && DisplayedNode.RateType == RateType.Auto && !DisplayedNode.KeyNode && !DisplayedNode.IsOverproducing() && !DisplayedNode.ManualRateNotMet() && DisplayedNode.InputLinks.Any() && DisplayedNode.OutputLinks.Any()
				&& DisplayedNode.PassthroughItem)
			{
				InputTabs[0].HideItemTab = true;
				OutputTabs[0].HideItemTab = true;

				//a link element can be momentarily absent (link added but its element not built yet) - drawing must not throw
				List<float> linkWidths = DisplayedNode.InputLinks.Concat(DisplayedNode.OutputLinks)
					.Where(l => graphViewer.LinkElementDictionary.ContainsKey(l))
					.Select(l => graphViewer.LinkElementDictionary[l].LinkWidth)
					.ToList();
				if (linkWidths.Count == 0)
				{
					InputTabs[0].HideItemTab = false;
					OutputTabs[0].HideItemTab = false;
					base.Draw(graphics, style);
					return;
				}
				float maxLineWidth = linkWidths.Max();
				Point inputPoint = InputTabs[0].GetConnectionPoint();
				Point outputPoint = OutputTabs[0].GetConnectionPoint();
				using (Pen pen = new Pen(DisplayedNode.PassthroughItem.Item.AverageColor, maxLineWidth) { EndCap = System.Drawing.Drawing2D.LineCap.Round, StartCap = System.Drawing.Drawing2D.LineCap.Round })
					graphics.DrawLine(pen, inputPoint, outputPoint);
				if (style == NodeDrawingStyle.Regular)
				{
					using (Brush brush = new SolidBrush(DisplayedNode.PassthroughItem.Item.AverageColor))
					{
						graphics.FillEllipse(brush, inputPoint.X - 6, Math.Min(outputPoint.Y, inputPoint.Y) - 6 + (ItemTabElement.TabWidth / 2), 12, 12);
						graphics.FillEllipse(brush, inputPoint.X - 6, Math.Max(outputPoint.Y, inputPoint.Y) - 6 - (ItemTabElement.TabWidth / 2), 12, 12);
					}

                    if (FindHighlighted)
                        using (Pen pen = new Pen(findOverlayBrush, Math.Max(30, maxLineWidth + 10)) { EndCap = System.Drawing.Drawing2D.LineCap.Round, StartCap = System.Drawing.Drawing2D.LineCap.Round })
                            graphics.DrawLine(pen, inputPoint, outputPoint);
                    if (Highlighted)
						using (Pen pen = new Pen(selectionOverlayBrush, Math.Max(30, maxLineWidth + 10)) { EndCap = System.Drawing.Drawing2D.LineCap.Round, StartCap = System.Drawing.Drawing2D.LineCap.Round })
							graphics.DrawLine(pen, inputPoint, outputPoint);
				}
			}
			else
			{
				InputTabs[0].HideItemTab = false;
				OutputTabs[0].HideItemTab = false;
				base.Draw(graphics, style);
			}
		}

		protected override void DetailsDraw(Graphics graphics, Point trans)
		{
			if (DisplayedNode.RateType == RateType.Manual)
			{
				int yoffset = DisplayedNode.NodeDirection == NodeDirection.Up ? 28 : 32;
				Rectangle titleSlot = new Rectangle(trans.X - (Width / 2) + 5, trans.Y - (Height / 2) + yoffset, Width - 10, 18);
				Rectangle textSlot = new Rectangle(titleSlot.X, titleSlot.Y + 18, titleSlot.Width, 20);
				//graphics.DrawRectangle(devPen, textSlot);
				//graphics.DrawRectangle(devPen, titleSlot);

				graphics.DrawString("-Limit-", TitleFont, TextBrush, titleSlot, TitleFormat);
				GraphicsStuff.DrawText(graphics, TextBrush, TextFormat, GraphicsStuff.DoubleToString(DisplayedNode.DesiredRate), BaseFont, textSlot);
			}
		}

		protected override void MouseUpAction(Point graph_point, MouseButtons button)
		{
			if (button == MouseButtons.Left && (Control.ModifierKeys & Keys.Shift) == Keys.Shift)
			{
				graphViewer.AddSourceNodesByShiftClick(this);
				return;
			}
			base.MouseUpAction(graph_point, button);
		}

		protected override List<TooltipInfo> GetMyToolTips(Point graph_point, bool exclusive)
		{
			List<TooltipInfo> tooltips = new List<TooltipInfo>();

			//a single connection marker reports just its own link - that is the question being asked by pointing at it,
			//so it replaces the whole-gutter totals rather than adding to them
			if (DisplayedNode.IsGutter)
			{
				ReadOnlyNodeLink hoveredLink = GutterLinkAt(graph_point, out bool intoGutter);
				if (hoveredLink != null)
				{
					ReadOnlyBaseNode farNode = intoGutter ? hoveredLink.Supplier : hoveredLink.Consumer;
					TooltipInfo linkToolTipInfo = new TooltipInfo();
					linkToolTipInfo.Text = string.Format("{0} {1} gutter\n{2} / {3}\n{4}",
						ItemName,
						intoGutter ? "into" : "out of",
						GraphicsStuff.DoubleToString(hoveredLink.Throughput),
						graphViewer.Graph.GetRateName(),
						DescribeFarNode(farNode));
					linkToolTipInfo.Direction = Direction.Up;
					linkToolTipInfo.ScreenLocation = graphViewer.GraphToScreen(GutterPointFor(farNode.Location));
					tooltips.Add(linkToolTipInfo);
					return tooltips;
				}
			}

			//only when nothing else claims the pointer - hovering an end icon should give the normal item tab tooltips
			//rather than stacking the gutter stats on top of them
			if (DisplayedNode.IsGutter && exclusive)
			{
				//a gutter carries no label of its own, so hovering has to say what it is and what is moving through it.
				//in and out are summed separately rather than reported as one throughput: the node balances them, so when
				//they disagree the gutter is under or over supplied and seeing both numbers is the point
				double inRate = DisplayedNode.InputLinks.Sum(l => l.Throughput);
				double outRate = DisplayedNode.OutputLinks.Sum(l => l.Throughput);
				string rateName = graphViewer.Graph.GetRateName();

				TooltipInfo statsToolTipInfo = new TooltipInfo();
				statsToolTipInfo.Text = string.Format("{0} gutter\nIn:  {1} / {2}  ({3} links)\nOut: {4} / {5}  ({6} links)",
					ItemName,
					GraphicsStuff.DoubleToString(inRate), rateName, DisplayedNode.InputLinks.Count(),
					GraphicsStuff.DoubleToString(outRate), rateName, DisplayedNode.OutputLinks.Count());
				statsToolTipInfo.Direction = Direction.Up;
				statsToolTipInfo.ScreenLocation = graphViewer.GraphToScreen(GutterPointFor(graph_point));
				tooltips.Add(statsToolTipInfo);
			}

			if (exclusive)
			{
				TooltipInfo helpToolTipInfo = new TooltipInfo();
				helpToolTipInfo.Text = DisplayedNode.IsGutter
					? string.Format("Gutter carrying {0}.\nDrag either end to resize.\nRight click for options.", ItemName)
					: string.Format("Left click to edit throughput of {0}.\nShift+click to add a supplier node.\nRight click for options.", ItemName);
				helpToolTipInfo.Direction = Direction.None;
				helpToolTipInfo.ScreenLocation = new Point(10, 10);
				tooltips.Add(helpToolTipInfo);
			}

			return tooltips;
		}

		protected override void AddRClickMenuOptions(bool nodeInSelection)
		{
			RightClickMenu.Items.Add(new ToolStripSeparator());
			if (DisplayedNode.SimpleDraw)
			{
				RightClickMenu.Items.Add(new ToolStripMenuItem("Dont simple-draw node", null,
					new EventHandler((o, e) =>
					{
						RightClickMenu.Close();
						((PassthroughNodeController)graphViewer.Graph.RequestNodeController(DisplayedNode)).SetSimpleDraw(false);
						graphViewer.Invalidate();
					})));
				if (graphViewer.SelectedNodes.Count > 1 && graphViewer.SelectedNodes.Contains(this))
				{
					RightClickMenu.Items.Add(new ToolStripMenuItem("Dont simple-draw selected nodes", null,
						new EventHandler((o, e) =>
						{
							RightClickMenu.Close();
							graphViewer.SetSelectedPassthroughNodesSimpleDraw(false);
							graphViewer.Invalidate();
						})));
				}
			}
			else
			{
				RightClickMenu.Items.Add(new ToolStripMenuItem("Simple-draw node", null,
					new EventHandler((o, e) =>
					{
						RightClickMenu.Close();
						((PassthroughNodeController)graphViewer.Graph.RequestNodeController(DisplayedNode)).SetSimpleDraw(true);
						graphViewer.Invalidate();
					})));
				if (graphViewer.SelectedNodes.Count > 1 && graphViewer.SelectedNodes.Contains(this))
				{
					RightClickMenu.Items.Add(new ToolStripMenuItem("Simple-draw selected nodes", null,
						new EventHandler((o, e) =>
						{
							RightClickMenu.Close();
							graphViewer.SetSelectedPassthroughNodesSimpleDraw(true);
							graphViewer.Invalidate();
						})));
				}
			}

			//a matching selection is two or more passthrough nodes for this same item, this one among them - the
			//multi-node operations need that; becoming a gutter does not, any passthrough node can do it alone
			bool matchingSelection = graphViewer.SelectedNodes.Count > 1
				&& graphViewer.SelectedNodes.Contains(this)
				&& graphViewer.SelectedNodes.All(n => n is PassthroughNodeElement pne && pne.DisplayedNode.PassthroughItem == DisplayedNode.PassthroughItem);

			RightClickMenu.Items.Add(new ToolStripSeparator());

			if (matchingSelection)
			{
				RightClickMenu.Items.Add(new ToolStripMenuItem("Merge selected passthrough nodes", null,
					new EventHandler((o, e) =>
					{
						RightClickMenu.Close();
						graphViewer.MergeSelectedPassthroughNodes(DisplayedNode);
					})));
			}

			if (DisplayedNode.IsGutter)
			{
				if (matchingSelection)
				{
					RightClickMenu.Items.Add(new ToolStripMenuItem("Add selected nodes to this gutter", null,
						new EventHandler((o, e) =>
						{
							RightClickMenu.Close();
							graphViewer.AddSelectionToGutter(DisplayedNode);
						})));
				}
				//deliberately not requiring this gutter to be in the selection - the natural gesture is to select the
				//producers and then right click the gutter you want them on
				if (graphViewer.SelectedNodes.Any(n => n != this))
				{
					RightClickMenu.Items.Add(new ToolStripMenuItem("Attach unconnected selected nodes", null,
						new EventHandler((o, e) =>
						{
							RightClickMenu.Close();
							graphViewer.AttachUnconnectedToGutter(DisplayedNode);
						})));
				}
				//same toggle the item tabs carry - a gutter's own tabs sit way out at its ends, so reaching this from
				//the bar you are already pointing at is the practical route
				bool gutterItemHidden = graphViewer.Graph.IsUtilityItemHidden(DisplayedNode.PassthroughItem);
				RightClickMenu.Items.Add(new ToolStripMenuItem(gutterItemHidden ? "Show links for this item" : "Hide links for this item", null,
					new EventHandler((o, e) =>
					{
						RightClickMenu.Close();
						graphViewer.SetUtilityItemHidden(DisplayedNode.PassthroughItem, !gutterItemHidden);
					})));
				RightClickMenu.Items.Add(new ToolStripMenuItem("Reset connection positions", null,
					new EventHandler((o, e) =>
					{
						RightClickMenu.Close();
						graphViewer.ResetGutterLinkPositions(DisplayedNode);
					})));
				RightClickMenu.Items.Add(new ToolStripMenuItem("Remove gutter", null,
					new EventHandler((o, e) =>
					{
						RightClickMenu.Close();
						graphViewer.RemoveGutter(DisplayedNode);
					})));
			}
			else
			{
				RightClickMenu.Items.Add(new ToolStripMenuItem(matchingSelection ? "Create gutter from selected nodes" : "Create gutter", null,
					new EventHandler((o, e) =>
					{
						RightClickMenu.Close();
						graphViewer.CreateGutter(DisplayedNode);
					})));
			}

            if (graphViewer.ConversionSnapshots.ContainsKey(DisplayedNode))
            {
                RightClickMenu.Items.Add(new ToolStripSeparator());
                RightClickMenu.Items.Add(new ToolStripMenuItem("Restore original node", null,
                    new EventHandler((o, e) =>
                    {
                        RightClickMenu.Close();
                        graphViewer.RestoreFromSnapshot(DisplayedNode);
                    })));
            }
        }
    }
}
