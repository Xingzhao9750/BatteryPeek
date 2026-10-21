using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryPeek.Ui
{
    /// <summary>
    /// Small always-on-top pill that shows the lowest battery level. It exists because the
    /// notification area is not always available (Windows 11 hides new icons, some machines
    /// refuse Shell_NotifyIcon outright), and a tool you cannot see is not a tool.
    /// </summary>
    internal sealed class BatteryWidget : Form
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;

        private const int PadX = 10;
        private const int Height_ = 30;
        private const string PositionKeyPath = @"Software\BatteryPeek";
        private const string PositionValueName = "WidgetPosition";
        private const string VisibleValueName = "WidgetVisible";

        private readonly ContextMenuStrip menu;
        private readonly ToolTip tip = new ToolTip();
        private string label = "外设电量";
        private int? percent;
        private bool dragging;
        private Point dragOrigin;
        private Point windowOrigin;

        internal event EventHandler OpenRequested;
        internal event EventHandler RefreshRequested;
        internal event EventHandler QuitRequested;
        internal event EventHandler HiddenRequested;

        internal BatteryWidget()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = Theme.Background;
            SetStyle(ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;

            menu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem("打开面板");
            openItem.Click += delegate { Raise(OpenRequested); };

            var refreshItem = new ToolStripMenuItem("刷新");
            refreshItem.Click += delegate { Raise(RefreshRequested); };

            var hideItem = new ToolStripMenuItem("隐藏悬浮窗");
            hideItem.Click += delegate { Raise(HiddenRequested); };

            var snapRightItem = new ToolStripMenuItem("钉在右下角");
            snapRightItem.Click += delegate { SnapToCorner(true); };

            var snapLeftItem = new ToolStripMenuItem("钉在左下角");
            snapLeftItem.Click += delegate { SnapToCorner(false); };


            var diagItem = new ToolStripMenuItem("诊断…");
            diagItem.Click += delegate { ShowDiagnostics(); };

            var quitItem = new ToolStripMenuItem("退出");
            quitItem.Click += delegate { Raise(QuitRequested); };

            menu.Items.Add(openItem);
            menu.Items.Add(snapRightItem);
            menu.Items.Add(snapLeftItem);
            menu.Items.Add(diagItem);
            menu.Items.Add(refreshItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(hideItem);
            menu.Items.Add(quitItem);
            ContextMenuStrip = menu;


            Size = new Size(120, Height_);
            RestorePosition();

            // Keep the pill above shell flyouts: the notification-area popup is topmost too and
            // would otherwise sit on top of the widget parked in the same corner.
            var pinTimer = new Timer();
            pinTimer.Interval = 1200;
            pinTimer.Tick += delegate
            {
                if (Visible && TopMost)
                {
                    SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                }
            };
            pinTimer.Start();
        }

        /// <summary>
        /// Shows the environment facts on screen. On this machine the log file cannot be
        /// written, so a dialog is the only channel the user can actually relay back.
        /// </summary>
        private void ShowDiagnostics()
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine("日志路径: " + Log.Path_);
            text.AppendLine("会话: " + System.Diagnostics.Process.GetCurrentProcess().SessionId
                + "   用户: " + Environment.UserName);
            text.AppendLine("托盘图标: " + (TrayIconVisible ? "已请求显示" : "未显示"));
            text.AppendLine("悬浮窗位置: " + Location.X + "," + Location.Y);
            text.AppendLine("数据: " + (percent.HasValue ? percent.Value + "% " + label : label));
            text.AppendLine();
            text.AppendLine("把这一段截图发我就能定位问题。");

            MessageBox.Show(this, text.ToString(), "外设电量 · 诊断", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        internal bool TrayIconVisible { get; set; }

        private static void Raise(EventHandler handler)
        {
            if (handler != null)
            {
                handler(null, EventArgs.Empty);
            }
        }

        internal static bool IsEnabledByUser()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PositionKeyPath, false))
                {
                    // Missing value means "first run": the widget starts enabled so the user can
                    // find the tool even when the tray icon never appears.
                    return key == null || key.GetValue(VisibleValueName) == null || (int)key.GetValue(VisibleValueName) != 0;
                }
            }
            catch (Exception)
            {
                return true;
            }
        }

        internal static void SetEnabledByUser(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(PositionKeyPath))
                {
                    if (key != null)
                    {
                        key.SetValue(VisibleValueName, enabled ? 1 : 0);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Parks the pill just above the taskbar in the chosen corner.</summary>
        internal void SnapToCorner(bool right)
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            int x = right ? work.Right - Width - 12 : work.Left + 12;
            int y = work.Bottom - Height - 8;
            Location = new Point(x, y);
            SavePosition();
            BringToFront();
        }

        private void RestorePosition()
        {
            Point saved = Point.Empty;
            bool hasSaved = false;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PositionKeyPath, false))
                {
                    object raw = key == null ? null : key.GetValue(PositionValueName);
                    string text = raw as string;
                    if (text != null)
                    {
                        string[] parts = text.Split(',');
                        int x, y;
                        if (parts.Length == 2 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y))
                        {
                            saved = new Point(x, y);
                            hasSaved = true;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            if (!hasSaved)
            {
                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                saved = new Point(work.Right - Width - 24, work.Bottom - Height - 16);
            }

            Location = saved;
        }

        private void SavePosition()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(PositionKeyPath))
                {
                    if (key != null)
                    {
                        key.SetValue(PositionValueName, Location.X + "," + Location.Y);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        internal void Update(IList<PeripheralBattery> devices)
        {
            int? lowest = null;
            string lowestName = null;

            foreach (PeripheralBattery device in devices)
            {
                if (device.Percent.HasValue && (!lowest.HasValue || device.Percent.Value < lowest.Value))
                {
                    lowest = device.Percent;
                    lowestName = device.Name;
                }
            }

            percent = lowest;
            label = lowestName ?? (devices.Count > 0 ? "未上报" : "无设备");

            // Keep the pill compact: the clipped device name is only a hint.
            string shortName = label.Length > 8 ? label.Substring(0, 7) + "…" : label;
            string text = percent.HasValue ? percent.Value + "%  " + shortName : shortName;

            using (Graphics graphics = CreateGraphics())
            {
                Size textSize = TextRenderer.MeasureText(graphics, text, Theme.Small);
                Width = Math.Max(96, PadX * 2 + 22 + textSize.Width);
            }

            string full = percent.HasValue ? label + " " + percent.Value + "%" : label;
            tip.SetToolTip(this, full);

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (GraphicsPath path = Theme.RoundedRect(new RectangleF(0.5F, 0.5F, Width - 1.5F, Height - 1.5F), 8F))
            using (var fill = new SolidBrush(Theme.Surface))
            using (var pen = new Pen(Theme.Border))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            Color color = Theme.LevelColor(percent);

            // Battery glyph
            var body = new RectangleF(PadX, 10F, 14F, 9F);
            using (GraphicsPath path = Theme.RoundedRect(body, 2.5F))
            using (var pen = new Pen(color, 1.6F))
            {
                g.DrawPath(pen, path);
            }

            using (var brush = new SolidBrush(color))
            {
                g.FillRectangle(brush, PadX + 14F, 12.5F, 2F, 4F);
            }

            if (percent.HasValue)
            {
                float ratio = Math.Max(0.12F, Math.Min(1F, percent.Value / 100F));
                using (GraphicsPath fill = Theme.RoundedRect(new RectangleF(PadX + 2F, 12F, 10F * ratio, 5F), 1.4F))
                using (var brush = new SolidBrush(color))
                {
                    g.FillPath(brush, fill);
                }
            }

            string shortName = label.Length > 8 ? label.Substring(0, 7) + "…" : label;
            string text = percent.HasValue ? percent.Value + "%  " + shortName : shortName;

            TextRenderer.DrawText(g, text, Theme.Small,
                new Rectangle(PadX + 22, 0, Width - PadX - 24, Height), Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                dragOrigin = Cursor.Position;
                windowOrigin = Location;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (!dragging)
            {
                return;
            }

            Point now = Cursor.Position;
            Location = new Point(windowOrigin.X + (now.X - dragOrigin.X), windowOrigin.Y + (now.Y - dragOrigin.Y));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            bool moved = Math.Abs(Cursor.Position.X - dragOrigin.X) > 3 || Math.Abs(Cursor.Position.Y - dragOrigin.Y) > 3;
            dragging = false;

            if (moved)
            {
                SavePosition();
                return;
            }

            Raise(OpenRequested);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Update(BatteryService.ReadAll());
        }
    }
}
