using System;
using System.Collections.Generic;
using BatteryPeek.Providers;

namespace BatteryPeek
{
    /// <summary>Runs every provider and merges the results into one list for the UI.</summary>
    internal static class BatteryService
    {
        internal static List<PeripheralBattery> ReadAll()
        {
            var merged = new List<PeripheralBattery>();
            var byName = new Dictionary<string, PeripheralBattery>(StringComparer.OrdinalIgnoreCase);

            Collect(new BluetoothProvider(), merged, byName);
            Collect(new VgnProvider(), merged, byName);
            Collect(new LogitechProvider(), merged, byName);

            merged.Sort(Compare);
            return merged;
        }

        private static void Collect(IBatteryProvider provider, List<PeripheralBattery> merged,
            Dictionary<string, PeripheralBattery> byName)
        {
            IEnumerable<PeripheralBattery> found;
            try
            {
                found = provider.Read();
            }
            catch (Exception)
            {
                // A single misbehaving device must not take the whole refresh down.
                return;
            }

            if (found == null)
            {
                return;
            }

            foreach (PeripheralBattery device in found)
            {
                if (device == null || string.IsNullOrWhiteSpace(device.Name))
                {
                    continue;
                }

                PeripheralBattery existing;
                if (byName.TryGetValue(device.Name, out existing))
                {
                    // Prefer the entry that actually knows the charge level.
                    if (!existing.Percent.HasValue && device.Percent.HasValue)
                    {
                        merged.Remove(existing);
                        merged.Add(device);
                        byName[device.Name] = device;
                    }

                    continue;
                }

                merged.Add(device);
                byName[device.Name] = device;
            }
        }

        private static int Compare(PeripheralBattery left, PeripheralBattery right)
        {
            if (left.Percent.HasValue != right.Percent.HasValue)
            {
                return left.Percent.HasValue ? -1 : 1;
            }

            return string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
