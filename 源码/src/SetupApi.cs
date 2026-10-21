using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BatteryPeek.Native
{
    /// <summary>
    /// Minimal cfgmgr32 wrapper. Reading PnP device properties through cfgmgr32 avoids
    /// WMI entirely, which keeps start-up fast and works without any service dependency.
    /// </summary>
    internal static class SetupApi
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct DevPropKey
        {
            public Guid FormatId;
            public uint PropertyId;

            public DevPropKey(string formatId, uint propertyId)
            {
                FormatId = new Guid(formatId);
                PropertyId = propertyId;
            }
        }

        private const int CrSuccess = 0;
        private const int CrBufferSmall = 26;
        private const uint GetIdListFilterPresent = 0x00000100;
        private const uint LocateDevNodeNormal = 0;

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_List_SizeW(out uint length, IntPtr filter, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_ListW(IntPtr filter, [Out] char[] buffer, uint length, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(uint devInst, [Out] char[] buffer, uint length, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint propertyType, [Out] byte[] buffer, ref uint size, uint flags);

        internal static IEnumerable<string> EnumeratePresentDeviceIds()
        {
            uint charCount;
            if (CM_Get_Device_ID_List_SizeW(out charCount, IntPtr.Zero, GetIdListFilterPresent) != CrSuccess)
            {
                yield break;
            }

            var buffer = new char[charCount];
            if (CM_Get_Device_ID_ListW(IntPtr.Zero, buffer, charCount, GetIdListFilterPresent) != CrSuccess)
            {
                yield break;
            }

            int start = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] != '\0')
                {
                    continue;
                }

                if (i > start)
                {
                    yield return new string(buffer, start, i - start);
                }

                start = i + 1;

                // The list is double-null terminated.
                if (start < buffer.Length && buffer[start] == '\0')
                {
                    break;
                }
            }
        }

        internal static uint? Locate(string deviceId)
        {
            uint devInst;
            return CM_Locate_DevNodeW(out devInst, deviceId, LocateDevNodeNormal) == CrSuccess ? devInst : (uint?)null;
        }

        internal static string GetDeviceId(uint devInst)
        {
            var buffer = new char[512];
            return CM_Get_Device_IDW(devInst, buffer, 512, 0) == CrSuccess ? new string(buffer).TrimEnd('\0') : null;
        }

        internal static byte[] GetPropertyBytes(uint devInst, DevPropKey key)
        {
            uint size = 0;
            uint type;
            int result = CM_Get_DevNode_PropertyW(devInst, ref key, out type, null, ref size, 0);
            if (result != CrBufferSmall || size == 0)
            {
                return null;
            }

            var buffer = new byte[size];
            result = CM_Get_DevNode_PropertyW(devInst, ref key, out type, buffer, ref size, 0);
            if (result != CrSuccess)
            {
                return null;
            }

            if (buffer.Length != size)
            {
                var trimmed = new byte[size];
                Array.Copy(buffer, trimmed, size);
                return trimmed;
            }

            return buffer;
        }

        internal static string GetString(uint devInst, DevPropKey key)
        {
            byte[] raw = GetPropertyBytes(devInst, key);
            if (raw == null || raw.Length < 2)
            {
                return null;
            }

            return Encoding.Unicode.GetString(raw).TrimEnd('\0');
        }

        internal static int? GetInt(uint devInst, DevPropKey key)
        {
            byte[] raw = GetPropertyBytes(devInst, key);
            if (raw == null || raw.Length == 0)
            {
                return null;
            }

            switch (raw.Length)
            {
                case 1: return raw[0];
                case 2: return BitConverter.ToUInt16(raw, 0);
                case 4: return (int)BitConverter.ToUInt32(raw, 0);
                default: return null;
            }
        }
    }

    /// <summary>Device property keys used by the battery readers.</summary>
    internal static class DeviceKeys
    {
        /// <summary>DEVPKEY_Device_FriendlyName.</summary>
        internal static readonly SetupApi.DevPropKey FriendlyName =
            new SetupApi.DevPropKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);

        /// <summary>DEVPKEY_Device_DeviceDesc.</summary>
        internal static readonly SetupApi.DevPropKey DeviceDesc =
            new SetupApi.DevPropKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);

        /// <summary>DEVPKEY_Device_Class.</summary>
        internal static readonly SetupApi.DevPropKey ClassName =
            new SetupApi.DevPropKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 9);

        /// <summary>DEVPKEY_Device_BusReportedDeviceDesc.</summary>
        internal static readonly SetupApi.DevPropKey BusReportedDesc =
            new SetupApi.DevPropKey("540B947E-8B40-45BC-A8A2-6A0B894CBDA2", 4);

        /// <summary>DEVPKEY_Device_Parent - used to tell which bus owns a child device.</summary>
        internal static readonly SetupApi.DevPropKey Parent =
            new SetupApi.DevPropKey("4340A6C5-93FA-4706-972C-7B648008A5A7", 2);

        /// <summary>DEVPKEY_Bluetooth_Battery - the value Windows shows in Settings.</summary>
        internal static readonly SetupApi.DevPropKey BluetoothBattery =
            new SetupApi.DevPropKey("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    }
}
