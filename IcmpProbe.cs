using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace MtrWindows;

internal enum ProbeStatus
{
    Reply,        // Destination replied — we reached it
    TtlExpired,   // Router replied with TIME_EXCEEDED — this is a hop
    Timeout,      // No response within the timeout window
    Unreachable,  // Destination/network/port unreachable
}

internal readonly record struct ProbeResult(
    ProbeStatus Status,
    IPAddress?  Address,
    uint        RoundTripMs);

/// <summary>
/// Wraps a single Windows ICMP handle and exposes one method: Send().
/// One IcmpProbe instance per concurrent probe stream is fine;
/// the underlying handle is not thread-safe so don't share across threads.
/// </summary>
internal sealed class IcmpProbe : IDisposable
{
    private static readonly IntPtr InvalidHandle = new(-1);

    // 32-byte payload — matches common ping tools, gives routers enough to echo back
    private const ushort RequestSize = 32;
    private static readonly byte[] RequestData = new byte[RequestSize];

    // Buffer must hold ICMP_ECHO_REPLY + payload + 8 bytes overhead per MSDN
    private static readonly int ReplyBufSize = IcmpNative.ICMP_ECHO_REPLY_SIZE + RequestSize + 8;

    private readonly IntPtr _handle;
    private bool _disposed;

    public IcmpProbe()
    {
        _handle = IcmpNative.IcmpCreateFile();
        if (_handle == IntPtr.Zero || _handle == InvalidHandle)
            throw new InvalidOperationException(
                $"IcmpCreateFile failed (error {Marshal.GetLastWin32Error()})");
    }

    /// <summary>
    /// Sends one ICMP echo request to <paramref name="destination"/> with the given TTL
    /// and blocks until a reply arrives or the timeout expires.
    /// </summary>
    public ProbeResult Send(IPAddress destination, byte ttl, uint timeoutMs = 3000)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // iphlpapi expects the address as a uint in host-byte-order on the same machine;
        // GetAddressBytes() for IPv4 returns bytes in network order (big-endian),
        // which BitConverter reads as little-endian on x86 — that's what the API wants.
        uint destAddr = BitConverter.ToUInt32(destination.GetAddressBytes(), 0);

        var options = new IcmpNative.IpOptionInformation
        {
            Ttl         = ttl,
            Tos         = 0,
            Flags       = 0,
            OptionsSize = 0,
            OptionsData = IntPtr.Zero,
        };

        var buf = new byte[ReplyBufSize];

        uint replies = IcmpNative.IcmpSendEcho2Ex(
            _handle,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,  // synchronous, no APC
            0,           // source address: let OS choose
            destAddr,
            RequestData, RequestSize,
            ref options,
            buf, (uint)buf.Length,
            timeoutMs);

        return replies >= 1
            ? ParseReply(buf)
            : ParseError((uint)Marshal.GetLastWin32Error());
    }

    // ── Buffer layout for ICMP_ECHO_REPLY (64-bit) ───────────────────────
    //  [0..3]   Address        (uint)   replying router/host IP
    //  [4..7]   Status         (uint)   IP_STATUS code
    //  [8..11]  RoundTripTime  (uint)   RTT in milliseconds
    //  [12..13] DataSize       (ushort)
    //  [14..15] Reserved       (ushort)
    //  [16..23] Data           (IntPtr)
    //  [24..27] Options bytes  (Ttl/Tos/Flags/Size)
    //  [28..31] padding
    //  [32..39] Options ptr

    private static ProbeResult ParseReply(byte[] buf)
    {
        var span    = buf.AsSpan();
        uint addr   = BinaryPrimitives.ReadUInt32LittleEndian(span[0..4]);
        uint status = BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]);
        uint rtt    = BinaryPrimitives.ReadUInt32LittleEndian(span[8..12]);

        IPAddress? ip = addr == 0 ? null : new IPAddress(addr);
        return MapStatus(status, ip, rtt);
    }

    private static ProbeResult ParseError(uint errorCode) =>
        MapStatus(errorCode, null, 0);

    private static ProbeResult MapStatus(uint status, IPAddress? ip, uint rtt) =>
        status switch
        {
            IcmpNative.IP_SUCCESS              => new(ProbeStatus.Reply,       ip,   rtt),
            IcmpNative.IP_TTL_EXPIRED_TRANSIT  => new(ProbeStatus.TtlExpired,  ip,   rtt),
            IcmpNative.IP_TTL_EXPIRED_REASSEM  => new(ProbeStatus.TtlExpired,  ip,   rtt),
            IcmpNative.IP_REQ_TIMED_OUT        => new(ProbeStatus.Timeout,     null, 0),
            _                                  => new(ProbeStatus.Unreachable, ip,   rtt),
        };

    public void Dispose()
    {
        if (!_disposed)
        {
            IcmpNative.IcmpCloseHandle(_handle);
            _disposed = true;
        }
    }
}
