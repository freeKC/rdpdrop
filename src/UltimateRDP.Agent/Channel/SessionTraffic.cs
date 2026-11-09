using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using UltimateRDP.Agent.Interop;

namespace UltimateRDP.Agent.Channel;

/// <summary>The traffic counters of the session, from WTSSessionInfoEx.</summary>
/// <param name="IncomingBytes">Bytes received from the client.</param>
/// <param name="OutgoingBytes">Bytes sent to the client.</param>
/// <param name="IncomingFrames">Frames received from the client.</param>
/// <param name="IncomingCompressedBytes">Compressed bytes received from the client.</param>
internal readonly record struct SessionCounters(uint IncomingBytes, uint OutgoingBytes, uint IncomingFrames, uint IncomingCompressedBytes);

/// <summary>
/// What Windows says about the session and its connection, for the log: the client
/// address, the name of the connection and the traffic counters. The transport (TCP or
/// UDP) is not available here; the connection bar of the client shows it.
/// </summary>
/// <remarks>
/// The counters of WTSSessionInfoEx are 32 bit and may stay at zero for a Remote Desktop
/// connection; they are logged as Windows gives them.
/// </remarks>
internal static unsafe class SessionTraffic
{
    private const int AddressFamilyInet = 2;
    private const int AddressFamilyInet6 = 23;

    /// <summary>One line about the connection of the session, or the reason it is unknown.</summary>
    public static string Describe()
    {
        var address = ClientAddress() ?? "unknown";
        var info = Query();
        if (info is not { } value)
        {
            return $"client address {address}; WTSSessionInfoEx is not available.";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"client address {address}, connection {value.Station}, session flags {value.Flags}; counters from WTSSessionInfoEx: {value.Counters.IncomingBytes} bytes and {value.Counters.IncomingFrames} frames in, {value.Counters.IncomingCompressedBytes} compressed bytes in, {value.Counters.OutgoingBytes} bytes out.");
    }

    /// <summary>The traffic counters of the session now, or null.</summary>
    public static SessionCounters? Counters() => Query()?.Counters;

    /// <summary>The address of the client as Windows gives it, or null.</summary>
    private static string? ClientAddress()
    {
        if (!WtsApi.WTSQuerySessionInformation(WtsApi.CurrentServerHandle, WtsApi.CurrentSession, WtsApi.ClientAddressInfo, out var buffer, out var size))
        {
            return null;
        }

        try
        {
            // WTS_CLIENT_ADDRESS: a DWORD of address family, then 20 bytes; an IPv4 address
            // starts at byte 2 of them. Where an IPv6 address starts is not documented
            // clearly, so its bytes are logged as they are.
            if (buffer == IntPtr.Zero || size < 24)
            {
                return null;
            }

            var family = Marshal.ReadInt32(buffer);
            var bytes = new ReadOnlySpan<byte>((byte*)buffer + 4, 20);
            return family switch
            {
                AddressFamilyInet => new IPAddress(bytes.Slice(2, 4)).ToString(),
                AddressFamilyInet6 => "IPv6, bytes " + Convert.ToHexString(bytes),
                _ => string.Create(CultureInfo.InvariantCulture, $"of family {family}"),
            };
        }
        finally
        {
            WtsApi.WTSFreeMemory(buffer);
        }
    }

    private static (string Station, uint Flags, SessionCounters Counters)? Query()
    {
        if (!WtsApi.WTSQuerySessionInformation(WtsApi.CurrentServerHandle, WtsApi.CurrentSession, WtsApi.SessionInfoEx, out var buffer, out var size))
        {
            return null;
        }

        try
        {
            if (buffer == IntPtr.Zero || size < sizeof(WtsInfoEx))
            {
                return null;
            }

            var info = (WtsInfoEx*)buffer;
            if (info->Level != 1)
            {
                return null;
            }

            var level = &info->Level1;
            var station = new string(level->WinStationName, 0, 33).TrimEnd('\0');
            var end = station.IndexOf('\0', StringComparison.Ordinal);
            if (end >= 0)
            {
                station = station[..end];
            }

            return (station, level->SessionFlags, new SessionCounters(level->IncomingBytes, level->OutgoingBytes, level->IncomingFrames, level->IncomingCompressedBytes));
        }
        finally
        {
            WtsApi.WTSFreeMemory(buffer);
        }
    }

    /// <summary>WTSINFOEXW with its level 1, WTSINFOEX_LEVEL1_W. The union starts on 8 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WtsInfoEx
    {
        public uint Level;
        public uint Padding;
        public WtsInfoExLevel1 Level1;
    }

    /// <summary>WTSINFOEX_LEVEL1_W. The names are not read beyond the name of the connection.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsInfoExLevel1
    {
        public uint SessionId;
        public int SessionState;
        public uint SessionFlags;
        public fixed char WinStationName[33];
        public fixed char UserName[21];
        public fixed char DomainName[18];
        public long LogonTime;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long CurrentTime;
        public uint IncomingBytes;
        public uint OutgoingBytes;
        public uint IncomingFrames;
        public uint OutgoingFrames;
        public uint IncomingCompressedBytes;
        public uint OutgoingCompressedBytes;
    }
}
