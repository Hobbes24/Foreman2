using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Diagnostics;

namespace Foreman
{
	public class LinkElement : BaseLinkElement
	{
		public ReadOnlyNodeLink DisplayedLink { get; private set; }
		public override ItemQualityPair Item { get { return DisplayedLink.Item; } protected set { } }

		public ItemTabElement SupplierTab { get; protected set; }
		public ItemTabElement ConsumerTab { get; protected set; }

		public LinkElement(ProductionGraphViewer graphViewer, ReadOnlyNodeLink displayedLink, BaseNodeElement supplierElement, BaseNodeElement consumerElement) : base(graphViewer)
		{
			if (supplierElement == null || consumerElement == null)
				Trace.Fail("Link element being created with one of the connected elements being null!");

			DisplayedLink = displayedLink;
			SupplierElement = supplierElement;
			ConsumerElement = consumerElement;
			SupplierTab = supplierElement.GetOutputLineItemTab(Item);
			ConsumerTab = consumerElement.GetInputLineItemTab(Item);

			if (SupplierTab == null || ConsumerTab == null)
				Trace.Fail(string.Format("Link element being created with one of the elements ({0}, {1}) not having the required item ({2})!", supplierElement, consumerElement, Item));

			LinkWidth = 3f;
			UpdateCurve();
		}

		protected override Tuple<Point,Point> GetCurveEndpoints()
		{
			if (iconOnlyDraw)
				return new Tuple<Point, Point>(SupplierElement.Location, ConsumerElement.Location);

			//each end gets a chance to move its own endpoint (gutters attach per link rather than at a single tab).
			//both calls use the far node's Location as their reference, never the other end's computed point, so this cannot recurse
			Point supplierPoint = SupplierElement.GetLinkAttachPoint(DisplayedLink, LinkType.Output, SupplierTab.GetConnectionPoint(), ConsumerElement.Location);
			Point consumerPoint = ConsumerElement.GetLinkAttachPoint(DisplayedLink, LinkType.Input, ConsumerTab.GetConnectionPoint(), SupplierElement.Location);
			return new Tuple<Point, Point>(supplierPoint, consumerPoint);
		}
		protected override Tuple<NodeDirection, NodeDirection> GetEndpointDirections()
		{
			return new Tuple<NodeDirection, NodeDirection>(SupplierElement.DisplayedNode.NodeDirection, ConsumerElement.DisplayedNode.NodeDirection);
		}
	}
}
