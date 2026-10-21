using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace BatteryPeek.Ui
{
    internal static class Theme
    {
        internal static readonly Color Background = Color.FromArgb(24, 25, 29);
        internal static readonly Color Surface = Color.FromArgb(33, 35, 41);
        internal static readonly Color Border = Color.FromArgb(54, 57, 66);
        internal static readonly Color TextPrimary = Color.FromArgb(240, 242, 247);
        internal static readonly Color TextSecondary = Color.FromArgb(148, 154, 168);
        internal static readonly Color Accent = Color.FromArgb(92, 173, 255);
        internal static readonly Color Good = Color.FromArgb(88, 196, 130);
        internal static readonly Color Medium = Color.FromArgb(232, 178, 74);
        internal static readonly Color Low = Color.FromArgb(232, 96, 96);
        internal static readonly Color Track = Color.FromArgb(48, 51, 60);
        internal static readonly Color Experimental = Color.FromArgb(196, 160, 90);

        internal static Font Title = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        internal static Font Device = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        internal static Font Body = new Font("Microsoft YaHei UI", 9F);
        internal static Font Small = new Font("Microsoft YaHei UI", 8F);
        internal static Font Percent = new Font("Consolas", 10F, FontStyle.Bold);

        internal static Color LevelColor(int? percent)
        {
            if (!percent.HasValue)
            {
                return TextSecondary;
            }

            if (percent.Value >= 50)
            {
                return Good;
            }

            return percent.Value >= 20 ? Medium : Low;
        }

        internal static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            float diameter = radius * 2F;
            if (diameter > bounds.Width)
            {
                diameter = bounds.Width;
            }

            if (diameter > bounds.Height)
            {
                diameter = bounds.Height;
            }

            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>The dark popup panel that lists every peripheral and its charge.</summary>
    internal sealed class BatteryPopup : Form
    {
        private const int PadX = 20;
        private const int HeaderHeight = 56;
        private const int RowHeight = 58;
        private const int FooterHeight = 44;
        private const int Width_ = 400;

        private readonly List<PeripheralBattery> rows = new List<PeripheralBattery>();
        private Rectangle refreshHit = new Rectangle(PadX, 0, 64, 28);
        private Rectangle quitHit = new Rectangle(PadX + 78, 0, 64, 28);
        private DateTime updatedUtc = DateTime.MinValue;
        private bool loading;
        private string errorText;
        private Rectangle closeHit = Rectangle.Empty;

        internal event EventHandler RefreshRequested;
        internal event EventHandler QuitRequested;

        internal BatteryPopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Theme.Background;
            Text = "外设电量";
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        internal void ShowAtCursor()
        {
            Relayout();
            Point cursor = Cursor.Position;
            Rectangle screen = Screen.FromPoint(cursor).WorkingArea;
            int x = cursor.X - Width / 2;
            int y = cursor.Y - Height - 12;

            if (y < screen.Top + 8)
            {
                y = cursor.Y + 16;
            }

            x = Math.Max(screen.Left + 8, Math.Min(x, screen.Right - Width - 8));
            Location = new Point(x, y);
            Show();
            Activate();
            BeginRefresh();
        }

        internal void SetData(List<PeripheralBattery> devices, DateTime utc)
        {
            rows.Clear();
            if (devices != null)
            {
                rows.AddRange(devices);
            }

            updatedUtc = utc;
            loading = false;
            errorText = null;
            Relayout();
            Invalidate();
        }

        internal void SetLoading()
        {
            loading = true;
            Relayout();
            Invalidate();
        }

        internal void SetError(string message)
        {
            loading = false;
            errorText = message;
            Relayout();
            Invalidate();
        }

        private void BeginRefresh()
        {
            SetLoading();
            EventHandler handler = RefreshRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void Relayout()
        {
            int rowCount = Math.Max(rows.Count, loading || errorText != null ? 1 : 1);
            int height = HeaderHeight + rowCount * RowHeight + FooterHeight;
            int targetWidth = Width = Math.Min(Width_, Screen.FromPoint(Cursor.Position).WorkingArea.Width - 40);
            Size = new Size(targetWidth, height);

            using (GraphicsPath path = Theme.RoundedRect(new RectangleF(0, 0, Width, Height), 14F))
            {
                Region = new Region(path);
            }

            int footerTop = Height - FooterHeight + 10;
            refreshHit.Y = footerTop;
            quitHit.Y = footerTop;
            closeHit = new Rectangle(Width - PadX - 18, 18, 18, 18);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var background = new SolidBrush(Theme.Background))
            {
                g.FillRectangle(background, ClientRectangle);
            }

            using (GraphicsPath border = Theme.RoundedRect(new RectangleF(0.5F, 0.5F, Width - 1.5F, Height - 1.5F), 14F))
            using (var pen = new Pen(Theme.Border))
            {
                g.DrawPath(pen, border);
            }

            TextRenderer.DrawText(g, "外设电量", Theme.Title, new Point(PadX, 16), Theme.TextPrimary, TextFormatFlags.NoPadding);

            string subtitle = loading
                ? "正在读取…"
                : updatedUtc == DateTime.MinValue
                    ? "尚未读取"
                    : "更新于 " + updatedUtc.ToLocalTime().ToString("HH:mm:ss");
            TextRenderer.DrawText(g, subtitle, Theme.Small,
                new Rectangle(PadX, 36, Width - PadX * 2 - 24, 16), Theme.TextSecondary,
                TextFormatFlags.NoPadding | TextFormatFlags.Right);

            // Close affordance.
            using (var pen = new Pen(Theme.TextSecondary, 1.6F))
            {
                int inset = 5;
                g.DrawLine(pen, closeHit.Left + inset, closeHit.Top + inset, closeHit.Right - inset, closeHit.Bottom - inset);
                g.DrawLine(pen, closeHit.Right - inset, closeHit.Top + inset, closeHit.Left + inset, closeHit.Bottom - inset);
            }

            int y = HeaderHeight;
            if (rows.Count == 0)
            {
                string message = errorText ?? (loading ? "正在读取外设…" : "没有找到可读取电量的外设");
                TextRenderer.DrawText(g, message, Theme.Body, new Rectangle(PadX, y + 12, Width - PadX * 2, 40),
                    Theme.TextSecondary, TextFormatFlags.WordBreak);
            }
            else
            {
                foreach (PeripheralBattery device in rows)
                {
                    DrawRow(g, device, y);
                    y += RowHeight;
                }
            }

            int dividerY = Height - FooterHeight + 2;
            using (var pen = new Pen(Theme.Border))
            {
                g.DrawLine(pen, PadX, dividerY, Width - PadX, dividerY);
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawFooterButton(g, refreshHit, "刷新");
            DrawFooterButton(g, quitHit, "退出");
            TextRenderer.DrawText(g, "点击空白处关闭", Theme.Small,
                new Rectangle(quitHit.Right + 16, refreshHit.Top + 6, Width - quitHit.Right - PadX - 20, 18),
                Theme.TextSecondary, TextFormatFlags.NoPadding | TextFormatFlags.Right);
        }

        private static void DrawFooterButton(Graphics g, Rectangle bounds, string label)
        {
            using (GraphicsPath path = Theme.RoundedRect(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), 6F))
            using (var fill = new SolidBrush(Theme.Surface))
            using (var pen = new Pen(Theme.Border))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            TextRenderer.DrawText(g, label, Theme.Body, bounds, Theme.TextPrimary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void DrawRow(Graphics g, PeripheralBattery device, int top)
        {
            // Line 1: device name on the left, the number on the right.
            var nameRect = new Rectangle(PadX, top + 6, Width - PadX * 2 - 64, 20);
            TextRenderer.DrawText(g, device.Name, Theme.Device, nameRect, Theme.TextPrimary,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            string percentText = device.Percent.HasValue ? device.Percent.Value + "%" : "—";
            TextRenderer.DrawText(g, percentText, Theme.Percent,
                new Rectangle(Width - PadX - 56, top + 6, 56, 20),
                device.Percent.HasValue ? Theme.LevelColor(device.Percent) : Theme.TextSecondary,
                TextFormatFlags.NoPadding | TextFormatFlags.Right);

            // Line 2: the level bar.
            var barRect = new RectangleF(PadX, top + 30, Width - PadX * 2, 6);
            using (GraphicsPath track = Theme.RoundedRect(barRect, 3F))
            using (var trackBrush = new SolidBrush(Theme.Track))
            {
                g.FillPath(trackBrush, track);
            }

            if (device.Percent.HasValue)
            {
                float ratio = Math.Max(0.02F, Math.Min(1F, device.Percent.Value / 100F));
                var fillRect = new RectangleF(barRect.X, barRect.Y, barRect.Width * ratio, barRect.Height);
                using (GraphicsPath fill = Theme.RoundedRect(fillRect, 3F))
                using (var brush = new SolidBrush(Theme.LevelColor(device.Percent)))
                {
                    g.FillPath(brush, fill);
                }
            }

            // Line 3: connection tag on the left, state on the right.
            string tag = device.Transport;
            if (device.IsExperimental)
            {
                tag += " · 实验性";
            }

            Color tagColor = device.IsExperimental ? Theme.Experimental : Theme.TextSecondary;
            TextRenderer.DrawText(g, tag, Theme.Small, new Rectangle(PadX, top + 40, 220, 14), tagColor,
                TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, device.StateText, Theme.Small,
                new Rectangle(Width - PadX - 120, top + 40, 120, 14), Theme.TextSecondary,
                TextFormatFlags.NoPadding | TextFormatFlags.Right);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (closeHit.Contains(e.Location))
            {
                Hide();
                return;
            }

            if (refreshHit.Contains(e.Location))
            {
                BeginRefresh();
                return;
            }

            if (quitHit.Contains(e.Location))
            {
                EventHandler handler = QuitRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }

                return;
            }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Hide();
        }
    }
}
