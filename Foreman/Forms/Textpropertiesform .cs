using System;
using System.Drawing;
using System.Windows.Forms;

namespace Foreman
{
    public partial class TextPropertiesForm : Form
    {
        private readonly TextAnnotationElement _element;
        private readonly ProductionGraphViewer _graphViewer;

        // Snapshot for Cancel restore
        private readonly string _originalText;
        private readonly Font _originalFont;
        private readonly Color _originalTextColor;
        private readonly Color _originalBackColor;
        private readonly System.Drawing.StringAlignment _originalTextAlign;

        // Working font copy — disposed on close if not transferred to element
        private Font _workingFont;

        public TextPropertiesForm(TextAnnotationElement element)
        {
            InitializeComponent();

            _element = element;
            _graphViewer = element.GraphViewer;

            // Save snapshot
            _originalText = element.Text;
            _originalFont = new Font(element.TextFont, element.TextFont.Style);
            _originalTextColor = element.TextColor;
            _originalBackColor = element.BackColor;
            _originalTextAlign = element.TextAlign;

            // Working font clone
            _workingFont = new Font(element.TextFont, element.TextFont.Style);

            // Initialise controls
            TextInput.Text = element.Text;

            // Unhook TextChanged during init to avoid firing before element is ready
            TextInput.TextChanged -= TextInput_TextChanged;
            TextInput.Text = element.Text;
            TextInput.TextChanged += TextInput_TextChanged;

            UpdateFontLabel();
            UpdateAlignRadios();
            UpdateTextColorButton();
            UpdateBackColorButton();

            TransparentCheckBox.CheckedChanged -= TransparentCheckBox_CheckedChanged;
            TransparentCheckBox.Checked = (element.BackColor.A == 0);
            TransparentCheckBox.CheckedChanged += TransparentCheckBox_CheckedChanged;
            BackColorButton.Enabled = !TransparentCheckBox.Checked;
            this.Shown += (s, e) => { TextInput.Focus(); TextInput.SelectAll(); };
        }

        // ----------------------------------------------------------------
        // Text — live update
        // ----------------------------------------------------------------

        private void TextInput_TextChanged(object sender, EventArgs e)
        {
            _element.Text = TextInput.Text;
            _element.AutoSizeToText();
            _graphViewer.Invalidate();
        }

        // ----------------------------------------------------------------
        // Font — live update
        // ----------------------------------------------------------------

        private void FontButton_Click(object sender, EventArgs e)
        {
            using (FontDialog dlg = new FontDialog())
            {
                dlg.Font = _workingFont;
                dlg.ShowEffects = true;
                dlg.ShowColor = false;
                dlg.FontMustExist = true;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _workingFont?.Dispose();
                    _workingFont = dlg.Font;

                    _element.TextFont?.Dispose();
                    _element.TextFont = new Font(dlg.Font, dlg.Font.Style);
                    _element.RebuildGdiObjects();
                    _element.AutoSizeToText();
                    _graphViewer.Invalidate();
                    UpdateFontLabel();
                }
            }
        }

        // ----------------------------------------------------------------
        // Text alignment — live update
        // ----------------------------------------------------------------

        private void UpdateAlignRadios()
        {
            AlignLeftRadio.CheckedChanged -= AlignRadio_CheckedChanged;
            AlignCenterRadio.CheckedChanged -= AlignRadio_CheckedChanged;
            AlignRightRadio.CheckedChanged -= AlignRadio_CheckedChanged;

            AlignLeftRadio.Checked   = _element.TextAlign == System.Drawing.StringAlignment.Near;
            AlignCenterRadio.Checked = _element.TextAlign == System.Drawing.StringAlignment.Center;
            AlignRightRadio.Checked  = _element.TextAlign == System.Drawing.StringAlignment.Far;

            AlignLeftRadio.CheckedChanged += AlignRadio_CheckedChanged;
            AlignCenterRadio.CheckedChanged += AlignRadio_CheckedChanged;
            AlignRightRadio.CheckedChanged += AlignRadio_CheckedChanged;
        }

        private void AlignRadio_CheckedChanged(object sender, EventArgs e)
        {
            if (AlignLeftRadio.Checked)
                _element.TextAlign = System.Drawing.StringAlignment.Near;
            else if (AlignCenterRadio.Checked)
                _element.TextAlign = System.Drawing.StringAlignment.Center;
            else if (AlignRightRadio.Checked)
                _element.TextAlign = System.Drawing.StringAlignment.Far;
            _element.RebuildGdiObjects();
            _graphViewer.Invalidate();
        }

        private void UpdateFontLabel()
        {
            FontPreviewLabel.Text = string.Format("{0}, {1}pt{2}{3}",
                _workingFont.FontFamily.Name,
                (int)_workingFont.SizeInPoints,
                _workingFont.Bold ? " Bold" : "",
                _workingFont.Italic ? " Italic" : "");
        }

        // ----------------------------------------------------------------
        // Text colour — live update
        // ----------------------------------------------------------------

        private void TextColorButton_Click(object sender, EventArgs e)
        {
            using (ColorDialog dlg = new ColorDialog())
            {
                dlg.Color = _element.TextColor;
                dlg.AnyColor = true;
                dlg.FullOpen = true;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _element.TextColor = dlg.Color;
                    _element.RebuildGdiObjects();
                    _graphViewer.Invalidate();
                    UpdateTextColorButton();
                }
            }
        }

        private void UpdateTextColorButton()
        {
            TextColorButton.BackColor = _element.TextColor;
            TextColorButton.ForeColor = (_element.TextColor.R + _element.TextColor.G + _element.TextColor.B > 382) ? Color.Black : Color.White;
        }

        // ----------------------------------------------------------------
        // Background colour — live update
        // ----------------------------------------------------------------

        private void BackColorButton_Click(object sender, EventArgs e)
        {
            using (ColorDialog dlg = new ColorDialog())
            {
                dlg.Color = Color.FromArgb(255, _element.BackColor.R, _element.BackColor.G, _element.BackColor.B);
                dlg.AnyColor = true;
                dlg.FullOpen = true;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _element.BackColor = dlg.Color;
                    _element.RebuildGdiObjects();
                    _graphViewer.Invalidate();
                    UpdateBackColorButton();
                }
            }
        }

        private void UpdateBackColorButton()
        {
            Color display = Color.FromArgb(255, _element.BackColor.R, _element.BackColor.G, _element.BackColor.B);
            BackColorButton.BackColor = display;
            BackColorButton.ForeColor = (display.R + display.G + display.B > 382) ? Color.Black : Color.White;
        }

        private void TransparentCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            BackColorButton.Enabled = !TransparentCheckBox.Checked;
            _element.BackColor = TransparentCheckBox.Checked
                ? Color.Transparent
                : Color.FromArgb(255, _element.BackColor.R, _element.BackColor.G, _element.BackColor.B);
            _element.RebuildGdiObjects();
            _graphViewer.Invalidate();
        }

        // ----------------------------------------------------------------
        // OK — changes already applied live, just close
        // ----------------------------------------------------------------

        private void OKButton_Click(object sender, EventArgs e)
        {
            // Persist this element's appearance as the defaults for the next Add Text
            TextAnnotationElement.SaveDefaults(_element);
            _workingFont?.Dispose();
            _workingFont = null;
            _originalFont?.Dispose();
            DialogResult = DialogResult.OK;
            Close();
        }

        // ----------------------------------------------------------------
        // Cancel — restore snapshot and close
        // ----------------------------------------------------------------

        private void CancelButton_Click(object sender, EventArgs e)
        {
            _element.Text = _originalText;
            _element.TextFont?.Dispose();
            _element.TextFont = _originalFont; // transfer ownership
            _element.TextColor = _originalTextColor;
            _element.BackColor = _originalBackColor;
            _element.TextAlign = _originalTextAlign;
            _element.RebuildGdiObjects();
            _graphViewer.Invalidate();

            _workingFont?.Dispose();
            _workingFont = null;

            DialogResult = DialogResult.Cancel;
            Close();
        }

        // ----------------------------------------------------------------
        // Cleanup
        // ----------------------------------------------------------------

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            _workingFont?.Dispose();
        }
    }
}