using System;
using System.Drawing;
using System.Runtime.InteropServices;
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
        public StringAlignment TextAlign { get; set; }

        // ----------------------------------------------------------------
        // GDI resources
        // ----------------------------------------------------------------

        private SolidBrush _textBrush;
        private SolidBrush _backBrush; // null when BackColor is fully transparent
        private StringFormat _textFormat;

        // ----------------------------------------------------------------
        // Last-used format defaults (cross-session persistent via Properties.Settings)
        // ----------------------------------------------------------------
        private static string _defaultFontFamily = Properties.Settings.Default.AnnotTextFontFamily;
        private static float _defaultFontSize = float.TryParse(Properties.Settings.Default.AnnotTextFontSize, out float _fs) ? _fs : 14f;
        private static FontStyle _defaultFontStyle = (FontStyle)Properties.Settings.Default.AnnotTextFontStyle;
        private static Color _defaultTextColor = Color.FromArgb(Properties.Settings.Default.AnnotTextColorARGB);
        private static Color _defaultBackColor = Color.FromArgb(Properties.Settings.Default.AnnotTextBackColorARGB);
        private static StringAlignment _defaultTextAlign = (StringAlignment)Properties.Settings.Default.AnnotTextAlign;

        /// <summary>
        /// Saves the current element's appearance as the new defaults for future Add Text operations,
        /// persisting them to user settings so they survive restarts.
        /// Called by TextPropertiesForm when the user clicks OK.
        /// </summary>
        public static void SaveDefaults(TextAnnotationElement element)
        {
            _defaultFontFamily = element.TextFont.FontFamily.Name;
            _defaultFontSize   = element.TextFont.SizeInPoints;
            _defaultFontStyle  = element.TextFont.Style;
            _defaultTextColor  = element.TextColor;
            _defaultBackColor  = element.BackColor;
            _defaultTextAlign  = element.TextAlign;

            var s = Properties.Settings.Default;
            s.AnnotTextFontFamily    = _defaultFontFamily;
            s.AnnotTextFontSize      = _defaultFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
            s.AnnotTextFontStyle     = (int)_defaultFontStyle;
            s.AnnotTextColorARGB     = _defaultTextColor.ToArgb();
            s.AnnotTextBackColorARGB = _defaultBackColor.ToArgb();
            s.AnnotTextAlign         = (int)_defaultTextAlign;
            s.Save();
        }

        // ----------------------------------------------------------------
        // Construction
        // ----------------------------------------------------------------

        /// <summary>Creates a new text label at the given graph-space location with defaults.</summary>
        public TextAnnotationElement(ProductionGraphViewer graphViewer, Point graphLocation)
            : base(graphViewer, graphLocation, DefaultWidth, DefaultHeight)
        {
            Text = "Label";
            try   { TextFont = new Font(_defaultFontFamily, _defaultFontSize, _defaultFontStyle, GraphicsUnit.Point); }
            catch { TextFont = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Point); }
            TextColor = _defaultTextColor;
            BackColor = _defaultBackColor;
            TextAlign = _defaultTextAlign;

            RebuildGdiObjects();
            AutoSizeToText();
        }

        /// <summary>Private constructor used by FromJson.</summary>
        private TextAnnotationElement(ProductionGraphViewer graphViewer,
                                      Point location, Size size,
                                      string text, Font textFont,
                                      Color textColor, Color backColor,
                                      StringAlignment textAlign)
            : base(graphViewer, location, size.Width, size.Height)
        {
            Text = text;
            TextFont = textFont;
            TextColor = textColor;
            BackColor = backColor;
            TextAlign = textAlign;

            RebuildGdiObjects();
        }

        /// <summary>
        /// Resizes the element to fit its current Text at the current TextFont.
        /// Wraps at a maximum of 600px; always leaves a small padding margin.
        /// Call this whenever Text or TextFont changes.
        /// </summary>
        public void AutoSizeToText()
        {
            if (string.IsNullOrEmpty(Text)) return;

            const int padding = 16;
            const int minWidth = 60;
            const int minHeight = 30;

            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
            {
                SizeF natural = g.MeasureString(Text, TextFont);
                Width  = Math.Max(minWidth,  (int)Math.Ceiling(natural.Width)  + padding);
                Height = Math.Max(minHeight, (int)Math.Ceiling(natural.Height) + padding);
            }
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
            _textFormat?.Dispose();

            _textBrush = new SolidBrush(TextColor);
            _backBrush = (BackColor.A > 0) ? new SolidBrush(BackColor) : null;
            _textFormat = new StringFormat
            {
                Alignment = TextAlign,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.None,
                FormatFlags = StringFormatFlags.NoWrap
            };
        }

        // ----------------------------------------------------------------
        // Drawing
        // ----------------------------------------------------------------

        public override bool ContainsPoint(Point graph_point)
        {
            if (!Visible)
                return false;
            // Resize handles sit outside bounds — check them first when selected
            if (IsSelected && GetHandleAtPoint(graph_point) != HandleType.None)
                return true;
            // Text labels respond to clicks anywhere in their full bounds,
            // not just the edge band — the whole box is interactive.
            return Bounds.Contains(GraphToLocal(graph_point));
        }

        protected override void Draw(Graphics graphics, NodeDrawingStyle style)
        {
            Rectangle r = GetGraphRect();

            // 1. Selection highlight (behind everything)
            DrawSelectionHighlight(graphics, r);

            // 2. Background fill (only when not transparent)
            if (_backBrush != null)
                graphics.FillRectangle(_backBrush, r);

            // 3. Text, aligned and clipped with ellipsis
            if (!string.IsNullOrEmpty(Text))
                graphics.DrawString(Text, TextFont, _textBrush, (RectangleF)r, _textFormat);

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
                if (form.ShowDialog(graphViewer.FindForm()) == System.Windows.Forms.DialogResult.OK)
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
            json["TextAlign"] = (int)TextAlign;
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
            StringAlignment textAlign = json["TextAlign"] != null ? (StringAlignment)(int)json["TextAlign"] : StringAlignment.Center;

            Font font;
            try { font = new Font(family, size, style, GraphicsUnit.Point); }
            catch { font = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Point); }

            return new TextAnnotationElement(graphViewer, loc, sz, text, font, textColor, backColor, textAlign);
        }

        // ----------------------------------------------------------------
        // Cleanup
        // ----------------------------------------------------------------

        public override void Dispose()
        {
            _textBrush?.Dispose();
            _backBrush?.Dispose();
            _textFormat?.Dispose();
            TextFont?.Dispose();
            base.Dispose();
        }
    }
}