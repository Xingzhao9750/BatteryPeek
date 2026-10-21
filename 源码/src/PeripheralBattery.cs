using System;

namespace BatteryPeek
{
    internal enum ChargeState
    {
        /// <summary>Nothing reported, or the device does not expose it.</summary>
        Unknown,

        /// <summary>
        /// Paired and reachable, but all we know is the last level the device reported - not
        /// whether it is actually being used right now. Claiming "in use" would be a lie.
        /// </summary>
        Connected,

        /// <summary>Paired, but the Bluetooth radio says there is no live link right now.</summary>
        Disconnected,

        /// <summary>The device reports a level and is running on its cell.</summary>
        Discharging,

        /// <summary>The device is on cable and topping up.</summary>
        Charging,

        /// <summary>The device is on cable and the gauge says 100%.</summary>
        Full,

        /// <summary>We can see the peripheral but no supported protocol exposes its charge.</summary>
        Unsupported
    }

    /// <summary>One row in the popup: a peripheral and whatever battery it exposes.</summary>
    internal sealed class PeripheralBattery
    {
        internal string Name { get; set; }
        internal string Transport { get; set; }
        internal int? Percent { get; set; }
        internal ChargeState State { get; set; }
        internal string Source { get; set; }

        /// <summary>Set when the value comes from a protocol that is not vendor-verified yet.</summary>
        internal bool IsExperimental { get; set; }

        internal PeripheralBattery()
        {
            Name = string.Empty;
            Transport = string.Empty;
            Source = string.Empty;
            State = ChargeState.Unknown;
        }

        internal string StateText
        {
            get
            {
                switch (State)
                {
                    case ChargeState.Charging: return "充电中";
                    case ChargeState.Full: return "已充满";
                    case ChargeState.Connected: return "已连接";
                    case ChargeState.Disconnected: return "未连接";
                    case ChargeState.Discharging: return "电池供电";
                    case ChargeState.Unsupported: return "需厂商协议";
                    default: return "未上报";
                }
            }
        }
    }
}
