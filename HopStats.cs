using System.Net;

namespace MtrWindows;

/// <summary>
/// Running statistics for one hop (one TTL value).
/// All mutations must happen on the engine thread; reads from the UI thread
/// are done via the snapshot <see cref="Snapshot"/> struct to avoid torn reads.
/// </summary>
internal sealed class HopStats
{
    // Last-seen address and resolved hostname (may change if routing changes)
    private IPAddress? _address;
    private string?    _hostname;

    // Counters
    private int _sent;
    private int _received;

    // RTT in milliseconds
    private double _last;
    private double _best = double.MaxValue;
    private double _worst;

    // Welford's online algorithm for mean + variance
    private double _mean;
    private double _m2;   // sum of squared deviations

    // ── Mutators (engine thread only) ────────────────────────────────────

    public void RecordSent() => Interlocked.Increment(ref _sent);

    public void RecordReply(IPAddress address, double rttMs)
    {
        _address  = address;
        _last     = rttMs;
        _best     = Math.Min(_best,  rttMs);
        _worst    = Math.Max(_worst, rttMs);

        int n = Interlocked.Increment(ref _received);

        // Welford update
        double delta = rttMs - _mean;
        _mean += delta / n;
        _m2   += delta * (rttMs - _mean);
    }

    public void RecordTimeout() { /* _sent already bumped by RecordSent */ }

    public void SetHostname(string hostname) => _hostname = hostname;

    // ── Snapshot (UI thread safe — primitive reads are atomic on x64) ────

    public readonly record struct Snapshot(
        IPAddress? Address,
        string     Host,         // hostname if resolved, else IP string, else "???"
        double     LossPct,
        int        Lost,         // Sent - Received — absolute count of dropped probes
        int        Sent,
        int        Received,
        double     Last,
        double     Avg,
        double     Best,
        double     Worst,
        double     StDev);

    public Snapshot GetSnapshot()
    {
        int    sent = _sent;
        int    recv = _received;
        int    lost = sent - recv;
        double loss = sent == 0 ? 0.0 : lost * 100.0 / sent;
        double best  = recv == 0 ? 0.0 : _best;
        double stdev = recv < 2  ? 0.0 : Math.Sqrt(_m2 / (_received - 1));

        string host = _hostname
            ?? _address?.ToString()
            ?? "???";

        return new Snapshot(
            Address:  _address,
            Host:     host,
            LossPct:  loss,
            Lost:     lost,
            Sent:     sent,
            Received: recv,
            Last:     recv == 0 ? 0.0 : _last,
            Avg:      recv == 0 ? 0.0 : _mean,
            Best:     best,
            Worst:    recv == 0 ? 0.0 : _worst,
            StDev:    stdev);
    }

    /// <summary>Resets all counters and RTT data while keeping address/hostname.</summary>
    public void Reset()
    {
        _sent     = 0;
        _received = 0;
        _last     = 0;
        _best     = double.MaxValue;
        _worst    = 0;
        _mean     = 0;
        _m2       = 0;
    }

    public bool HasAddress => _address is not null;
    public IPAddress? Address => _address;
}
