using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using System.Windows.Forms;
using BatteryPeek.Native;
using BatteryPeek.Providers;
using BatteryPeek.Ui;

namespace BatteryPeek
{
    internal static class Program
    {
        private const string MutexName = "Local\\BatteryPeek.SingleInstance";
        private const string ShowPanelEventName = "Local\\BatteryPeek.ShowPanel";

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr template);

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleOutputCP();

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);

        [STAThread]
        private static int Main(string[] args)
        {
            bool wantsConsole = args.Length > 0;

            if (wantsConsole)
            {
                AttachConsole(-1);
                RedirectConsole();
            }

            EnableDpiAwareness();

            if (HasFlag(args, "--once"))
            {
                PrintTable();
                return 0;
            }

            if (HasFlag(args, "--json"))
            {
                PrintJson();
                return 0;
            }

            if (HasFlag(args, "--diagnose"))
            {
                Diagnose.Run();
                return 0;
            }

            if (HasFlag(args, "--preview"))
            {
                return RunPreview();
            }

            if (HasFlag(args, "--dumpicon"))
            {
                return RunDumpIcon(args);
            }

            if (HasFlag(args, "--popup"))
            {
                return RunTray();
            }

            if (HasFlag(args, "--help") || HasFlag(args, "-h") || HasFlag(args, "/?"))
            {
                PrintUsage();
                return 0;
            }

            return RunTray();
        }

        private static int RunPreview()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var popup = new BatteryPopup();
            popup.SetData(BatteryService.ReadAll(), DateTime.UtcNow);
            popup.Location = new System.Drawing.Point(200, 160);
            popup.Show();
            popup.Activate();

            var closer = new System.Windows.Forms.Timer();
            closer.Interval = 6000;
            closer.Tick += delegate
            {
                closer.Stop();
                popup.Close();
                Application.Exit();
            };
            closer.Start();

            Application.Run();
            return 0;
        }

        /// <summary>Renders the tray glyphs to a PNG so the drawing can be inspected without the shell icon cache.</summary>
        private static int RunDumpIcon(string[] args)
        {
            string output = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--dumpicon", StringComparison.OrdinalIgnoreCase))
                {
                    output = args[i + 1];
                }
            }

            if (string.IsNullOrEmpty(output))
            {
                output = "icon.png";
            }

            int?[] levels = { 100, 45, 12, null };
            const int scale = 4;

            using (var sheet = new System.Drawing.Bitmap(32 * scale * levels.Length, 32 * scale))
            using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(sheet))
            {
                g.Clear(System.Drawing.Color.FromArgb(32, 34, 40));
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

                for (int i = 0; i < levels.Length; i++)
                {
                    using (System.Drawing.Icon icon = TrayIcons.Create(levels[i]))
                    using (System.Drawing.Bitmap bitmap = icon.ToBitmap())
                    {
                        g.DrawImage(bitmap, new System.Drawing.Rectangle(i * 32 * scale, 0, 32 * scale, 32 * scale));
                    }
                }

                sheet.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            }

            Console.WriteLine("已写入 " + output);
            return 0;
        }

        private static int RunTray()
        {
            bool created;
            var mutex = new Mutex(true, MutexName, out created);
            if (!created)
            {
                // Someone is already running: ask that instance to pop its panel instead of
                // exiting silently, otherwise a second double-click looks like "nothing happens".
                try
                {
                    EventWaitHandle running;
                    if (EventWaitHandle.TryOpenExisting(ShowPanelEventName, out running))
                    {
                        running.Set();
                        running.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("hand-off failed: " + ex.Message);
                }

                Log.Write("second instance started; handed over to the running one");
                return 0;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowPanelEventName))
                using (var app = new TrayApp(showEvent))
                {
                    Application.Run();
                }
            }
            catch (Exception ex)
            {
                Log.Write("fatal: " + ex);
                MessageBox.Show("外设电量启动失败：" + ex.Message + Environment.NewLine + Environment.NewLine
                    + "日志：" + Log.Path_, "外设电量", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                GC.KeepAlive(mutex);
            }

            return 0;
        }

        private static bool HasFlag(string[] args, string flag)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void EnableDpiAwareness()
        {
            try
            {
                SetProcessDpiAwareness(2); // PROCESS_PER_MONITOR_DPI_AWARE
            }
            catch (Exception)
            {
                // Older Windows builds do not export shcore.
            }
        }

        /// <summary>
        /// The exe is linked as a GUI application so double-clicking never flashes a console.
        /// When started from a terminal with arguments, borrow that console for the CLI modes.
        /// </summary>
        private static void RedirectConsole()
        {
            IntPtr stdout = CreateFile("CONOUT$", 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (stdout == new IntPtr(-1))
            {
                return;
            }

            Encoding encoding = GetConsoleEncoding();
            var stream = new FileStream(new SafeFileHandle(stdout, true), FileAccess.Write);
            var writer = new StreamWriter(stream, encoding) { AutoFlush = true };
            Console.SetOut(writer);
        }

        /// <summary>
        /// Write in the console's own code page: a Chinese Windows console expects GBK, and
        /// UTF-8 there would show up as mojibake in cmd/PowerShell.
        /// </summary>
        private static Encoding GetConsoleEncoding()
        {
            try
            {
                uint codePage = GetConsoleOutputCP();
                if (codePage != 0)
                {
                    return Encoding.GetEncoding((int)codePage);
                }
            }
            catch (Exception)
            {
                // Fall through to the ANSI default.
            }

            return Encoding.Default;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("外设电量 / BatteryPeek");
            Console.WriteLine();
            Console.WriteLine("  BatteryPeek.exe            托盘运行");
            Console.WriteLine("  BatteryPeek.exe --once     在终端打印一次");
            Console.WriteLine("  BatteryPeek.exe --json     以 JSON 输出");
            Console.WriteLine("  BatteryPeek.exe --diagnose 输出诊断信息（HID 集合与设备属性）");
        }

        private static void PrintTable()
        {
            Log.Write("cli --once invoked");
            List<PeripheralBattery> devices = BatteryService.ReadAll();
            if (devices.Count == 0)
            {
                Console.WriteLine("没有找到可读取电量的外设。");
                return;
            }

            Console.WriteLine(string.Format("{0,-24} {1,-10} {2,6}  {3,-12} {4}", "设备", "连接", "电量", "状态", "来源"));
            Console.WriteLine(new string('-', 78));

            foreach (PeripheralBattery device in devices)
            {
                string percent = device.Percent.HasValue ? device.Percent.Value + "%" : "—";
                string experimental = device.IsExperimental ? "（实验性）" : string.Empty;
                Console.WriteLine(string.Format("{0,-24} {1,-10} {2,6}  {3,-12} {4}{5}",
                    Clip(device.Name, 24), device.Transport, percent, device.StateText, device.Source, experimental));
            }
        }

        private static string Clip(string text, int width)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= width)
            {
                return text;
            }

            return text.Substring(0, width - 1) + "…";
        }

        private static void PrintJson()
        {
            List<PeripheralBattery> devices = BatteryService.ReadAll();
            var builder = new StringBuilder();
            builder.AppendLine("[");

            for (int i = 0; i < devices.Count; i++)
            {
                PeripheralBattery device = devices[i];
                builder.Append("  {");
                builder.AppendFormat("\"name\":\"{0}\",", Escape(device.Name));
                builder.AppendFormat("\"transport\":\"{0}\",", Escape(device.Transport));
                builder.AppendFormat("\"percent\":{0},", device.Percent.HasValue ? device.Percent.Value.ToString() : "null");
                builder.AppendFormat("\"state\":\"{0}\",", device.State);
                builder.AppendFormat("\"source\":\"{0}\",", Escape(device.Source));
                builder.AppendFormat("\"experimental\":{0}", device.IsExperimental ? "true" : "false");
                builder.Append("}");
                builder.AppendLine(i == devices.Count - 1 ? string.Empty : ",");
            }

            builder.AppendLine("]");
            Console.WriteLine(builder.ToString());
        }

        private static string Escape(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
