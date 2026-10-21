using System;
using System.Collections.Generic;
using System.Text;
using BatteryPeek.Native;

namespace BatteryPeek
{
    /// <summary>
    /// Prints what the tool can see: HID collections, Bluetooth battery properties and the
    /// raw bytes of the VGN status feature report. Useful when a device reports nothing.
    /// </summary>
    internal static class Diagnose
    {
        internal static void Run()
        {
            Console.WriteLine("== HID 集合（VID 0x391D / 0x046D 与含 feature 报告的集合）==");
            foreach (HidInterface hid in HidDevices.Enumerate())
            {
                bool interesting = hid.VendorId == 0x391D || hid.VendorId == 0x046D
                    || (hid.UsagePage >= 0xFF00 && hid.FeatureReportLength > 0);

                if (!interesting)
                {
                    continue;
                }

                Console.WriteLine(string.Format("  VID_{0:X4} PID_{1:X4}  UP=0x{2:X2} U=0x{3:X2}  IN={4,3} OUT={5,3} FEAT={6,4}  {7}",
                    hid.VendorId, hid.ProductId, hid.UsagePage, hid.Usage,
                    hid.InputReportLength, hid.OutputReportLength, hid.FeatureReportLength, hid.ProductName));
            }

            Console.WriteLine();
            Console.WriteLine("== 蓝牙设备 ==");
            foreach (string deviceId in SetupApi.EnumeratePresentDeviceIds())
            {
                if (!deviceId.StartsWith("BTHENUM\\DEV_", StringComparison.OrdinalIgnoreCase) &&
                    !deviceId.StartsWith("BTHLE\\DEV_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                uint? devInst = SetupApi.Locate(deviceId);
                if (devInst == null)
                {
                    continue;
                }

                string name = SetupApi.GetString(devInst.Value, DeviceKeys.FriendlyName)
                    ?? SetupApi.GetString(devInst.Value, DeviceKeys.BusReportedDesc) ?? "(无名称)";
                int? battery = SetupApi.GetInt(devInst.Value, DeviceKeys.BluetoothBattery);

                Console.WriteLine(string.Format("  {0,-34} 电量属性={1}", name,
                    battery.HasValue ? battery.Value + "%" : "未上报"));
            }

            Console.WriteLine();
            Console.WriteLine("== VGN 状态 feature 报告原始字节 ==");
            foreach (HidInterface hid in HidDevices.Enumerate())
            {
                if (hid.VendorId != 0x391D || hid.FeatureReportLength < 2)
                {
                    continue;
                }

                Console.WriteLine(string.Format("  {0}  UP=0x{1:X2} U=0x{2:X2} FEAT={3}",
                    hid.ProductName, hid.UsagePage, hid.Usage, hid.FeatureReportLength));

                using (HidConnection connection = HidConnection.Open(hid))
                {
                    if (connection == null)
                    {
                        Console.WriteLine("    (无法打开)");
                        continue;
                    }

                    for (int reportId = 0; reportId <= 0x0F; reportId++)
                    {
                        var report = new byte[hid.FeatureReportLength];
                        report[0] = (byte)reportId;
                        if (!connection.GetFeature(report))
                        {
                            continue;
                        }

                        Console.WriteLine("    id=0x" + reportId.ToString("X2") + " : " + Hex(report));
                    }
                }
            }
        }

        private static string Hex(byte[] data)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < data.Length; i++)
            {
                builder.Append(data[i].ToString("X2"));
                builder.Append(' ');
            }

            return builder.ToString().Trim();
        }
    }
}
