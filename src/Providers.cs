using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using BatteryPeek.Native;

namespace BatteryPeek.Providers
{
    internal interface IBatteryProvider
    {
        /// <summary>Reads every peripheral this provider knows how to talk to. Never throws.</summary>
        IEnumerable<PeripheralBattery> Read();
    }

    /// <summary>
    /// Bluetooth peripherals: reads the same PnP property Windows itself shows in Settings
    /// (DEVPKEY_Bluetooth_Battery).
    ///
    /// The value can live on different nodes: the paired device itself, an LE device, or -
    /// for headsets - the Hands-Free profile (uuid 0x111E) node, which is where Windows puts
    /// the level the headset reports over AT+IPHONEACCEV. All three are scanned and merged by
    /// display name.
    /// </summary>
    internal sealed class BluetoothProvider : IBatteryProvider
    {
        private const string HandsFreeProfileId = "{0000111E-0000-1000-8000-00805F9B34FB}";

        public IEnumerable<PeripheralBattery> Read()
        {
            var byName = new Dictionary<string, PeripheralBattery>(StringComparer.OrdinalIgnoreCase);

            foreach (string deviceId in SetupApi.EnumeratePresentDeviceIds())
            {
                bool isClassic = deviceId.StartsWith("BTHENUM\\DEV_", StringComparison.OrdinalIgnoreCase);
                bool isLowEnergy = deviceId.StartsWith("BTHLE\\DEV_", StringComparison.OrdinalIgnoreCase);
                bool isHandsFree = deviceId.IndexOf(HandsFreeProfileId, StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isClassic && !isLowEnergy && !isHandsFree)
                {
                    continue;
                }

                uint? devInst = SetupApi.Locate(deviceId);
                if (devInst == null)
                {
                    continue;
                }

                string name = SetupApi.GetString(devInst.Value, DeviceKeys.FriendlyName);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = SetupApi.GetString(devInst.Value, DeviceKeys.BusReportedDesc);
                }

                name = CleanBluetoothName(name);
                if (name.Length == 0)
                {
                    continue;
                }

                int? percent = SetupApi.GetInt(devInst.Value, DeviceKeys.BluetoothBattery);
                string transport = isLowEnergy ? "蓝牙 LE" : "蓝牙";
                string source = isHandsFree ? "Windows PnP · HFP" : "Windows PnP";

                PeripheralBattery existing;
                if (byName.TryGetValue(name, out existing))
                {
                    // Keep whichever node actually carries a level; the plain paired-device node
                    // is often blank while the HFP node has the headset's own report.
                    if (!existing.Percent.HasValue && percent.HasValue)
                    {
                        existing.Percent = percent;
                        existing.State = ChargeState.Discharging;
                        existing.Source = source;
                    }

                    continue;
                }

                byName[name] = new PeripheralBattery
                {
                    Name = name,
                    Transport = transport,
                    Source = source,
                    Percent = percent,
                    State = percent.HasValue ? ChargeState.Discharging : ChargeState.Unknown
                };
            }

            return new List<PeripheralBattery>(byName.Values);
        }

        /// <summary>Strips the profile suffixes Windows appends to a paired device's name.</summary>
        internal static string CleanBluetoothName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            string cleaned = name.Trim();
            string[] suffixes =
            {
                " Hands-Free AG", " Hands-Free", " Avrcp 传输", " Avrcp Target",
                " 音频接收端", " 音频发送端", " Audio Sink", " Audio Source",
                " 设备标识服务", " Device Identification Service", " 蓝牙外围设备"
            };

            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (string suffix in suffixes)
                {
                    if (cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        cleaned = cleaned.Substring(0, cleaned.Length - suffix.Length).Trim();
                        changed = true;
                    }
                }
            }

            if (cleaned.Length == 0 || cleaned == "蓝牙外围设备")
            {
                return string.Empty;
            }

            return cleaned;
        }
    }

    /// <summary>
    /// VGN 2.4G peripherals (VID 0x391D). Two command channels are implemented:
    ///
    /// Keyboards - HID collection 0xFF60 / 0x61, 63-byte frame:
    ///     [0]=command [1]=parameter [2]=0 [3]=totalPayloadLen [4]=packetIndex [5]=payloadLen
    ///     [6..61]=payload [62]=checksum = 255 - ((reportId + sum(first 62 bytes)) % 256)
    ///     Battery query = 0x87; reply [6]=payloadLen [7]=battery [8]=charging.
    ///
    /// Mice - HID collection 0xFF01 / 0x10, 64-byte payload:
    ///     [0]=0x55 [1]=command (0x30 = GetBattery) [2..]=0
    ///     reply [1]=0x30 [8]=battery [9]=wired flag (1 = on cable, no percentage).
    ///
    /// Both were verified against VGN's own web driver / VHUB readings.
    /// </summary>
    internal sealed class VgnProvider : IBatteryProvider
    {
        private const ushort VgnVendorId = 0x391D;
        private const int KeyboardUsagePage = 0xFF60;
        private const int KeyboardUsage = 0x61;
        private const int MouseUsagePage = 0xFF01;
        private const int MouseUsage = 0x10;
        private const byte KeyboardBatteryCommand = 0x87;
        private const byte MouseBatteryCommand = 0x30;
        private const byte MouseFrameHeader = 0x55;

        /// <summary>Report ids to try: the V98Pro V4 dongle answers on 9, the mouse dongle on 8.</summary>
        private static readonly byte[] ReportIdCandidates = { 9, 8, 0, 1, 7, 10 };

        public IEnumerable<PeripheralBattery> Read()
        {
            var results = new List<PeripheralBattery>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<HidInterface> interfaces = new List<HidInterface>();

            foreach (HidInterface hid in HidDevices.Enumerate())
            {
                if (hid.VendorId == VgnVendorId)
                {
                    interfaces.Add(hid);
                }
            }

            // Pass 1: command channels. A dongle exposes several collections under the same
            // product name, so a readable one has to be tried before the fallback row exists.
            foreach (HidInterface hid in interfaces)
            {
                if (!hid.SupportsOutput || !hid.SupportsInput)
                {
                    continue;
                }

                string name = CleanName(hid.ProductName);
                if (name.Length == 0 || seen.Contains(name))
                {
                    continue;
                }

                PeripheralBattery entry = null;
                if (hid.UsagePage == KeyboardUsagePage && hid.Usage == KeyboardUsage)
                {
                    entry = ReadKeyboard(hid, name);
                }
                else if (hid.UsagePage == MouseUsagePage && hid.Usage == MouseUsage)
                {
                    entry = ReadMouse(hid, name);
                }

                if (entry != null)
                {
                    seen.Add(name);
                    results.Add(entry);
                }
            }

            // Pass 2: anything left gets an honest "no supported protocol" row.
            foreach (HidInterface hid in interfaces)
            {
                string name = CleanName(hid.ProductName);
                if (name.Length == 0 || seen.Contains(name))
                {
                    continue;
                }

                seen.Add(name);
                results.Add(new PeripheralBattery
                {
                    Name = name,
                    Transport = IsDongle(hid.ProductName) ? "2.4G" : "USB",
                    Source = "VGN 私有协议",
                    Percent = null,
                    State = ChargeState.Unsupported
                });
            }

            return results;
        }

        private static bool IsDongle(string productName)
        {
            return productName.IndexOf("Dongle", StringComparison.OrdinalIgnoreCase) >= 0
                || productName.IndexOf("Receiver", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static PeripheralBattery ReadKeyboard(HidInterface hid, string name)
        {
            using (HidConnection connection = HidConnection.Open(hid))
            {
                if (connection == null)
                {
                    return null;
                }

                foreach (byte reportId in ReportIdCandidates)
                {
                    var report = new byte[hid.OutputReportLength];
                    if (report.Length < 64)
                    {
                        continue;
                    }

                    report[0] = reportId;
                    byte[] frame = BuildKeyboardFrame(reportId, KeyboardBatteryCommand, 0x00);
                    Array.Copy(frame, 0, report, 1, Math.Min(frame.Length, report.Length - 1));

                    if (!connection.Write(report))
                    {
                        continue;
                    }

                    byte[] reply = connection.Read(hid.InputReportLength, 700);
                    if (reply == null || reply.Length < 9)
                    {
                        continue;
                    }

                    int payloadLength = reply[6];
                    int battery = reply[7];
                    bool charging = reply[8] != 0;

                    // A sleeping keyboard makes the dongle answer without a payload.
                    if (payloadLength < 2 || battery > 100)
                    {
                        return null;
                    }

                    return new PeripheralBattery
                    {
                        Name = name,
                        Transport = "2.4G",
                        Source = "VGN 私有协议",
                        Percent = battery,
                        State = charging
                            ? (battery >= 100 ? ChargeState.Full : ChargeState.Charging)
                            : ChargeState.Discharging
                    };
                }
            }

            return null;
        }

        private static PeripheralBattery ReadMouse(HidInterface hid, string name)
        {
            using (HidConnection connection = HidConnection.Open(hid))
            {
                if (connection == null)
                {
                    return null;
                }

                foreach (byte reportId in ReportIdCandidates)
                {
                    var report = new byte[hid.OutputReportLength];
                    if (report.Length < 8)
                    {
                        continue;
                    }

                    report[0] = reportId;
                    report[1] = MouseFrameHeader;
                    report[2] = MouseBatteryCommand;

                    if (!connection.Write(report))
                    {
                        continue;
                    }

                    byte[] reply = connection.Read(hid.InputReportLength, 700);
                    if (reply == null || reply.Length < 11)
                    {
                        continue;
                    }

                    // reply[0] is the input report id, so the vendor payload starts at 1.
                    if (reply[2] != MouseBatteryCommand)
                    {
                        continue;
                    }

                    bool wired = reply[10] == 1;
                    int battery = reply[9];

                    if (wired)
                    {
                        // On the cable VGN itself reports no percentage.
                        return new PeripheralBattery
                        {
                            Name = name,
                            Transport = "USB",
                            Source = "VGN 私有协议",
                            Percent = null,
                            State = ChargeState.Charging
                        };
                    }

                    if (battery > 100)
                    {
                        return null;
                    }

                    return new PeripheralBattery
                    {
                        Name = name,
                        Transport = "2.4G",
                        Source = "VGN 私有协议",
                        Percent = battery,
                        State = ChargeState.Discharging
                    };
                }
            }

            return null;
        }

        /// <summary>Builds the 63-byte keyboard command frame.</summary>
        internal static byte[] BuildKeyboardFrame(byte reportId, byte command, byte parameter)
        {
            var frame = new byte[63];
            frame[0] = command;
            frame[1] = parameter;
            frame[2] = 0x00;
            frame[3] = 0x00; // total payload length for a single-packet command
            frame[4] = 0x00; // packet index
            frame[5] = 0x00; // payload length

            int sum = reportId;
            for (int i = 0; i < 62; i++)
            {
                sum += frame[i];
            }

            frame[62] = (byte)(255 - (sum % 256));
            return frame;
        }

        /// <summary>Turns "VGN V98Pro V4 2.4G Dongle" into "VGN V98Pro V4".</summary>
        internal static string CleanName(string productName)
        {
            if (string.IsNullOrWhiteSpace(productName))
            {
                return string.Empty;
            }

            string name = productName.Trim();
            string[] suffixes = { " 8K 2.4G Dongle", " 1K 2.4G Dongle", " 2.4G Dongle", " 2.4G Receiver", " Dongle", " Receiver" };
            foreach (string suffix in suffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return name.Substring(0, name.Length - suffix.Length).Trim();
                }
            }

            return name;
        }
    }

    /// <summary>
    /// Logitech HID++ 2.0 battery reader (Lightspeed / Bolt / Unifying receivers and
    /// cable-connected mice). Feature 0x1000 gives a percentage, 0x1001 a voltage we map.
    /// </summary>
    internal sealed class LogitechProvider : IBatteryProvider
    {
        private const ushort LogitechVendorId = 0x046D;
        private const byte ShortReportId = 0x10;
        private const byte SwId = 0x01;
        private const ushort FeatureBatteryStatus = 0x1000;
        private const ushort FeatureBatteryVoltage = 0x1001;

        public IEnumerable<PeripheralBattery> Read()
        {
            var results = new List<PeripheralBattery>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (HidInterface hid in HidDevices.Enumerate())
            {
                if (hid.VendorId != LogitechVendorId)
                {
                    continue;
                }

                // HID++ lives on the vendor collection with a 7-byte short report.
                if (hid.UsagePage != 0xFF00 || hid.Usage < 1 || hid.Usage > 2)
                {
                    continue;
                }

                if (hid.FeatureReportLength < 7 && hid.OutputReportLength < 7)
                {
                    continue;
                }

                PeripheralBattery entry = Probe(hid);
                if (entry != null && seenNames.Add(entry.Name))
                {
                    results.Add(entry);
                }
            }

            return results;
        }

        private static PeripheralBattery Probe(HidInterface hid)
        {
            using (HidConnection connection = HidConnection.Open(hid))
            {
                if (connection == null)
                {
                    return null;
                }

                // A receiver answers for up to six paired devices plus itself (0xFF);
                // a mouse on its own cable answers on 0xFF.
                var candidates = new List<byte> { 0xFF, 1, 2, 3, 4, 5, 6 };

                foreach (byte deviceIndex in candidates)
                {
                    PeripheralBattery entry = ProbeDevice(connection, hid, deviceIndex);
                    if (entry != null)
                    {
                        return entry;
                    }
                }
            }

            return null;
        }

        private static PeripheralBattery ProbeDevice(HidConnection connection, HidInterface hid, byte deviceIndex)
        {
            byte featureIndex = GetFeatureIndex(connection, deviceIndex, FeatureBatteryStatus);
            if (featureIndex != 0)
            {
                byte[] reply = Request(connection, deviceIndex, featureIndex, 0x00);
                if (reply == null || reply.Length < 6)
                {
                    return null;
                }

                int level = reply[4];
                int status = reply[5];

                if (level > 100)
                {
                    return null;
                }

                return new PeripheralBattery
                {
                    Name = CleanName(hid.ProductName),
                    Transport = "2.4G",
                    Source = "Logitech HID++",
                    Percent = level,
                    State = status == 1 ? ChargeState.Charging : (status == 3 || level >= 100 ? ChargeState.Full : ChargeState.Discharging)
                };
            }

            featureIndex = GetFeatureIndex(connection, deviceIndex, FeatureBatteryVoltage);
            if (featureIndex == 0)
            {
                return null;
            }

            byte[] voltageReply = Request(connection, deviceIndex, featureIndex, 0x00);
            if (voltageReply == null || voltageReply.Length < 6)
            {
                return null;
            }

            int millivolts = (voltageReply[5] << 8) | voltageReply[4];
            int percent = PercentFromMillivolts(millivolts);

            return new PeripheralBattery
            {
                Name = CleanName(hid.ProductName),
                Transport = "2.4G",
                Source = "Logitech HID++ (电压换算)",
                Percent = percent,
                State = ChargeState.Discharging
            };
        }

        private static byte GetFeatureIndex(HidConnection connection, byte deviceIndex, ushort featureId)
        {
            byte[] reply = Request(connection, deviceIndex, 0x00, (byte)((featureId >> 8) & 0xFF), (byte)(featureId & 0xFF));
            if (reply == null || reply.Length < 5 || reply[2] != 0x00)
            {
                return 0;
            }

            return reply[4];
        }

        /// <summary>Sends one short HID++ message and waits for the matching reply.</summary>
        private static byte[] Request(HidConnection connection, byte deviceIndex, byte featureIndex, params byte[] parameters)
        {
            var message = new byte[7];
            message[0] = ShortReportId;
            message[1] = deviceIndex;
            message[2] = featureIndex;
            message[3] = SwId;

            for (int i = 0; i < parameters.Length && i + 4 < message.Length; i++)
            {
                message[i + 4] = parameters[i];
            }

            bool sent = connection.Interface.OutputReportLength >= 7
                ? connection.Write(message)
                : connection.SetFeature(PadToFeatureLength(connection.Interface, message));

            if (!sent)
            {
                return null;
            }

            for (int attempt = 0; attempt < 4; attempt++)
            {
                byte[] reply = connection.Read(connection.Interface.InputReportLength > 0 ? connection.Interface.InputReportLength : 20, 250);
                if (reply == null || reply.Length < 5)
                {
                    return null;
                }

                // Ignore traffic for other devices/features and unsolicited notifications.
                if (reply[1] != deviceIndex || reply[2] != featureIndex || (reply[3] & 0x0F) != SwId)
                {
                    continue;
                }

                return reply;
            }

            return null;
        }

        private static byte[] PadToFeatureLength(HidInterface hid, byte[] message)
        {
            var report = new byte[Math.Max(hid.FeatureReportLength, message.Length)];
            Array.Copy(message, report, Math.Min(message.Length, report.Length));
            return report;
        }

        private static int PercentFromMillivolts(int millivolts)
        {
            if (millivolts <= 0)
            {
                return 0;
            }

            // Rough Li-ion curve: fine for a glance, not a fuel gauge.
            int[][] curve =
            {
                new[] { 4200, 100 }, new[] { 4100, 95 }, new[] { 4000, 85 }, new[] { 3900, 75 },
                new[] { 3800, 60 }, new[] { 3700, 45 }, new[] { 3600, 25 }, new[] { 3500, 10 }, new[] { 3400, 3 }
            };

            foreach (int[] point in curve)
            {
                if (millivolts >= point[0])
                {
                    return point[1];
                }
            }

            return 1;
        }

        private static string CleanName(string productName)
        {
            if (string.IsNullOrWhiteSpace(productName))
            {
                return "Logitech 设备";
            }

            return productName.Trim();
        }
    }
}
