using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Newtonsoft.Json.Linq;

namespace Foreman
{
    /// <summary>
    /// A freehand rectangle or ellipse annotation drawn on the canvas.
    /// All GDI resources are lazily rebuilt when properties change.
    /// </summary>
    public class ShapeAnnotationElement : AnnotationElement
    {
        // ----------------------------------------------------------------
        // Shape type
        // ----------------------------------------------------------------

        public enum ShapeType { Rectangle, Ellipse }

        // ----------------------------------------------------------------
        // Default dimensions for newly created shapes
        // ----------------------------------------------------------------

        private const int DefaultWidth = 200;
        private const int DefaultHeight = 150;

        // ----------------------------------------------------------------
        // Appearance properties
        // ----------------------------------------------------------------

        public ShapeType CurrentShapeType { get; set; }
        public Color FillColor { get; set; }
        public Color BorderColor { get; set; }
        public int BorderWidth { get; set; }

        // ----------------------------------------------------------------
        // GDI resources — rebuilt via RebuildGdiObjects()
        // ----------------------------------------------------------------

        private SolidBrush _fillBrush;
        private Pen _borderPen;

        // ----------------------------------------------------------------
        // Construction
        // ----------------------------------------------------------------

        /// <summary>Creates a new shape at the given graph-space location with defaults.</summary>
        public ShapeAnnotationElement(ProductionGraphViewer graphViewer, Point graphLocation)
            : base(graphViewer, graphLocation, DefaultWidth, DefaultHeight)
        {
            CurrentShapeType = ShapeType.Rectangle;
            FillColor = Color.FromArgb(0, 80, 140, 255);  // semi-transparent blue fill
            BorderColor = Color.FromArgb(220, 60, 120, 220);  // blue-ish border
            BorderWidth = 2;

            RebuildGdiObjects();
        }

        /// <summary>Private constructor used by FromJson.</summary>
        private ShapeAnnotationElement(ProductionGraphViewer graphViewer,
                                       Point location, Size size,
                                       ShapeType shapeType,
                                       Color fillColor, Color borderColor, int borderWidth)
            : base(graphViewer, location, size.Width, size.Height)
        {
            CurrentShapeType = shapeType;
            FillColor = fillColor;
            BorderColor = borderColor;
            BorderWidth = borderWidth;

            RebuildGdiObjects();
        }

        /// <summary>
        /// Recreates all GDI objects from current property values.
        /// Must be called after changing any appearance property, and by
        /// ShapePropertiesForm when the user clicks OK.
        /// </summary>
        public void RebuildGdiObjects()
        {
            _fillBrush?.Dispose();
            _borderPen?.Dispose();

            _fillBrush = new SolidBrush(FillColor);

            _borderPen = new Pen(BorderColor, Math.Max(1, BorderWidth));
            _borderPen.Alignment = PenAlignment.Inset; // keep border inside bounds
        }

        // ----------------------------------------------------------------
        // Drawing
        // ----------------------------------------------------------------

        protected override void Draw(Graphics graphics, NodeDrawingStyle style)
        {
            // Convert element position to graph-space rect for GDI.
            Rectangle r = GetGraphRect();

            // 1. Selection highlight (behind everything)
            DrawSelectionHighlight(graphics, r);

            // 2. Fill
            if (FillColor.A > 0)
            {
                if (CurrentShapeType == ShapeType.Rectangle)
                    graphics.FillRectangle(_fillBrush, r);
                else
                    graphics.FillEllipse(_fillBrush, r);
            }

            // 3. Border
            if (BorderWidth > 0 && BorderColor.A > 0)
            {
                if (CurrentShapeType == ShapeType.Rectangle)
                    graphics.DrawRectangle(_borderPen, r);
                else
                    graphics.DrawEllipse(_borderPen, r);
            }
            // 4. Resize handles (drawn on top when selected)
            DrawResizeHandles(graphics);
        }

        // ----------------------------------------------------------------
        // Properties dialog (Stage 3)
        // ----------------------------------------------------------------

        public override void ShowPropertiesDialog()
        {
            using (ShapePropertiesForm form = new ShapePropertiesForm(this))
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
            JObject json = BaseJson("Shape", this);
            json["ShapeType"] = CurrentShapeType.ToString();
            json["FillColor"] = ColorToJson(FillColor);
            json["BorderColor"] = ColorToJson(BorderColor);
            json["BorderWidth"] = BorderWidth;
            return json;
        }

        public static new ShapeAnnotationElement FromJson(JObject json, ProductionGraphViewer graphViewer)
        {
            Point loc = LocationFromJson(json);
            Size sz = SizeFromJson(json);
            ShapeType shapeType = (ShapeType)Enum.Parse(typeof(ShapeType), (string)json["ShapeType"]);
            Color fillColor = ColorFromJson(json["FillColor"]);
            Color borderColor = ColorFromJson(json["BorderColor"]);
            int borderWidth = (int)json["BorderWidth"];

            return new ShapeAnnotationElement(graphViewer, loc, sz, shapeType, fillColor, borderColor, borderWidth);
        }

        // ----------------------------------------------------------------
        // Cleanup
        // ----------------------------------------------------------------

        public override void Dispose()
        {
            _fillBrush?.Dispose();
            _borderPen?.Dispose();
            base.Dispose();
        }
    }
}