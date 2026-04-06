using System.Runtime.InteropServices;

namespace MtrWindows;

/// <summary>
/// P/Invoke declarations for the Windows ICMP API (iphlpapi.dll).
/// IcmpSendEcho2Ex lets us set TTL on outbound probes; routers that drop
/// a packet due to TTL exhaustion send back ICMP TIME_EXCEEDED, giving us
/// each hop's address — exactly what mtr needs.
/// </summary>
internal static class IcmpNative
{
    // ── Status codes returned in ICMP_ECHO_REPLY.Status ──────────────────
    internal const uint IP_SUCCESS              = 0;
    internal const uint IP_TTL_EXPIRED_TRANSIT  = 11013;
    internal const uint IP_TTL_EXPIRED_REASSEM  = 11014;
    internal const uint IP_REQ_TIMED_OUT        = 11010;
    internal const uint IP_DEST_HOST_UNREACHABLE = 11003;
    internal const uint IP_DEST_NET_UNREACHABLE  = 11002;
    internal const uint IP_DEST_PORT_UNREACHABLE = 11005;

    /// <summary>
    /// Size of ICMP_ECHO_REPLY on 64-bit Windows:
    ///   Address(4) + Status(4) + RoundTripTime(4) + DataSize(2) + Reserved(2)
    ///   + Data ptr(8) + IP_OPTION_INFORMATION[Ttl+Tos+Flags+Size(4)+pad(4)+ptr(8)] = 40 bytes
    /// </summary>
    internal const int ICMP_ECHO_REPLY_SIZE = 40;

    /// <summary>
    /// The TTL/TOS/Flags options we pass to control the outbound probe packet.
    /// OptionsData must be IntPtr.Zero when OptionsSize == 0.
    /// LayoutKind.Sequential on 64-bit: 4 bytes + 4 bytes pad + 8 byte ptr = 16 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IpOptionInformation
    {
        public byte   Ttl;
        public byte   Tos;
        public byte   Flags;
        public byte   OptionsSize;
        public IntPtr OptionsData;  // PUCHAR — must be Zero when OptionsSize == 0
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    internal static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IcmpCloseHandle(IntPtr icmpHandle);

    /// <param name="hEvent">NULL → synchronous (blocks until reply or timeout)</param>
    /// <param name="sourceAddress">0 → OS chooses source address</param>
    /// <returns>Number of ICMP_ECHO_REPLY structures written to replyBuffer (0 or 1)</returns>
    [DllImport("iphlpapi.dll", SetLastError = true)]
    internal static extern uint IcmpSendEcho2Ex(
        IntPtr                  icmpHandle,
        IntPtr                  hEvent,
        IntPtr                  apcRoutine,
        IntPtr                  apcContext,
        uint                    sourceAddress,
        uint                    destinationAddress,
        byte[]                  requestData,
        ushort                  requestSize,
        ref IpOptionInformation requestOptions,
        byte[]                  replyBuffer,
        uint                    replySize,
        uint                    timeout);
}
