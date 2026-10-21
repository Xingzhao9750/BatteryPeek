using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryPeek.Ui
{
    internal static class TrayIcons
    {
        /// <summary>Draws a battery glyph; the fill width tracks the reported level.</summary>
        internal static Icon Create(int? percent)
        {
            const int size = 32;
            using (var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.Transparent);

                // Opaque rounded badge: Bitmap.GetHicon flattens the alpha channel, so a
                // transparent glyph would come out as an unreadable black square.
                using (GraphicsPath badge = Theme.RoundedRect(new RectangleF(1F, 1F, size - 2F, size - 2F), 6.5F))
                using (var badgeBrush = new SolidBrush(Color.FromArgb(42, 44, 50)))
                using (var badgePen = new Pen(Color.FromArgb(84, 88, 98), 1F))
                {
                    g.FillPath(badgeBrush, badge);
                    g.DrawPath(badgePen, badge);
                }

                Color color = Theme.LevelColor(percent);
                var body = new RectangleF(6.5F, 10.5F, 17F, 11F);
                using (GraphicsPath path = Theme.RoundedRect(body, 3F))
                using (var pen = new Pen(color, 2F))
                {
                    g.DrawPath(pen, path);
                }

                using (var brush = new SolidBrush(color))
                {
                    g.FillRectangle(brush, 24.5F, 14F, 2.5F, 4F);
                }

                if (percent.HasValue)
                {
                    float ratio = Math.Max(0.12F, Math.Min(1F, percent.Value / 100F));
                    var inner = new RectangleF(8.5F, 12.5F, 13F * ratio, 7F);
                    using (GraphicsPath fill = Theme.RoundedRect(inner, 1.8F))
                    using (var brush = new SolidBrush(color))
                    {
                        g.FillPath(brush, fill);
                    }
                }
                else
                {
                    using (var brush = new SolidBrush(Color.FromArgb(150, color)))
                    {
                        g.FillEllipse(brush, 13.5F, 14F, 5F, 5F);
                    }
                }

                return FromBitmapHandle(bitmap);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        /// <summary>
        /// Builds the icon through Bitmap.GetHicon: the shell accepts it reliably, whereas a
        /// hand-written ICO stream was ignored by Shell_NotifyIcon even though .NET parsed it.
        /// </summary>
        private static Icon FromBitmapHandle(Bitmap bitmap)
        {
            IntPtr handle = bitmap.GetHicon();
            try
            {
                using (Icon temporary = Icon.FromHandle(handle))
                {
                    return (Icon)temporary.Clone();
                }
            }
            finally
            {
                DestroyIcon(handle);
            }
        }

        /// <summary>
        /// Wraps the bitmap as a 32bpp icon with an alpha channel. Bitmap.GetHicon would drop
        /// the alpha and leave an opaque black square in the notification area.
        /// </summary>
        private static Icon FromBitmap(Bitmap bitmap)
        {
            int width = bitmap.Width;
            int height = bitmap.Height;
            int maskStride = ((width + 31) / 32) * 4;
            int xorSize = width * height * 4;
            int andSize = maskStride * height;

            using (var ico = new MemoryStream())
            {
                var writer = new BinaryWriter(ico);

                // ICONDIR
                writer.Write((short)0);
                writer.Write((short)1);
                writer.Write((short)1);

                // ICONDIRENTRY
                writer.Write((byte)width);
                writer.Write((byte)height);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((short)1);
                writer.Write((short)32);
                writer.Write(40 + xorSize + andSize);
                writer.Write(22);

                // BITMAPINFOHEADER - height counts the XOR image plus the AND mask
                writer.Write(40);
                writer.Write(width);
                writer.Write(height * 2);
                writer.Write((short)1);
                writer.Write((short)32);
                writer.Write(0);
                writer.Write(xorSize);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);

                // BGRA pixels, bottom-up
                for (int y = height - 1; y >= 0; y--)
                {
                    for (int x = 0; x < width; x++)
                    {
                        Color pixel = bitmap.GetPixel(x, y);
                        writer.Write(pixel.B);
                        writer.Write(pixel.G);
                        writer.Write(pixel.R);
                        writer.Write(pixel.A);
                    }
                }

                // AND mask stays zero; transparency comes from the alpha channel
                for (int i = 0; i < andSize; i++)
                {
                    writer.Write((byte)0);
                }

                writer.Flush();
                ico.Position = 0;
                return new Icon(ico);
            }
        }
    }

    /// <summary>Tray icon, context menu and the popup lifetime.</summary>
    internal sealed class TrayApp : IDisposable
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "BatteryPeek";
        private const string SettingsKeyPath = @"Software\BatteryPeek";
        private const string TipValueName = "FirstRunTipShown";

        private readonly NotifyIcon notifyIcon;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem startupItem;

        private readonly BatteryPopup popup;
        private BatteryWidget widget;
        private readonly System.Windows.Forms.Timer refreshTimer;
        private readonly System.Windows.Forms.Timer applyTimer;
        private readonly System.Windows.Forms.Timer showTimer;
        private readonly object gate = new object();
        private readonly EventWaitHandle showEvent;

        private Icon currentIcon;
        private DateTime hiddenUtc = DateTime.MinValue;
        private bool refreshing;
        private bool disposed;
        private volatile bool pendingShow;
        private List<PeripheralBattery> pendingResult;
        private List<PeripheralBattery> lastDevices = new List<PeripheralBattery>();
        private string pendingError;

        internal TrayApp(EventWaitHandle showEvent)
        {
            this.showEvent = showEvent;

            menu = new ContextMenuStrip();
            var showItem = new ToolStripMenuItem("显示面板");
            showItem.Click += delegate { ShowPanel(); };

            var refreshItem = new ToolStripMenuItem("刷新");
            refreshItem.Click += delegate { RefreshAsync(); };


            startupItem = new ToolStripMenuItem("开机启动");
            startupItem.Click += delegate { ToggleStartup(); };

            var widgetItem = new ToolStripMenuItem("显示悬浮窗");
            widgetItem.Click += delegate { ShowWidget(); };

            var quitItem = new ToolStripMenuItem("退出");
            quitItem.Click += delegate { Quit(); };

            menu.Items.Add(showItem);
            menu.Items.Add(widgetItem);
            menu.Items.Add(refreshItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(startupItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quitItem);
            menu.Opening += delegate { startupItem.Checked = IsStartupEnabled(); };
            menu.Renderer = new ToolStripProfessionalRenderer();

            popup = new BatteryPopup();
            popup.RefreshRequested += delegate { RefreshAsync(); };
            popup.QuitRequested += delegate { Quit(); };
            popup.VisibleChanged += delegate
            {
                if (!popup.Visible)
                {
                    hiddenUtc = DateTime.UtcNow;
                }
            };

            currentIcon = TrayIcons.Create(null);
            notifyIcon = new NotifyIcon();
            notifyIcon.Icon = currentIcon;
            notifyIcon.Text = "外设电量";
            notifyIcon.Visible = true;
            notifyIcon.ContextMenuStrip = menu;
            notifyIcon.MouseUp += OnMouseUp;
            notifyIcon.DoubleClick += delegate { ShowPanel(); };

            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = 60000;
            refreshTimer.Tick += delegate { RefreshAsync(); };
            refreshTimer.Start();

            // The worker thread never touches UI objects; a UI timer picks its result up.
            applyTimer = new System.Windows.Forms.Timer();
            applyTimer.Interval = 120;
            applyTimer.Tick += delegate { ApplyPending(); };
            applyTimer.Start();

            // A second launch (or --popup) only raises the event, so it must reach the UI thread
            // through a timer rather than being touched directly.
            showTimer = new System.Windows.Forms.Timer();
            showTimer.Interval = 150;
            showTimer.Tick += delegate
            {
                if (!pendingShow)
                {
                    return;
                }

                pendingShow = false;

                if (!BatteryWidget.IsEnabledByUser())
                {
                    // A second launch is an explicit "where is it?" - bring the widget back.
                    ShowWidget();
                }

                ShowPanel();
            };
            showTimer.Start();

            // The widget is the fallback surface: it works even where the notification area
            // refuses new icons, and it is the only visible thing before the first refresh.
            if (BatteryWidget.IsEnabledByUser())
            {
                ShowWidget();
            }

            StartShowListener();

            Log.Write("tray started; icon visible=" + notifyIcon.Visible
                + "; text length=" + (notifyIcon.Text ?? string.Empty).Length
                + "; session=" + System.Diagnostics.Process.GetCurrentProcess().SessionId
                + "; user=" + Environment.UserName
                + "; localAppDataWritable=" + IsDirectoryWritable(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

            // Only the widget appears on start-up. The panel closes on the first outside click,
            // which reads as "it vanished" - so it is left to an explicit click.
            ShowFirstRunTip();

            RefreshAsync();
        }

        private static bool IsDirectoryWritable(string directory)
        {
            try
            {
                string probe = Path.Combine(directory, "BatteryPeek.probe");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void StartShowListener()
        {
            if (showEvent == null)
            {
                return;
            }

            var listener = new Thread(delegate()
            {
                while (!disposed)
                {
                    try
                    {
                        if (!showEvent.WaitOne(500))
                        {
                            continue;
                        }

                        pendingShow = true;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Write("show listener: " + ex.Message);
                        return;
                    }
                }
            });

            listener.IsBackground = true;
            listener.Name = "BatteryPeek.ShowPanel";
            listener.Start();
        }

        private void ShowFirstRunTip()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath))
                {
                    if (key == null || key.GetValue(TipValueName) != null)
                    {
                        return;
                    }

                    key.SetValue(TipValueName, 1);
                }
            }
            catch (Exception)
            {
                return;
            }

            notifyIcon.ShowBalloonTip(6000, "外设电量已启动",
                "如果任务栏上看不到图标，点右下角的 ^ ，把电池图标拖到任务栏即可常驻。",
                ToolTipIcon.Info);
        }

        private void ShowWidget()
        {
            if (widget == null)
            {
                widget = new BatteryWidget();
                widget.OpenRequested += delegate { ShowPanel(); };
                widget.RefreshRequested += delegate { RefreshAsync(); };
                widget.QuitRequested += delegate
                {
                    if (MessageBox.Show("确定退出外设电量？", "外设电量",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    {
                        Quit();
                    }
                };
                widget.HiddenRequested += delegate
                {
                    widget.Hide();
                    BatteryWidget.SetEnabledByUser(false);
                    Log.Write("widget hidden by user");
                };
            }

            BatteryWidget.SetEnabledByUser(true);
            widget.Show();
            widget.TrayIconVisible = notifyIcon != null && notifyIcon.Visible;
            widget.Update(lastDevices);
            widget.BringToFront();
            Log.Write("widget shown; visible=" + widget.Visible);
        }

        private void ShowPanel()
        {
            if (popup.Visible)
            {
                popup.Hide();
            }

            popup.ShowAtCursor();
            Log.Write("panel shown");

            // Opening the panel is the user asking "what is it now" - never show stale numbers.
            RefreshAsync();
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            if (popup.Visible)
            {
                popup.Hide();
                return;
            }

            // The popup hides itself on deactivate; a click right after must not reopen it.
            if ((DateTime.UtcNow - hiddenUtc).TotalMilliseconds < 250)
            {
                return;
            }

            popup.ShowAtCursor();
        }

        internal void RefreshAsync()
        {
            lock (gate)
            {
                if (refreshing)
                {
                    return;
                }

                refreshing = true;
            }

            var worker = new Thread(RefreshWorker);
            worker.IsBackground = true;
            worker.Start();
        }

        private void RefreshWorker()
        {
            List<PeripheralBattery> result = null;
            string error = null;

            try
            {
                result = BatteryService.ReadAll();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            lock (gate)
            {
                pendingResult = result;
                pendingError = error;
                refreshing = false;
            }
        }

        private void ApplyPending()
        {
            List<PeripheralBattery> result;
            string error;

            lock (gate)
            {
                result = pendingResult;
                error = pendingError;
                pendingResult = null;
                pendingError = null;
            }

            if (result == null && error == null)
            {
                return;
            }

            if (error != null)
            {
                popup.SetError("读取失败：" + error);
                return;
            }

            lastDevices = result;
            popup.SetData(result, DateTime.UtcNow);
            ApplyIcon(result);

            if (widget != null && widget.Visible)
            {
                widget.Update(result);
            }
        }

        private void ApplyIcon(List<PeripheralBattery> devices)
        {
            int? lowest = null;
            foreach (PeripheralBattery device in devices)
            {
                if (device.Percent.HasValue && (!lowest.HasValue || device.Percent.Value < lowest.Value))
                {
                    lowest = device.Percent;
                }
            }

            Icon previous = currentIcon;
            currentIcon = TrayIcons.Create(lowest);
            notifyIcon.Icon = currentIcon;
            if (previous != null)
            {
                previous.Dispose();
            }

            notifyIcon.Text = BuildTooltip(devices);
        }

        private static string BuildTooltip(List<PeripheralBattery> devices)
        {
            if (devices.Count == 0)
            {
                return "外设电量：未发现设备";
            }

            var parts = new List<string>();
            foreach (PeripheralBattery device in devices)
            {
                parts.Add(device.Name + " " + (device.Percent.HasValue ? device.Percent.Value + "%" : "—"));
            }

            string text = "外设电量：" + string.Join(" · ", parts.ToArray());
            return text.Length > 63 ? text.Substring(0, 62) + "…" : text;
        }

        private void ToggleStartup()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null)
                    {
                        return;
                    }

                    if (IsStartupEnabled())
                    {
                        key.DeleteValue(RunValueName, false);
                    }
                    else
                    {
                        key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\" --tray");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置开机启动失败：" + ex.Message, "外设电量");
            }
        }

        internal static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    return key != null && key.GetValue(RunValueName) != null;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Quit()
        {
            Log.Write("quit requested");
            notifyIcon.Visible = false;
            Application.Exit();
        }

        public void Dispose()
        {
            disposed = true;
            showTimer.Dispose();
            refreshTimer.Dispose();
            applyTimer.Dispose();
            notifyIcon.Dispose();
            if (currentIcon != null)
            {
                currentIcon.Dispose();
            }

            if (widget != null)
            {
                widget.Dispose();
            }

            menu.Dispose();
            popup.Dispose();
        }
    }
}
