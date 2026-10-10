using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AirStereo.Ui
{
    /// <summary>Keyboard-accessible slider used by the compact dark control surface.</summary>
    internal sealed class ValueSlider : Control
    {
        private int minimum;
        private int maximum = 100;
        private int value;
        /// <summary>
        /// Blocks user input without setting Enabled=false.  This keeps the slider's
        /// normal theme colours readable while a negotiated stream is running.
        /// </summary>
        private bool inputLocked;
        public bool InputLocked
        {
            get => inputLocked;
            set
            {
                if (inputLocked == value) return;
                inputLocked = value;
                Invalidate();
            }
        }
        public event EventHandler ValueChanged;
        public int Minimum { get => minimum; set { minimum = value; Value = this.value; Invalidate(); } }
        public int Maximum { get => maximum; set { maximum = value; Value = this.value; Invalidate(); } }
        public int SmallChange { get; set; } = 1;
        public int LargeChange { get; set; } = 10;
        public Orientation Orientation { get; set; } = Orientation.Horizontal;
        public int TickFrequency { get; set; }
        public int Value
        {
            get => value;
            set
            {
                int next = Math.Max(minimum, Math.Min(maximum, value));
                if (this.value == next) return;
                this.value = next;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            }
        }

        public ValueSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.Slider;
            Cursor = Cursors.Hand;
            Size = new Size(160, 32);
        }

        private int Radius => Math.Max(5, (int)Math.Round(6 * DeviceDpi / 96.0));
        private void SetFromPointer(int x, int y)
        {
            int inset = Radius + 3;
            double position = Orientation == Orientation.Vertical
                ? 1 - (y - inset) / (double)Math.Max(1, Height - inset * 2)
                : (x - inset) / (double)Math.Max(1, Width - inset * 2);
            Value = minimum + (int)Math.Round(Math.Max(0, Math.Min(1, position)) * (maximum - minimum));
        }

        protected override void OnMouseDown(MouseEventArgs args)
        {
            base.OnMouseDown(args);
            if (InputLocked || args.Button != MouseButtons.Left) return;
            Focus();
            Capture = true;
            SetFromPointer(args.X, args.Y);
        }

        protected override void OnMouseMove(MouseEventArgs args)
        {
            base.OnMouseMove(args);
            if (!InputLocked && Capture && args.Button == MouseButtons.Left) SetFromPointer(args.X, args.Y);
        }

        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            if (args.Button == MouseButtons.Left) Capture = false;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
                key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs args)
        {
            base.OnKeyDown(args);
            if (InputLocked) return;
            if (args.KeyCode == Keys.Left || args.KeyCode == Keys.Down) Value -= SmallChange;
            else if (args.KeyCode == Keys.Right || args.KeyCode == Keys.Up) Value += SmallChange;
            else if (args.KeyCode == Keys.PageDown) Value -= LargeChange;
            else if (args.KeyCode == Keys.PageUp) Value += LargeChange;
            else if (args.KeyCode == Keys.Home) Value = minimum;
            else if (args.KeyCode == Keys.End) Value = maximum;
            else return;
            args.Handled = true;
        }

        protected override void OnMouseWheel(MouseEventArgs args)
        {
            base.OnMouseWheel(args);
            if (InputLocked) return;
            Value += args.Delta / SystemInformation.MouseWheelScrollDelta * SmallChange;
        }

        protected override void OnEnabledChanged(EventArgs args) { base.OnEnabledChanged(args); Invalidate(); }
        protected override void OnGotFocus(EventArgs args) { base.OnGotFocus(args); Invalidate(); }
        protected override void OnLostFocus(EventArgs args) { base.OnLostFocus(args); Invalidate(); }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            Graphics graphics = args.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (Orientation == Orientation.Vertical)
            {
                DrawVertical(graphics);
                DrawFocus(graphics);
                return;
            }
            int inset = Radius + 3;
            int y = Height / 2;
            int end = Math.Max(inset, Width - inset);
            int thumb = inset + (int)Math.Round((end - inset) * (value - minimum) / (double)Math.Max(1, maximum - minimum));
            // A negotiated buffer cannot be changed mid-session.  Use a muted rail
            // and thumb for that state, but do not set Enabled=false: WinForms would
            // also darken surrounding text on some Windows themes.
            Color accent = !Enabled || InputLocked
                ? DesktopTheme.Disabled
                : DesktopTheme.StrongAccent;
            using (Pen rail = new Pen(DesktopTheme.Disabled, Math.Max(2, DeviceDpi / 32F)))
            using (Pen fill = new Pen(accent, rail.Width))
            using (SolidBrush knob = new SolidBrush(accent))
            {
                rail.StartCap = rail.EndCap = LineCap.Round;
                fill.StartCap = fill.EndCap = LineCap.Round;
                graphics.DrawLine(rail, inset, y, end, y);
                if (thumb > inset) graphics.DrawLine(fill, inset, y, thumb, y);
                DrawThumb(graphics, thumb, y, accent);
            }
            DrawFocus(graphics);
        }

        private void DrawVertical(Graphics graphics)
        {
            int inset = Radius + 3, end = Math.Max(inset, Height - inset), x = Width / 2;
            int thumb = end - (int)Math.Round((end - inset) * (value - minimum) / (double)Math.Max(1, maximum - minimum));
            Color accent = !Enabled || InputLocked ? DesktopTheme.Disabled : DesktopTheme.StrongAccent;
            using (Pen rail = new Pen(DesktopTheme.Disabled, Math.Max(2, DeviceDpi / 32F)))
            using (Pen fill = new Pen(accent, rail.Width))
            {
                rail.StartCap = rail.EndCap = fill.StartCap = fill.EndCap = LineCap.Round;
                graphics.DrawLine(rail, x, inset, x, end);
                if (TickFrequency > 0)
                    for (int tick = minimum; tick <= maximum; tick += TickFrequency)
                    {
                        int y = end - (int)Math.Round((end - inset) * (tick - minimum) / (double)Math.Max(1, maximum - minimum));
                        graphics.DrawLine(rail, x + Radius + 6, y, x + Radius + 7, y);
                    }
                if (thumb < end) graphics.DrawLine(fill, x, thumb, x, end);
                DrawThumb(graphics, x, thumb, accent);
            }
        }

        private void DrawThumb(Graphics graphics, int x, int y, Color accent)
        {
            using (SolidBrush ring = new SolidBrush(DesktopTheme.Surface))
            using (Pen border = new Pen(DesktopTheme.Border))
            using (SolidBrush fill = new SolidBrush(accent))
            {
                graphics.FillEllipse(ring, x - Radius - 2, y - Radius - 2, Radius * 2 + 4, Radius * 2 + 4);
                graphics.DrawEllipse(border, x - Radius - 2, y - Radius - 2, Radius * 2 + 4, Radius * 2 + 4);
                graphics.FillEllipse(fill, x - Radius + 1, y - Radius + 1, Radius * 2 - 2, Radius * 2 - 2);
            }
        }

        private void DrawFocus(Graphics graphics)
        {
            if (Focused && ShowFocusCues)
                using (Pen focus = new Pen(DesktopTheme.Accent) { DashStyle = DashStyle.Dot })
                    graphics.DrawRectangle(focus, 1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new SliderAccessibleObject(this);
        private sealed class SliderAccessibleObject : ControlAccessibleObject
        {
            private readonly ValueSlider slider;
            public SliderAccessibleObject(ValueSlider owner) : base(owner) { slider = owner; }
            public override string Value
            {
                get => slider.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                set { if (int.TryParse(value, out int next)) slider.Value = next; }
            }
        }
    }
}
