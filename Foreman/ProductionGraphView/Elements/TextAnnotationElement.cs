using System;
using System.Drawing;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace Foreman
{
    /// <summary>
    /// A freehand text label annotation drawn on the canvas.
    /// The text is drawn centred inside the element bounds.
    /// </summary>
    public class TextAnnotationElement : AnnotationElement
    {
        // ----------------------------------------------------------------
        // Default dimensions for newly created text labels
        // ----------------------------------------------------------------

        private const int DefaultWidth = 200;
        private const int DefaultHeight = 60;

        // ----------------------------------------------------------------
        // Appearance properties
        // ----------------------------------------------------------------

        public string Text { get; set; }
        public Font TextFont { get; set; }
        public Color TextColor { get; set; }
        public Color BackColor { get; set; }

        // ----------------------------------------------------------------
        // GDI resources
        // ----------------------------------------------------------------

        private SolidBrush _textBrush;
        private SolidBrush _backBrush; // null when BackColor is fully transparent

        private static readonly StringFormat CenteredFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };

        // ----------------------------------------------------------------
        // Construction
        // ----------------------------------------------------------------

        /// <summary>Creates a new text label at the given graph-space location with defaults.</summary>
        public TextAnnotationElement(ProductionGraphViewer graphViewer, Point graphLocation)
            : base(graphViewer, graphLocation, DefaultWidth, DefaultHeight)
        {
            Text = "Label";
            TextFont = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Point);
            TextColor = Color.Black;
            BackColor = Color.Transparent;

            RebuildGdiObjects();
        }

        /// <summary>Private constructor used by FromJson.</summary>
        private TextAnnotationElement(ProductionGraphViewer graphViewer,
                                      Point location, Size size,
                                      string text, Font textFont,
                                      Color textColor, Color backColor)
            : base(graphViewer, location, size.Width, size.Height)
        {
            Text = text;
            TextFont = textFont;
            TextColor = textColor;
            BackColor = backColor;

            RebuildGdiObjects();
        }

        /// <summary>
        /// Recreates all GDI objects from current property values.
        /// Must be called after changing any appearance property, and by
        /// TextPropertiesForm when the user clicks OK.
        /// </summary>
        public void RebuildGdiObjects()
        {
            _textBrush?.Dispose();
            _backBrush?.Dispose();

            _textBrush = new SolidBrush(TextColor);
            _backBrush = (BackColor.A > 0) ? new SolidBrush(BackColor) : null;
        }

        // ----------------------------------------------------------------
        // Drawing
        // ----------------------------------------------------------------

        protected override void Draw(Graphics graphics, NodeDrawingStyle style)
        {
            Rectangle r = GetGraphRect();

            // 1. Selection highlight (behind everything)
            DrawSelectionHighlight(graphics, r);

            // 2. Background fill (only when not transparent)
            if (_backBrush != null)
                graphics.FillRectangle(_backBrush, r);

            // 3. Text, centred and clipped with ellipsis
            if (!string.IsNullOrEmpty(Text))
                graphics.DrawString(Text, TextFont, _textBrush, (RectangleF)r, CenteredFormat);

            // 4. Resize handles (drawn on top when selected)
            DrawResizeHandles(graphics);
        }

        // ----------------------------------------------------------------
        // Properties dialog (Stage 3)
        // ----------------------------------------------------------------

        public override void ShowPropertiesDialog()
        {
            using (TextPropertiesForm form = new TextPropertiesForm(this))
            {
                form.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
                if (form.ShowDialog(graphViewer.ParentForm) == System.Windows.Forms.DialogResult.OK)
                {
                    RebuildGdiObjects();
                    graphViewer.Invalidate();
                }
            }
        }

        // ----------------------------------------------------------------
        // Serialization
        // ----------------------------------------------------------------

        public override JObject ToJson()
        {
            JObject json = BaseJson("Text", this);
            json["Text"] = Text;
            json["FontFamily"] = TextFont.FontFamily.Name;
            json["FontSize"] = TextFont.SizeInPoints;
            json["FontStyle"] = (int)TextFont.Style;
            json["TextColor"] = ColorToJson(TextColor);
            json["BackColor"] = ColorToJson(BackColor);
            return json;
        }

        public static new TextAnnotationElement FromJson(JObject json, ProductionGraphViewer graphViewer)
        {
            Point loc = LocationFromJson(json);
            Size sz = SizeFromJson(json);
            string text = (string)json["Text"];
            string family = (string)json["FontFamily"];
            float size = (float)json["FontSize"];
            FontStyle style = (FontStyle)(int)json["FontStyle"];
            Color textColor = ColorFromJson(json["TextColor"]);
            Color backColor = ColorFromJson(json["BackColor"]);

            Font font;
            try { font = new Font(family, size, style, GraphicsUnit.Point); }
            catch { font = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Point); }

            return new TextAnnotationElement(graphViewer, loc, sz, text, font, textColor, backColor);
        }

        // ----------------------------------------------------------------
        // Cleanup
        // ----------------------------------------------------------------

        public override void Dispose()
        {
            _textBrush?.Dispose();
            _backBrush?.Dispose();
            TextFont?.Dispose();
            base.Dispose();
        }
    }
}