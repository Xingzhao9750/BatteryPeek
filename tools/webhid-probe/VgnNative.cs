using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Native confirmation of the VGN battery query: 63-byte frame on report id 9,
// checksum = 255 - ((reportId + sum(first 62 bytes)) % 256), battery in payload[0].
internal static class VgnNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData { public int Size; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData { public int Size; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes { public int Size; public ushort VendorId; public ushort ProductId; public ushort Version; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage; public ushort UsagePage; public ushort InLen; public ushort OutLen; public ushort FeatLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort Rest0; public ushort InBC; public ushort InVC; public ushort InDI;
        public ushort OutBC; public ushort OutVC; public ushort OutDI; public ushort FeatBC; public ushort FeatVC; public ushort FeatDI;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(IntPtr d, out IntPtr p);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr p, ref HidpCaps c);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(IntPtr d, ref HiddAttributes a);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] private static extern bool HidD_GetProductString(IntPtr d, StringBuilder b, int n);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, int f);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SpDeviceInterfaceData p);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SpDeviceInterfaceData d, IntPtr p, int sz, out int req, ref SpDevInfoData di);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(IntPtr h, byte[] b, int n, out int r, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(IntPtr h, byte[] b, int n, out int w, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateEvent(IntPtr a, bool m, bool i, IntPtr n);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIo(IntPtr h);

    private static readonly IntPtr Invalid = new IntPtr(-1);
    private const uint Gr = 0x80000000, Gw = 0x40000000, Share = 3, Open = 3, OvFlag = 0x40000000;

    private sealed class Iface { public string Path; public string Product; public ushort Vid, Pid; public int Up, U, In, Out; }

    private static List<Iface> Enumerate()
    {
        var list = new List<Iface>();
        Guid g; HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (set == Invalid) return list;

        for (int i = 0; ; i++)
        {
            var data = new SpDeviceInterfaceData(); data.Size = Marshal.SizeOf(typeof(SpDeviceInterfaceData));
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref data)) break;
            var di = new SpDevInfoData(); di.Size = Marshal.SizeOf(typeof(SpDevInfoData));
            int req;
            SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out req, ref di);
            if (req <= 0) continue;
            IntPtr det = Marshal.AllocHGlobal(req);
            try
            {
                Marshal.WriteInt32(det, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref data, det, req, out req, ref di)) continue;
                string path = null;
                for (int off = 4; off < req - 8; off += 2)
                    if (Marshal.ReadInt16(det, off) == 0x5C && Marshal.ReadInt16(det, off + 2) == 0x5C) { path = Marshal.PtrToStringUni(IntPtr.Add(det, off)); break; }
                if (path == null) continue;

                IntPtr h = CreateFile(path, Gr | Gw, Share, IntPtr.Zero, Open, 0, IntPtr.Zero);
                if (h == Invalid) h = CreateFile(path, 0, Share, IntPtr.Zero, Open, 0, IntPtr.Zero);
                if (h == Invalid) continue;
                try
                {
                    var attr = new HiddAttributes(); attr.Size = Marshal.SizeOf(typeof(HiddAttributes));
                    HidD_GetAttributes(h, ref attr);
                    var iface = new Iface { Path = path, Vid = attr.VendorId, Pid = attr.ProductId };
                    var product = new StringBuilder(256);
                    HidD_GetProductString(h, product, product.Capacity);
                    iface.Product = product.ToString();

                    IntPtr pp;
                    if (HidD_GetPreparsedData(h, out pp))
                    {
                        var caps = new HidpCaps(); caps.Reserved = new ushort[17];
                        if (HidP_GetCaps(pp, ref caps) >= 0) { iface.Up = caps.UsagePage; iface.U = caps.Usage; iface.In = caps.InLen; iface.Out = caps.OutLen; }
                        HidD_FreePreparsedData(pp);
                    }
                    list.Add(iface);
                }
                finally { CloseHandle(h); }
            }
            finally { Marshal.FreeHGlobal(det); }
        }
        SetupDiDestroyDeviceInfoList(set);
        return list;
    }

    private static string Hex(byte[] b) { var sb = new StringBuilder(); foreach (byte x in b) sb.Append(x.ToString("X2")).Append(' '); return sb.ToString().Trim(); }

    /// <summary>Builds the 63-byte command frame the vendor protocol expects.</summary>
    private static byte[] BuildFrame(byte reportId, byte command, byte parameter)
    {
        var frame = new byte[63];
        frame[0] = command;
        frame[1] = parameter;
        frame[2] = 0x00;
        frame[3] = 0x00; // total payload length (single packet)
        frame[4] = 0x00; // packet index
        frame[5] = 0x00; // payload length

        int sum = reportId;
        for (int i = 0; i < 62; i++) sum += frame[i];
        frame[62] = (byte)(255 - (sum % 256));
        return frame;
    }

    private static byte[] ReadOnce(IntPtr h, int len, int timeout)
    {
        var buffer = new byte[len];
        IntPtr ev = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
        IntPtr ov = Marshal.AllocHGlobal(32);
        for (int i = 0; i < 32; i++) Marshal.WriteByte(ov, i, 0);
        Marshal.WriteIntPtr(ov, IntPtr.Size == 8 ? 24 : 16, ev);
        try
        {
            int read;
            bool ok = ReadFile(h, buffer, len, out read, ov);
            if (!ok && Marshal.GetLastWin32Error() != 997) return null;
            if (WaitForSingleObject(ev, (uint)timeout) != 0) { CancelIo(h); return null; }
            int n = Marshal.ReadInt32(ov, 8);
            if (n <= 0 || n > buffer.Length) return null;
            var res = new byte[n];
            Array.Copy(buffer, res, n);
            return res;
        }
        finally { Marshal.FreeHGlobal(ov); CloseHandle(ev); }
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ushort vid = args.Length > 0 ? Convert.ToUInt16(args[0], 16) : (ushort)0x391D;
        ushort pid = args.Length > 1 ? Convert.ToUInt16(args[1], 16) : (ushort)0x2A04;

        foreach (Iface iface in Enumerate())
        {
            if (iface.Vid != vid || iface.Pid != pid) continue;
            if (iface.Up != 0xFF60 || iface.U != 0x61) continue;

            Console.WriteLine(string.Format("{0}  0x{1:X2}/0x{2:X2} IN={3} OUT={4}", iface.Product, iface.Up, iface.U, iface.In, iface.Out));

            IntPtr handle = CreateFile(iface.Path, Gr | Gw, Share, IntPtr.Zero, Open, OvFlag, IntPtr.Zero);
            if (handle == Invalid) { Console.WriteLine("打开失败 " + Marshal.GetLastWin32Error()); continue; }
            try
            {
                const byte reportId = 9;
                byte[] frame = BuildFrame(reportId, 0x87, 0x00);

                // Windows wants the report id in front of the payload.
                var report = new byte[64];
                report[0] = reportId;
                Array.Copy(frame, 0, report, 1, 63);

                IntPtr ev = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
                IntPtr ov = Marshal.AllocHGlobal(32);
                for (int i = 0; i < 32; i++) Marshal.WriteByte(ov, i, 0);
                Marshal.WriteIntPtr(ov, IntPtr.Size == 8 ? 24 : 16, ev);
                try
                {
                    int written;
                    bool ok = WriteFile(handle, report, report.Length, out written, ov);
                    int err = Marshal.GetLastWin32Error();
                    if (!ok && err != 997) { Console.WriteLine("写入失败 err=" + err); return 3; }
                    WaitForSingleObject(ev, 500);
                }
                finally { Marshal.FreeHGlobal(ov); CloseHandle(ev); }

                byte[] reply = ReadOnce(handle, iface.In, 800);
                if (reply == null) { Console.WriteLine("没有回包"); return 4; }

                Console.WriteLine("回包: " + Hex(reply));
                // The input report starts with the report id, so every frame index shifts by one.
                int payloadLength = reply[6];
                int battery = reply[7];
                int charging = reply[8];
                Console.WriteLine(string.Format("payloadLen={0}  电量={1}%  充电={2}", payloadLength, battery, charging));
                return battery >= 0 && battery <= 100 ? 0 : 5;
            }
            finally { CloseHandle(handle); }
        }

        Console.WriteLine("没找到 0xFF60/0x61 接口");
        return 2;
    }
}
