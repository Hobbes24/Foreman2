using System;
using System.Windows.Forms;

namespace Foreman
{
	public partial class EditFlowPanel : UserControl
	{
		private readonly ProductionGraphViewer myGraphViewer;
		private readonly BaseNodeController nodeController;
		private readonly ReadOnlyBaseNode nodeData;

		public EditFlowPanel(ReadOnlyBaseNode node, ProductionGraphViewer graphViewer)
		{
			InitializeComponent();
			SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

			nodeData = node;
			nodeController = graphViewer.Graph.RequestNodeController(node);
			myGraphViewer = graphViewer;

			RateLabel.Text = node.SetValueDescription;
			FixedFlowInput.Maximum = (decimal)(node.MaxDesiredSetValue * graphViewer.Graph.GetRateMultipler());

			if(node is ReadOnlyPassthroughNode pNode)
			{
				SimplePassthroughNodesCheckBox.Checked = pNode.SimpleDraw;
				SimplePassthroughNodesCheckBox.Visible = true;
			}

			KeyNodeCheckBox.Checked = nodeData.KeyNode;
			KeyNodeTitleLabel.Visible = nodeData.KeyNode;
			KeyNodeTitleInput.Visible = nodeData.KeyNode;
			KeyNodeTitleInput.Text = nodeData.KeyNodeTitle;

			InitializeRates();

			SimplePassthroughNodesCheckBox.CheckedChanged += SimplePassthroughNodesCheckBox_CheckedChanged;
			KeyNodeCheckBox.CheckedChanged += KeyNodeCheckBox_CheckedChanged;
			KeyNodeTitleInput.TextChanged += KeyNodeTitleInput_TextChanged;
		}

		private void InitializeRates()
		{
			if (nodeData.RateType == RateType.Auto)
			{
				AutoOption.Checked = true;
				FixedFlowInput.Enabled = false;
				FixedFlowInput.Value = Math.Min(FixedFlowInput.Maximum, (decimal)nodeData.ActualSetValue);
			}
			else
			{
				FixedOption.Checked = true;
				FixedFlowInput.Enabled = true;
				FixedFlowInput.Value = Math.Min(FixedFlowInput.Maximum, (decimal)nodeData.DesiredSetValue);
			}
			UpdateFixedFlowInputDecimals(FixedFlowInput);
		}

		private void SetFixedRate()
		{
			if (nodeData.DesiredSetValue != (double)FixedFlowInput.Value)
			{
				nodeController.SetDesiredSetValue((double)FixedFlowInput.Value);
				myGraphViewer.Graph.UpdateNodeValues();
			}
			UpdateFixedFlowInputDecimals(FixedFlowInput);
		}

		//NumericUpDown re-parses its own text back into Value when the box is committed, so DecimalPlaces is not just a
		//display setting - anything past it is rounded away for good. At 4 a flowrate below 0.0001 was read back as 0
		//and written to the node, which is why small Pyanodons rates would not survive being looked at. Only as many
		//places as the value actually uses are shown, so ordinary rates still read as "100" rather than "100.00000000".
		private const int MaxRateDecimals = 8;

		private void UpdateFixedFlowInputDecimals(NumericUpDown nud)
		{
			int decimals = MathDecimals.GetDecimals(nud.Value);
			decimals = Math.Min(decimals, MaxRateDecimals);
			nud.DecimalPlaces = decimals;
		}

		private void FixedOption_CheckChanged(object sender, EventArgs e)
		{
			FixedFlowInput.Enabled = FixedOption.Checked;
			RateType updatedRateType = (FixedOption.Checked) ? RateType.Manual : RateType.Auto;

			if (nodeData.RateType != updatedRateType)
			{
				nodeController.SetRateType(updatedRateType);
				myGraphViewer.Graph.UpdateNodeValues();
			}
		}

		private void FixedFlowInput_ValueChanged(object sender, EventArgs e)
		{
			SetFixedRate();
		}

		private void SimplePassthroughNodesCheckBox_CheckedChanged(object sender, EventArgs e)
		{
			(nodeController as PassthroughNodeController).SetSimpleDraw(SimplePassthroughNodesCheckBox.Checked);
			myGraphViewer.Invalidate();
		}

		private void KeyNodeCheckBox_CheckedChanged(object sender, EventArgs e)
		{
			nodeController.SetKeyNode(KeyNodeCheckBox.Checked);
			KeyNodeTitleLabel.Visible = nodeData.KeyNode;
			KeyNodeTitleInput.Visible = nodeData.KeyNode;
			KeyNodeTitleInput.Text = nodeData.KeyNodeTitle;
			myGraphViewer.Invalidate();
		}

		private void KeyNodeTitleInput_TextChanged(object sender, EventArgs e)
		{
			nodeController.SetKeyNodeTitle(KeyNodeTitleInput.Text);
		}
	}
}
