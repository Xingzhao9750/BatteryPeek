using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BatteryPeek.Native
{
    /// <summary>A single HID collection (one entry of SetupAPI's device interface list).</summary>
    internal sealed class HidInterface
    {
        internal string Path;
        internal ushort VendorId;
        internal ushort ProductId;
        internal int UsagePage;
        internal int Usage;
        internal int InputReportLength;
        internal int OutputReportLength;
        internal int FeatureReportLength;
        internal string ProductName = string.Empty;
        internal string Manufacturer = string.Empty;

        internal bool SupportsFeature {
            get { return FeatureReportLength > 0; }
        }

        internal bool SupportsOutput {
            get { return OutputReportLength > 0; }
        }

        internal bool SupportsInput {
            get { return InputReportLength > 0; }
        }
    }

    /// <summary>Enumerates HID collections and opens them for report I/O.</summary>
    internal static class HidDevices
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SpDeviceInterfaceData
        {
            public int Size;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDevInfoData
        {
            public int Size;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HiddAttributes
        {
            public int Size;
            public ushort VendorId;
            public ushort ProductId;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HidpCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;

            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        private const int DigcfPresent = 0x02;
        private const int DigcfDeviceInterface = 0x10;

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        private static extern bool HidD_GetProductString(IntPtr device, StringBuilder buffer, int bufferLength);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        private static extern bool HidD_GetManufacturerString(IntPtr device, StringBuilder buffer, int bufferLength);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetPreparsedData(IntPtr device, out IntPtr data);

        [DllImport("hid.dll")]
        private static extern bool HidD_FreePreparsedData(IntPtr data);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr data, ref HidpCaps caps);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetAttributes(IntPtr device, ref HiddAttributes attributes);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, int memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, ref SpDevInfoData deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        internal static List<HidInterface> Enumerate()
        {
            var result = new List<HidInterface>();
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);

            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (set == new IntPtr(-1))
            {
                return result;
            }

            try
            {
                for (int index = 0; ; index++)
                {
                    var interfaceData = new SpDeviceInterfaceData();
                    interfaceData.Size = Marshal.SizeOf(typeof(SpDeviceInterfaceData));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                    {
                        break;
                    }

                    HidInterface hidInterface = ReadInterface(set, ref interfaceData);
                    if (hidInterface != null)
                    {
                        result.Add(hidInterface);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }

            return result;
        }

        private static HidInterface ReadInterface(IntPtr set, ref SpDeviceInterfaceData interfaceData)
        {
            var devInfo = new SpDevInfoData();
            devInfo.Size = Marshal.SizeOf(typeof(SpDevInfoData));

            int required;
            SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, IntPtr.Zero, 0, out required, ref devInfo);
            if (required <= 0)
            {
                return null;
            }

            IntPtr detail = Marshal.AllocHGlobal(required);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA::cbSize is 8 on x64 and 6 on x86.
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, detail, required, out required, ref devInfo))
                {
                    return null;
                }

                string path = FindDevicePath(detail, required);
                if (path == null)
                {
                    return null;
                }

                return Describe(path);
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }

        /// <summary>
        /// The wide device path does not always start at a fixed offset in the detail
        /// buffer, so scan for its leading L"\\\\?\\" marker instead of trusting a layout.
        /// </summary>
        private static string FindDevicePath(IntPtr detail, int byteCount)
        {
            for (int offset = 4; offset < byteCount - 8; offset += 2)
            {
                if (Marshal.ReadInt16(detail, offset) == 0x005C && Marshal.ReadInt16(detail, offset + 2) == 0x005C)
                {
                    return Marshal.PtrToStringUni(IntPtr.Add(detail, offset));
                }
            }

            return null;
        }

        private static HidInterface Describe(string path)
        {
            IntPtr handle = NativeMethods.CreateFile(path, NativeMethods.GenericRead | NativeMethods.GenericWrite,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite, IntPtr.Zero,
                NativeMethods.OpenExisting, 0, IntPtr.Zero);

            if (handle == NativeMethods.InvalidHandle)
            {
                handle = NativeMethods.CreateFile(path, 0, NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                    IntPtr.Zero, NativeMethods.OpenExisting, 0, IntPtr.Zero);
            }

            if (handle == NativeMethods.InvalidHandle)
            {
                return null;
            }

            try
            {
                var attributes = new HiddAttributes();
                attributes.Size = Marshal.SizeOf(typeof(HiddAttributes));
                HidD_GetAttributes(handle, ref attributes);

                var result = new HidInterface
                {
                    Path = path,
                    VendorId = attributes.VendorId,
                    ProductId = attributes.ProductId
                };

                var product = new StringBuilder(256);
                if (HidD_GetProductString(handle, product, product.Capacity))
                {
                    result.ProductName = product.ToString();
                }

                var manufacturer = new StringBuilder(256);
                if (HidD_GetManufacturerString(handle, manufacturer, manufacturer.Capacity))
                {
                    result.Manufacturer = manufacturer.ToString();
                }

                IntPtr preparsed;
                if (HidD_GetPreparsedData(handle, out preparsed))
                {
                    var caps = new HidpCaps();
                    caps.Reserved = new ushort[17];
                    if (HidP_GetCaps(preparsed, ref caps) >= 0)
                    {
                        result.UsagePage = caps.UsagePage;
                        result.Usage = caps.Usage;
                        result.InputReportLength = caps.InputReportByteLength;
                        result.OutputReportLength = caps.OutputReportByteLength;
                        result.FeatureReportLength = caps.FeatureReportByteLength;
                    }

                    HidD_FreePreparsedData(preparsed);
                }

                return result;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }
    }

    /// <summary>An open HID collection with synchronous-looking report helpers.</summary>
    internal sealed class HidConnection : IDisposable
    {
        [DllImport("hid.dll")]
        private static extern bool HidD_GetFeature(IntPtr device, byte[] buffer, int bufferLength);

        [DllImport("hid.dll")]
        private static extern bool HidD_SetFeature(IntPtr device, byte[] buffer, int bufferLength);

        private IntPtr handle = NativeMethods.InvalidHandle;

        private HidConnection()
        {
        }

        internal HidInterface Interface { get; private set; }

        internal static HidConnection Open(HidInterface hidInterface)
        {
            IntPtr handle = NativeMethods.CreateFile(hidInterface.Path,
                NativeMethods.GenericRead | NativeMethods.GenericWrite,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero, NativeMethods.OpenExisting, NativeMethods.FileFlagOverlapped, IntPtr.Zero);

            if (handle == NativeMethods.InvalidHandle)
            {
                return null;
            }

            return new HidConnection { handle = handle, Interface = hidInterface };
        }

        internal bool SetFeature(byte[] report)
        {
            return HidD_SetFeature(handle, report, report.Length);
        }

        internal bool GetFeature(byte[] report)
        {
            return HidD_GetFeature(handle, report, report.Length);
        }

        internal bool Write(byte[] report)
        {
            IntPtr overlapped;
            IntPtr completedEvent = NativeMethods.BeginOverlapped(out overlapped);
            try
            {
                int written;
                bool ok = NativeMethods.WriteFile(handle, report, report.Length, out written, overlapped);
                if (!ok && Marshal.GetLastWin32Error() != NativeMethods.ErrorIoPending)
                {
                    return false;
                }

                return NativeMethods.WaitForSingleObject(completedEvent, 1000) == 0;
            }
            finally
            {
                NativeMethods.EndOverlapped(overlapped, completedEvent);
            }
        }

        internal byte[] Read(int length, int timeoutMs)
        {
            var buffer = new byte[length];
            IntPtr overlapped;
            IntPtr completedEvent = NativeMethods.BeginOverlapped(out overlapped);
            try
            {
                int read;
                bool ok = NativeMethods.ReadFile(handle, buffer, length, out read, overlapped);
                if (!ok && Marshal.GetLastWin32Error() != NativeMethods.ErrorIoPending)
                {
                    return null;
                }

                if (NativeMethods.WaitForSingleObject(completedEvent, (uint)timeoutMs) != 0)
                {
                    NativeMethods.CancelIo(handle);
                    return null;
                }

                int transferred = Marshal.ReadInt32(overlapped, 16);
                if (transferred <= 0 || transferred > buffer.Length)
                {
                    return buffer;
                }

                var trimmed = new byte[transferred];
                Array.Copy(buffer, trimmed, transferred);
                return trimmed;
            }
            finally
            {
                NativeMethods.EndOverlapped(overlapped, completedEvent);
            }
        }

        /// <summary>Reads every input report that arrives within the window (vendor status frames).</summary>
        internal List<byte[]> DrainInput(int timeoutMs, int maxReports)
        {
            var reports = new List<byte[]>();
            int length = Interface.InputReportLength > 0 ? Interface.InputReportLength : 64;

            for (int i = 0; i < maxReports; i++)
            {
                byte[] report = Read(length, timeoutMs);
                if (report == null)
                {
                    break;
                }

                reports.Add(report);
            }

            return reports;
        }

        public void Dispose()
        {
            if (handle != NativeMethods.InvalidHandle)
            {
                NativeMethods.CloseHandle(handle);
                handle = NativeMethods.InvalidHandle;
            }
        }
    }

    internal static class NativeMethods
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint FileShareRead = 0x00000001;
        internal const uint FileShareWrite = 0x00000002;
        internal const uint OpenExisting = 3;
        internal const uint FileFlagOverlapped = 0x40000000;
        internal const int ErrorIoPending = 997;

        internal static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadFile(IntPtr handle, byte[] buffer, int numberOfBytesToRead,
            out int numberOfBytesRead, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteFile(IntPtr handle, byte[] buffer, int numberOfBytesToWrite,
            out int numberOfBytesWritten, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, IntPtr name);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CancelIo(IntPtr handle);

        /// <summary>Allocates a zeroed OVERLAPPED whose hEvent is set, as Win32 requires for overlapped I/O.</summary>
        internal static IntPtr BeginOverlapped(out IntPtr overlapped)
        {
            IntPtr completedEvent = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            overlapped = Marshal.AllocHGlobal(32);
            for (int i = 0; i < 32; i++)
            {
                Marshal.WriteByte(overlapped, i, 0);
            }

            // hEvent is the fourth pointer-sized field of OVERLAPPED.
            Marshal.WriteIntPtr(overlapped, IntPtr.Size == 8 ? 24 : 16, completedEvent);
            return completedEvent;
        }

        internal static void EndOverlapped(IntPtr overlapped, IntPtr completedEvent)
        {
            Marshal.FreeHGlobal(overlapped);
            CloseHandle(completedEvent);
        }
    }
}
