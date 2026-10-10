using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DuoClick
{
    internal sealed class SelectionOverlay : Form
    {
        private readonly Bitmap _image;
        private readonly Rectangle _desktopBounds;
        private AppLanguage _language;
        private Point _start;
        private Point _current;

        internal SelectionOverlay(Bitmap image, Rectangle desktopBounds, Point start,
            AppLanguage language)
        {
            _image = image;
            _desktopBounds = desktopBounds;
            _language = language;
            _start = start;
            _current = start;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = desktopBounds;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = Color.Black;
            Cursor = Cursors.Cross;
        }

        internal void SetSelectionEnd(Point point)
        {
            _current = point;
            Invalidate();
        }

        internal void SetLanguage(AppLanguage language)
        {
            _language = language;
            Invalidate();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                parameters.ExStyle |= 0x00080000; // WS_EX_LAYERED
                parameters.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
                return parameters;
            }
        }

        protected override void OnShown(System.EventArgs e)
        {
            base.OnShown(e);
            // A fully opaque layered window still paints normally, while the
            // layered+transparent extended styles keep it out of mouse hit tests.
            SetLayeredWindowAttributes(Handle, 0, 255, 0x00000002);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.DrawImageUnscaled(_image, 0, 0);

            using (SolidBrush shade = new SolidBrush(Color.FromArgb(135, 0, 0, 0)))
                graphics.FillRectangle(shade, ClientRectangle);

            Rectangle selection = GetLocalSelection();
            if (selection.Width > 0 && selection.Height > 0)
            {
                graphics.DrawImage(_image, selection, selection, GraphicsUnit.Pixel);
                using (Pen outline = new Pen(Color.FromArgb(255, 65, 170, 255), 2.0f))
                {
                    outline.Alignment = PenAlignment.Inset;
                    graphics.DrawRectangle(outline, selection);
                }
            }

            DrawInstruction(graphics, _language);
        }

        private Rectangle GetLocalSelection()
        {
            int left = System.Math.Min(_start.X, _current.X) - _desktopBounds.Left;
            int top = System.Math.Min(_start.Y, _current.Y) - _desktopBounds.Top;
            int right = System.Math.Max(_start.X, _current.X) - _desktopBounds.Left;
            int bottom = System.Math.Max(_start.Y, _current.Y) - _desktopBounds.Top;

            Rectangle selection = Rectangle.FromLTRB(left, top, right, bottom);
            return Rectangle.Intersect(selection, ClientRectangle);
        }

        private static void DrawInstruction(Graphics graphics, AppLanguage language)
        {
            string instruction = AppLocalization.Get(language, AppText.OverlayInstruction);
            using (Font font = new Font("Segoe UI", 12.0f, FontStyle.Regular, GraphicsUnit.Point))
            {
                SizeF size = graphics.MeasureString(instruction, font);
                RectangleF background = new RectangleF(18, 18, size.Width + 26, size.Height + 18);
                using (SolidBrush back = new SolidBrush(Color.FromArgb(210, 25, 29, 36)))
                    graphics.FillRectangle(back, background);
                using (SolidBrush text = new SolidBrush(Color.White))
                    graphics.DrawString(instruction, font, text, 31, 26);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(System.IntPtr window,
            uint colorKey, byte alpha, uint flags);
    }
}
