using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Foreman
{
	public abstract class BaseLinkElement : GraphElement
	{
		public enum LineType { Simple, UShape, NShape }

		public BaseNodeElement SupplierElement { get; protected set; }
		public BaseNodeElement ConsumerElement { get; protected set; }
		public virtual ItemQualityPair Item { get; protected set; }

		private Point consumerOrigin, supplierOrigin;
		private NodeDirection consumerDirection, supplierDirection;

		public LineType Type { get; private set; }

		private Point consumerPull, supplierPull; //for basic links
		private Point midUA, midUB, midUC, midUD, pullU1, pullU2, pullU3, pullU4; //for U shape links
		private Point midNA, midNB, midNC, midND, midNE, midNF, pullN1, pullN2, pullN3, pullN4, pullN5, pullN6, pullN7, pullN8; //for N shape links
		//private Point pointMidA, pointMidAPull, pointMidB, pointMidBPull; //for the U and N shape links

		public float LinkWidth { get; set; }

		public Rectangle CalculatedBounds { get; private set; }

		protected bool iconOnlyDraw;

		private const int circlePull = 100;
		private static CustomLineCap arrowCap = new AdjustableArrowCap(4,3);

		public override Point Location //link elements are always considered to be located at 0,0 graph to simplify things, with their connection points being in graph-coordinates (no need to do any local transforms)
		{
			get { return new Point(); }
			set { }
		}
		public override int X { get { return 0; } set { } }
		public override int Y { get { return 0; } set { } }

		public BaseLinkElement(ProductionGraphViewer graphViewer) : base(graphViewer)
		{
			LinkWidth = 3f;
		}
		protected BaseLinkElement(ProductionGraphViewer graphViewer, BaseLinkElement masterLink) : base(graphViewer, masterLink) { LinkWidth = masterLink.Width; }

		public override void UpdateVisibility(Rectangle graph_zone, int xborder, int yborder)
		{
			//NOTE: link element works in graph coordinates throughout (since Location is 0,0 for it - and it is always owned directly by the graph viewer). So we dont have to bother with graph to local conversions
			UpdateCurve();
			Visible =
					 	CalculatedBounds.X + CalculatedBounds.Width > graph_zone.X - xborder &&
						CalculatedBounds.X < graph_zone.X + graph_zone.Width + xborder &&
						CalculatedBounds.Y + CalculatedBounds.Height > graph_zone.Y - yborder &&
						CalculatedBounds.Y < graph_zone.Y + graph_zone.Height + yborder;
		}

		protected abstract Tuple<Point, Point> GetCurveEndpoints(); //supplier,consumer
		protected abstract Tuple<NodeDirection, NodeDirection> GetEndpointDirections(); //supplier,consumer

		protected void UpdateCurve() //updates all points & boundaries (important for occluding objects outside view)
		{
			Tuple<Point,Point> endpoints = GetCurveEndpoints();
			Tuple<NodeDirection, NodeDirection> endpointDirections = GetEndpointDirections();

			if (endpoints == null || endpointDirections == null)
				return;

			if (supplierOrigin != endpoints.Item1|| consumerOrigin != endpoints.Item2 || supplierDirection != endpointDirections.Item1 || consumerDirection != endpointDirections.Item2)
			{
				supplierOrigin = endpoints.Item1;
				supplierDirection = endpointDirections.Item1;
				consumerOrigin = endpoints.Item2;
				consumerDirection = endpointDirections.Item2;

				Type = (supplierDirection != consumerDirection) ? LineType.UShape :
					((supplierDirection == NodeDirection.Up && consumerOrigin.Y > supplierOrigin.Y) || (supplierDirection == NodeDirection.Down && consumerOrigin.Y < supplierOrigin.Y)) ? LineType.NShape : LineType.Simple;

				switch(Type)
				{
					case LineType.Simple: //supplier and consumer directions are same, link direction is regular (consumer is below supplier if direction is up, and above supplier if direction is down)
						if (supplierDirection == NodeDirection.Up)
						{
							supplierPull = new Point(supplierOrigin.X, supplierOrigin.Y -  Math.Max((int)((supplierOrigin.Y - consumerOrigin.Y) / 2), 20));
							consumerPull = new Point(consumerOrigin.X, consumerOrigin.Y + Math.Max((int)((supplierOrigin.Y - consumerOrigin.Y) / 2), 20));
						}
						else
						{
							supplierPull = new Point(supplierOrigin.X, supplierOrigin.Y + Math.Max((int)((consumerOrigin.Y - supplierOrigin.Y) / 2), 20));
							consumerPull = new Point(consumerOrigin.X, consumerOrigin.Y - Math.Max((int)((consumerOrigin.Y - supplierOrigin.Y) / 2), 20));
						}

						CalculatedBounds = new Rectangle(
							Math.Min(supplierOrigin.X, consumerOrigin.X),
							Math.Min(supplierOrigin.Y, consumerOrigin.Y),
							Math.Abs(supplierOrigin.X - consumerOrigin.X),
							Math.Abs(supplierOrigin.Y - consumerOrigin.Y));

						break;
					case LineType.UShape: //supplier and consumer directions are different

						int xOffset = Math.Min(circlePull * 2, Math.Abs(consumerOrigin.X - supplierOrigin.X)) * Math.Sign(consumerOrigin.X - supplierOrigin.X) / 2;
						if(supplierDirection == NodeDirection.Up)
						{
							midUA = new Point(supplierOrigin.X, Math.Min(supplierOrigin.Y, consumerOrigin.Y));
							midUB = new Point(midUA.X + xOffset, midUA.Y - circlePull);
							midUD = new Point(consumerOrigin.X, midUA.Y);
							midUC = new Point(midUD.X - xOffset, midUB.Y);

							pullU1 = new Point(supplierOrigin.X, midUA.Y - (circlePull / 2));
							pullU2 = new Point(supplierOrigin.X + (xOffset / 2), midUB.Y);
							pullU3 = new Point(consumerOrigin.X - (xOffset / 2), midUB.Y);
							pullU4 = new Point(consumerOrigin.X, midUD.Y - (circlePull / 2));
						}
						else
						{
							midUA = new Point(supplierOrigin.X, Math.Max(supplierOrigin.Y, consumerOrigin.Y));
							midUB = new Point(midUA.X + xOffset, midUA.Y + circlePull);
							midUD = new Point(consumerOrigin.X, midUA.Y);
							midUC = new Point(midUD.X - xOffset, midUB.Y);

							pullU1 = new Point(supplierOrigin.X, midUA.Y + (circlePull / 2));
							pullU2 = new Point(supplierOrigin.X + (xOffset / 2), midUB.Y);
							pullU3 = new Point(consumerOrigin.X - (xOffset / 2), midUB.Y);
							pullU4 = new Point(consumerOrigin.X, midUD.Y + (circlePull / 2));
						}

						CalculatedBounds = new Rectangle(
							Math.Min(supplierOrigin.X, consumerOrigin.X),
							Math.Min(supplierOrigin.Y, consumerOrigin.Y) - (supplierDirection == NodeDirection.Up? circlePull : 0),
							Math.Abs(supplierOrigin.X - consumerOrigin.X),
							Math.Abs(supplierOrigin.Y - consumerOrigin.Y) + circlePull);
						break;
					case LineType.NShape: //supplier and consumer directions are same, but the link direction is wrong (consumer is above supplier if direction is up, and below supplier if direction is down)
						int midX = Math.Abs(supplierOrigin.X - consumerOrigin.X) > 2 * circlePull ? (supplierOrigin.X + consumerOrigin.X) / 2 : supplierOrigin.X > consumerOrigin.X ? supplierOrigin.X + (int)(circlePull * 1.5) : supplierOrigin.X - (int)(circlePull * 1.5);
						int xOffsetA = Math.Min(circlePull * 2, Math.Abs(supplierOrigin.X - midX)) * Math.Sign(midX - supplierOrigin.X) / 2;
						int xOffsetB = Math.Min(circlePull * 2, Math.Abs(midX - consumerOrigin.X)) * Math.Sign(consumerOrigin.X - midX) / 2;

						midNC = new Point(midX, supplierOrigin.Y);
						midND = new Point(midX, consumerOrigin.Y);

						if(supplierDirection == NodeDirection.Up)
						{
							midNA = new Point(supplierOrigin.X + xOffsetA, supplierOrigin.Y - circlePull);
							midNB = new Point(midNC.X - xOffsetA, midNA.Y);

							midNE = new Point(midND.X + xOffsetB, consumerOrigin.Y + circlePull);
							midNF = new Point(consumerOrigin.X - xOffsetB, midNE.Y);

							pullN1 = new Point(supplierOrigin.X, supplierOrigin.Y - (circlePull / 2));
							pullN2 = new Point(supplierOrigin.X + (xOffsetA / 2), midNA.Y);
							pullN3 = new Point(midNC.X - (xOffsetA / 2), midNA.Y);
							pullN4 = new Point(midNC.X, pullN1.Y);
							pullN5 = new Point(midNC.X, consumerOrigin.Y + (circlePull / 2));
							pullN6 = new Point(midNC.X + (xOffsetB / 2), midNE.Y);
							pullN7 = new Point(consumerOrigin.X - (xOffsetB / 2), midNE.Y);
							pullN8 = new Point(consumerOrigin.X, pullN5.Y);
						}
						else
						{
							midNA = new Point(supplierOrigin.X + xOffsetA, supplierOrigin.Y + circlePull);
							midNB = new Point(midNC.X - xOffsetA, midNA.Y);

							midNE = new Point(midND.X + xOffsetB, consumerOrigin.Y - circlePull);
							midNF = new Point(consumerOrigin.X - xOffsetB, midNE.Y);

							pullN1 = new Point(supplierOrigin.X, supplierOrigin.Y + (circlePull / 2));
							pullN2 = new Point(supplierOrigin.X + (xOffsetA / 2), midNA.Y);
							pullN3 = new Point(midNC.X - (xOffsetA / 2), midNA.Y);
							pullN4 = new Point(midNC.X, pullN1.Y);
							pullN5 = new Point(midNC.X, consumerOrigin.Y - (circlePull / 2));
							pullN6 = new Point(midNC.X + (xOffsetB / 2), midNE.Y);
							pullN7 = new Point(consumerOrigin.X - (xOffsetB / 2), midNE.Y);
							pullN8 = new Point(consumerOrigin.X, pullN5.Y);
						}

						CalculatedBounds = new Rectangle(
							Math.Min(Math.Min(midX, supplierOrigin.X), consumerOrigin.X),
							Math.Min(supplierOrigin.Y, consumerOrigin.Y) - circlePull,
							Math.Max(Math.Max(midX, supplierOrigin.X), consumerOrigin.X) - Math.Min(Math.Min(midX, supplierOrigin.X), consumerOrigin.X),
							Math.Abs(supplierOrigin.Y - consumerOrigin.Y) + (2 * circlePull));
						break;
				}
			}
		}
		public override bool ContainsPoint(Point graph_point)
		{
			return false;
		}

		//a link is traced whenever either node it joins is selected. the viewer redraws these in a pass after everything
		//else, so a selected gutter's connections can be followed across a crowded graph instead of vanishing into it
		public bool IsTraced { get { return SupplierElement != null && ConsumerElement != null && (SupplierElement.Highlighted || ConsumerElement.Highlighted); } }

		//the core keeps a fixed share of the halo so the item colour stays readable at any highlight size
		private static float TraceHaloWidth { get { return Math.Max(2, Properties.Settings.Default.LinkTraceWidth); } }
		private static float TraceCoreWidth { get { return TraceHaloWidth * 0.43f; } }
		private static readonly Color traceHaloColor = Color.FromArgb(190, 20, 20, 25);

		public void DrawTrace(Graphics graphics)
		{
			UpdateCurve();

			//widths are divided by the view scale so the trace keeps a constant thickness on screen - zoomed out far
			//enough to need tracing is exactly where a graph-space width would shrink to nothing
			float scale = (float)Math.Max(0.05, graphViewer.ViewScale);

			using (Pen halo = new Pen(traceHaloColor, LinkWidth + (TraceHaloWidth / scale)) { EndCap = LineCap.Round, StartCap = LineCap.Round })
				DrawCurve(graphics, halo);
			using (Pen core = new Pen(Item.Item.AverageColor, LinkWidth + (TraceCoreWidth / scale)) { EndCap = LineCap.Round, StartCap = LineCap.Round })
				DrawCurve(graphics, core);
		}

		//set by the viewer's find feature when this link's item matches the search. drawn in its own pass on top of
		//the graph, same as tracing, so a matched link is not buried under whatever gets painted after it
		public bool FindHighlighted { get; set; }
		private static readonly Color findHaloColor = Color.FromArgb(210, 255, 60, 60);

		//same halo-then-core construction as DrawTrace, but in the find colour - the core keeps the item colour so a
		//highlighted link still reads as the item it carries
		public void DrawFindHighlight(Graphics graphics)
		{
			UpdateCurve();

			float scale = (float)Math.Max(0.05, graphViewer.ViewScale);

			using (Pen halo = new Pen(findHaloColor, LinkWidth + (TraceHaloWidth / scale)) { EndCap = LineCap.Round, StartCap = LineCap.Round })
				DrawCurve(graphics, halo);
			using (Pen core = new Pen(Item.Item.AverageColor, LinkWidth + (TraceCoreWidth / scale)) { EndCap = LineCap.Round, StartCap = LineCap.Round })
				DrawCurve(graphics, core);
		}

		private const int UtilityStubLength = 14;
		private const int UtilityStubDotRadius = 3;

		//a hidden utility link keeps a mark at each end so you can still see the connection exists and what it carries -
		//what is given up is only the line between them, which is the part that was crossing the whole graph
		private void DrawUtilityStubs(Graphics graphics)
		{
			Tuple<Point, Point> endpoints = GetCurveEndpoints();
			if (endpoints == null)
				return;

			using (Pen pen = new Pen(Item.Item.AverageColor, LinkWidth) { EndCap = LineCap.Round, StartCap = LineCap.Round })
			using (Brush dot = new SolidBrush(Item.Item.AverageColor))
			{
				DrawUtilityStub(graphics, pen, dot, endpoints.Item1, endpoints.Item2);
				DrawUtilityStub(graphics, pen, dot, endpoints.Item2, endpoints.Item1);
			}
		}

		//the stub points the way the link would have gone, so a node with several hidden links still shows them fanning
		//towards their different destinations rather than collapsing into one mark
		private void DrawUtilityStub(Graphics graphics, Pen pen, Brush dot, Point from, Point towards)
		{
			double dx = towards.X - from.X;
			double dy = towards.Y - from.Y;
			double distance = Math.Sqrt((dx * dx) + (dy * dy));
			if (distance < 1)
				return;

			int endX = from.X + (int)(dx / distance * UtilityStubLength);
			int endY = from.Y + (int)(dy / distance * UtilityStubLength);
			graphics.DrawLine(pen, from.X, from.Y, endX, endY);
			graphics.FillEllipse(dot, endX - UtilityStubDotRadius, endY - UtilityStubDotRadius, UtilityStubDotRadius * 2, UtilityStubDotRadius * 2);
		}

		protected override void Draw(Graphics graphics, NodeDrawingStyle style)
		{
			iconOnlyDraw = (style == NodeDrawingStyle.IconsOnly);
			UpdateCurve();

			//selecting either end brings a hidden link back in full - the same condition the trace pass uses
			if (!iconOnlyDraw && !IsTraced && !graphViewer.IgnoreUtilityHiding && graphViewer.Graph.IsUtilityItemHidden(Item))
			{
				DrawUtilityStubs(graphics);
				return;
			}

			using (Pen pen = new Pen(Item.Item.AverageColor, LinkWidth) { EndCap = System.Drawing.Drawing2D.LineCap.Round, StartCap = System.Drawing.Drawing2D.LineCap.Round })
			{
				if (graphViewer.ArrowsOnLinks && !graphViewer.DynamicLinkWidth && !iconOnlyDraw)
					pen.CustomEndCap = arrowCap;

				DrawCurve(graphics, pen);
			}
		}

		private void DrawCurve(Graphics graphics, Pen pen)
		{
			{
				switch(Type)
				{
					case LineType.Simple:
						graphics.DrawBeziers(pen, new Point[]
						{
							supplierOrigin,
							supplierPull,

							consumerPull,
							consumerOrigin
						});
						break;
					case LineType.UShape:
						graphics.DrawBeziers(pen, new Point[]
						{
							supplierOrigin,
							supplierOrigin,

							midUA,
							midUA,
							pullU1,

							pullU2,
							midUB,
							midUB,

							midUC,
							midUC,
							pullU3,

							pullU4,
							midUD,
							midUD,

							consumerOrigin,
							consumerOrigin
						});
						break;
					case LineType.NShape:
						graphics.DrawBeziers(pen, new Point[]
						{
							supplierOrigin,
							pullN1,

							pullN2,
							midNA,
							midNA,

							midNB,
							midNB,
							pullN3,

							pullN4,
							midNC,
							midNC,
							
							midND,
							midND,
							pullN5,

							pullN6,
							midNE,
							midNE,

							midNF,
							midNF,
							pullN7,

							pullN8,
							consumerOrigin
						}); ;
						break;
				}
			}
		}
	}
}
