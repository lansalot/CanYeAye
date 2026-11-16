using System.Reflection;
using System.Runtime.InteropServices;

namespace WinCAN.Connections.Transports;

/// <summary>
/// P/Invoke surface for the PCAN-Basic API. The driver owns USB enumeration, so a channel
/// handle such as <c>PCAN_USBBUS1</c> addresses the first PCAN-USB adapter.
/// <list type="bullet">
/// <item>Windows: PEAK's <c>PCANBasic.dll</c>.</item>
/// <item>macOS: MacCAN's <c>libPCBUSB.dylib</c> (https://www.mac-can.com), a PCAN-Basic
/// compatible user-space driver for PCAN-USB adapters (universal x86_64/arm64).</item>
/// <item>Linux: PEAK's <c>libpcanbasic.so</c> (PCAN-Basic for Linux over the pcan driver).</item>
/// </list>
/// All three export the same C functions and struct layouts, so only the library name differs;
/// it is mapped by a <see cref="DllImportResolver"/>.
/// </summary>
internal static class PcanNative
{
    private const string LibraryName = "PCANBasic.dll";

    static PcanNative() => NativeLibrary.SetDllImportResolver(typeof(PcanNative).Assembly, ResolveLibrary);

    /// <summary>Human-readable name of the driver library this platform needs.</summary>
    public static string DriverLibraryName =>
        OperatingSystem.IsMacOS() ? "libPCBUSB.dylib from mac-can.com"
        : OperatingSystem.IsLinux() ? "libpcanbasic.so (PEAK PCAN-Basic for Linux)"
        : "PCANBasic.dll";

    /// <summary>Candidate file names / paths for the PCAN-Basic library on this platform.</summary>
    internal static IReadOnlyList<string> LibraryCandidates()
    {
        if (OperatingSystem.IsMacOS())
        {
            return
            [
                Path.Combine(AppContext.BaseDirectory, "libPCBUSB.dylib"),
                "libPCBUSB.dylib",
                "/usr/local/lib/libPCBUSB.dylib",
                "/opt/homebrew/lib/libPCBUSB.dylib",
            ];
        }

        if (OperatingSystem.IsLinux())
        {
            return
            [
                Path.Combine(AppContext.BaseDirectory, "libpcanbasic.so"),
                "libpcanbasic.so",
                "libpcanbasic.so.4",
                "/usr/lib/libpcanbasic.so",
                "/usr/local/lib/libpcanbasic.so",
            ];
        }

        return [LibraryName];
    }

    /// <summary>True when the PCAN-Basic library for this platform can be loaded.</summary>
    public static bool IsDriverAvailable()
    {
        foreach (var candidate in LibraryCandidates())
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }

        return false;
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal) || OperatingSystem.IsWindows())
        {
            return IntPtr.Zero; // default probing
        }

        foreach (var candidate in LibraryCandidates())
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero; // default probing raises DllNotFoundException, handled by callers
    }

    public const uint ErrorOk = 0x00000;
    public const uint ErrorBusLight = 0x00004;
    public const uint ErrorBusHeavy = 0x00008;
    public const uint ErrorBusOff = 0x00010;
    public const uint ErrorQrcvEmpty = 0x00020;
    public const uint ErrorQOverrun = 0x00040;
    public const uint ErrorQxmtFull = 0x00080;
    public const uint ErrorNoDriver = 0x00200;
    public const uint ErrorHwInUse = 0x00400;
    public const uint ErrorNetInUse = 0x00800;
    public const uint ErrorIllHw = 0x01400;
    public const uint ErrorIllNet = 0x01800;
    public const uint ErrorIllClient = 0x01C00;
    public const uint ErrorResource = 0x02000;
    public const uint ErrorBusPassive = 0x40000;
    public const uint ErrorInitialize = 0x4000000;

    public const byte ParameterChannelCondition = 0x0D;
    public const byte ParameterHardwareName = 0x0E;

    public const uint ChannelConditionAvailable = 1;
    public const uint ChannelConditionOccupied = 2;

    public const byte MessageStandard = 0x00;
    public const byte MessageRemoteRequest = 0x01;
    public const byte MessageExtended = 0x02;

    public const ushort UsbBus1 = 0x0051;
    public const int MaxUsbChannels = 16;

    // Natural alignment, as in PCANBasic.h / PCBUSB.h: field offsets 0/4/5/6, sizeof == 16.
    [StructLayout(LayoutKind.Sequential)]
    public struct PcanMessage
    {
        public uint Id;
        public byte MessageType;
        public byte Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] Data;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PcanTimestamp
    {
        public uint Millis;
        public ushort MillisOverflow;
        public ushort Micros;
    }

    [DllImport(LibraryName, EntryPoint = "CAN_Initialize", CallingConvention = CallingConvention.Winapi)]
    public static extern uint Initialize(ushort channel, ushort bitTiming, uint hwType, uint ioPort, ushort interrupt);

    [DllImport(LibraryName, EntryPoint = "CAN_Uninitialize", CallingConvention = CallingConvention.Winapi)]
    public static extern uint Uninitialize(ushort channel);

    [DllImport(LibraryName, EntryPoint = "CAN_Read", CallingConvention = CallingConvention.Winapi)]
    public static extern uint Read(ushort channel, out PcanMessage message, out PcanTimestamp timestamp);

    [DllImport(LibraryName, EntryPoint = "CAN_Write", CallingConvention = CallingConvention.Winapi)]
    public static extern uint Write(ushort channel, ref PcanMessage message);

    [DllImport(LibraryName, EntryPoint = "CAN_GetValue", CallingConvention = CallingConvention.Winapi)]
    public static extern uint GetValue(ushort channel, byte parameter, out uint value, uint bufferLength);

    public static ushort ChannelHandle(int channel) => (ushort)(UsbBus1 + Math.Clamp(channel, 1, MaxUsbChannels) - 1);

    /// <summary>Maps a bit rate to PCAN-Basic's BTR0/BTR1 constant; null when unsupported.</summary>
    public static ushort? BitTiming(uint bitrate) => bitrate switch
    {
        1_000_000 => 0x0014,
        800_000 => 0x0016,
        500_000 => 0x001C,
        250_000 => 0x011C,
        125_000 => 0x031C,
        100_000 => 0x432F,
        95_000 => 0xC34E,
        83_333 => 0x852B,
        50_000 => 0x472F,
        47_619 => 0x1414,
        33_333 => 0x8B2F,
        20_000 => 0x532F,
        10_000 => 0x672F,
        5_000 => 0x7F7F,
        _ => null,
    };

    public static string Describe(uint status) => status switch
    {
        ErrorOk => "OK",
        ErrorQrcvEmpty => "receive queue empty",
        ErrorQOverrun => "receive queue overrun",
        ErrorQxmtFull => "transmit queue full",
        ErrorBusLight => "CAN bus warning (error counter high)",
        ErrorBusHeavy => "CAN bus heavy errors",
        ErrorBusOff => "CAN bus off",
        ErrorBusPassive => "CAN bus passive",
        ErrorNoDriver => $"PCAN driver not installed ({DriverLibraryName})",
        ErrorHwInUse => "PCAN channel is already in use by another application",
        ErrorNetInUse => "PCAN channel is in use",
        ErrorIllHw => "no PCAN hardware on this channel",
        ErrorIllNet => "invalid PCAN network channel",
        ErrorIllClient => "invalid PCAN client handle",
        ErrorResource => "insufficient PCAN resources",
        ErrorInitialize => "PCAN channel could not be initialized",
        _ => $"PCAN error 0x{status:X}",
    };

    /// <summary>Probes PCAN-USB channels 1..16; empty when the driver is missing.</summary>
    public static IReadOnlyList<int> GetAvailableChannels()
    {
        var channels = new List<int>();
        try
        {
            for (var index = 1; index <= MaxUsbChannels; index++)
            {
                var status = GetValue(ChannelHandle(index), ParameterChannelCondition, out var condition, 4);
                if (status != ErrorOk)
                {
                    continue;
                }

                if (condition is ChannelConditionAvailable or ChannelConditionOccupied)
                {
                    channels.Add(index);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return [];
        }

        return channels;
    }

    public static string GetHardwareName(int channel)
    {
        try
        {
            var buffer = new byte[256];
            var result = GetHardwareNameValue(ChannelHandle(channel), ParameterHardwareName, buffer, (uint)buffer.Length);
            if (result != ErrorOk)
            {
                return string.Empty;
            }

            var length = Array.IndexOf(buffer, (byte)0);
            if (length < 0)
            {
                length = buffer.Length;
            }

            return System.Text.Encoding.ASCII.GetString(buffer, 0, length).Trim();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return string.Empty;
        }
    }

    [DllImport(LibraryName, EntryPoint = "CAN_GetValue", CallingConvention = CallingConvention.Winapi)]
    private static extern uint GetHardwareNameValue(ushort channel, byte parameter, byte[] buffer, uint bufferLength);
}
