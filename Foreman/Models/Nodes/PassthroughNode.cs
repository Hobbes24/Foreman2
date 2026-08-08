using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Serialization;

namespace Foreman
{
	//a gutter is axis aligned by construction - that is what keeps every link to it a short perpendicular stub
	public enum GutterOrientation { Vertical, Horizontal }

	public class PassthroughNode : BaseNode
	{
        public enum Errors
        {
            Clean = 0b_0000_0000_0000,
            InvalidLinks = 0b_1000_0000_0000
        }

        public Errors ErrorSet { get; private set; }

        private readonly BaseNodeController controller;
		public override BaseNodeController Controller { get { return controller; } }

		public readonly ItemQualityPair PassthroughItem;

		public override IEnumerable<ItemQualityPair> Inputs { get { yield return PassthroughItem; } }
		public override IEnumerable<ItemQualityPair> Outputs { get { yield return PassthroughItem; } }

		public bool SimpleDraw;

		//a gutter is an ordinary passthrough node drawn as a long line, with its links attaching at points along that line.
		//the rate math is unchanged - the node still balances everything in against everything out - so the solver never sees a gutter at all.
		//length is measured in graph units along GutterOrientation, centered on Location. zero means this is a plain passthrough node.
		public int GutterLength;
		public GutterOrientation GutterOrientation;

		public bool IsGutter { get { return GutterLength > 0; } }

		//manual placement of individual links along a gutter, keyed by the far node's id and measured along the gutter
		//axis from its centre. a link with no entry places itself by projection, so only the ones you drag get pinned
		public readonly Dictionary<int, int> GutterLinkOffsets = new Dictionary<int, int>();

		public PassthroughNode(ProductionGraph graph, int nodeID, ItemQualityPair item) : base(graph, nodeID)
		{
			PassthroughItem = item;
			SimpleDraw = true;
			GutterLength = 0;
			GutterOrientation = GutterOrientation.Vertical;
			controller = PassthroughNodeController.GetController(this);
			ReadOnlyNode = new ReadOnlyPassthroughNode(this);
		}

        internal override NodeState GetUpdatedState()
        {
            ErrorSet = Errors.Clean;

            if (!AllLinksValid)
                ErrorSet |= Errors.InvalidLinks;

            if (ErrorSet != Errors.Clean)
                return NodeState.Error;

            if (AllLinksConnected)
                return NodeState.Clean;
            return NodeState.MissingLink;
        }

        public override double GetConsumeRate(ItemQualityPair item) { return ActualRate; }
		public override double GetSupplyRate(ItemQualityPair item) { return ActualRate; }

		internal override double inputRateFor(ItemQualityPair item) { return 1; }
		internal override double outputRateFor(ItemQualityPair item) { return 1; }

		public override void GetObjectData(SerializationInfo info, StreamingContext context)
		{
			base.GetObjectData(info, context);

            info.AddValue("NodeType", NodeType.Passthrough);
            info.AddValue("Item", PassthroughItem.Item.Name);
            info.AddValue("BaseQuality", PassthroughItem.Quality.Name);
            if (RateType == RateType.Manual)
                info.AddValue("DesiredRate", DesiredRatePerSec);
			info.AddValue("SDraw", SimpleDraw);
			//only written for actual gutters - a missing GLength reads back as zero, which is exactly "not a gutter",
			//so old saves load correctly without a version migration
			if (IsGutter)
			{
				info.AddValue("GLength", GutterLength);
				info.AddValue("GOrient", (int)GutterOrientation);

				//written as two parallel arrays built from one pass, so the pairs cannot drift apart.
				//the ids are this session's, and the loader remaps them the same way it remaps links
				if (GutterLinkOffsets.Count > 0)
				{
					int[] ids = new int[GutterLinkOffsets.Count];
					int[] offsets = new int[GutterLinkOffsets.Count];
					int i = 0;
					foreach (KeyValuePair<int, int> pinned in GutterLinkOffsets)
					{
						ids[i] = pinned.Key;
						offsets[i] = pinned.Value;
						i++;
					}
					info.AddValue("GLinkIDs", ids);
					info.AddValue("GLinkOffs", offsets);
				}
			}
		}

        public override string ToString() { return string.Format("Supply node for: {0} ({1})", PassthroughItem.Item.Name, PassthroughItem.Quality.Name); }
    }

    public class ReadOnlyPassthroughNode : ReadOnlyBaseNode
	{
		public ItemQualityPair PassthroughItem => MyNode.PassthroughItem;

		private readonly PassthroughNode MyNode;

		public ReadOnlyPassthroughNode(PassthroughNode node) : base(node) { MyNode = node; }

		public bool SimpleDraw => MyNode.SimpleDraw;

		public int GutterLength => MyNode.GutterLength;
		public GutterOrientation GutterOrientation => MyNode.GutterOrientation;
		public bool IsGutter => MyNode.IsGutter;

		public bool TryGetGutterLinkOffset(int farNodeID, out int offset) { return MyNode.GutterLinkOffsets.TryGetValue(farNodeID, out offset); }

        public override List<string> GetErrors()
        {
            List<string> errors = new List<string>();
            if ((MyNode.ErrorSet & PassthroughNode.Errors.InvalidLinks) != 0)
                errors.Add("> Some links are invalid!");
            return errors;
        }

        public override List<string> GetWarnings() { Trace.Fail("Passthrough node never has the warning state!"); return null; }
	}

	public class PassthroughNodeController : BaseNodeController
	{
		private readonly PassthroughNode MyNode;

		protected PassthroughNodeController(PassthroughNode myNode) : base(myNode) { MyNode = myNode; }

		public static PassthroughNodeController GetController(PassthroughNode node)
		{
			if (node.Controller != null)
				return (PassthroughNodeController)node.Controller;
			return new PassthroughNodeController(node);
		}

		public void SetSimpleDraw(bool alwaysRegularDraw) { MyNode.SimpleDraw = alwaysRegularDraw; }

		public void SetGutter(int length, GutterOrientation orientation)
		{
			MyNode.GutterLength = ClampLength(length);
			MyNode.GutterOrientation = orientation;
		}

		public void SetGutterLength(int length) { MyNode.GutterLength = ClampLength(length); }

		//pinned link positions are meaningless once this stops being a gutter
		public void ClearGutter() { MyNode.GutterLength = 0; MyNode.GutterLinkOffsets.Clear(); }

		public void SetGutterLinkOffset(int farNodeID, int offset) { MyNode.GutterLinkOffsets[farNodeID] = offset; }

		//drops a link back to placing itself by projection
		public void ClearGutterLinkOffset(int farNodeID) { MyNode.GutterLinkOffsets.Remove(farNodeID); }

		//clamped here as well as at the call sites, so no future caller can leave a gutter absurdly long
		private static int ClampLength(int length)
		{
			if (length <= 0)
				return 0;
			return Math.Min(ProductionGraphViewer.MaximumGutterLength, Math.Max(ProductionGraphViewer.MinimumGutterLength, length));
		}

        public override Dictionary<string, Action> GetErrorResolutions()
        {
            Dictionary<string, Action> resolutions = new Dictionary<string, Action>();
            if (MyNode.ErrorSet != PassthroughNode.Errors.Clean)
                resolutions.Add("Delete node", new Action(() => this.Delete()));
            else
                foreach (KeyValuePair<string, Action> kvp in GetInvalidConnectionResolutions())
                    resolutions.Add(kvp.Key, kvp.Value);
            return resolutions;
        }

        public override Dictionary<string, Action> GetWarningResolutions() { Trace.Fail("Passthrough node never has the warning state!"); return null; }
	}
}
