using System.Net;

namespace MtrWindows;

/// <summary>
/// Core mtr engine: continuously probes each TTL 1..N in round-robin order,
/// accumulating per-hop statistics. Runs on a dedicated background thread.
/// </summary>
internal sealed class TracerouteEngine : IDisposable
{
    public const int MaxHops = 30;

    private readonly IPAddress   _destination;
    private readonly DnsResolver _dns;
    private readonly IcmpProbe   _probe;
    private readonly uint        _timeoutMs;
    private readonly int         _intervalMs; // inter-probe delay

    // Hop 0 = TTL 1, Hop 1 = TTL 2, …
    public HopStats[] Hops { get; } = Enumerable
        .Range(0, MaxHops)
        .Select(_ => new HopStats())
        .ToArray();

    // Index of the last TTL we need to probe (grows until we reach the dest)
    private int _activeHops = 1;

    // Set to >= 0 when the destination has been reached; we stop probing beyond it
    private int _destHopIndex = -1;

    public TracerouteEngine(IPAddress destination,
                            DnsResolver dns,
                            uint timeoutMs  = 3000,
                            int  intervalMs = 100)
    {
        _destination = destination;
        _dns         = dns;
        _probe       = new IcmpProbe();
        _timeoutMs   = timeoutMs;
        _intervalMs  = intervalMs;
    }

    /// <summary>
    /// Runs the probe loop until cancellation is requested.
    /// Intended to be called via Task.Run on a background thread.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int limit = Math.Min(_activeHops, MaxHops);

            for (int i = 0; i < limit && !ct.IsCancellationRequested; i++)
            {
                byte ttl  = (byte)(i + 1);
                var  hop  = Hops[i];

                hop.RecordSent();
                ProbeResult result = _probe.Send(_destination, ttl, _timeoutMs);

                switch (result.Status)
                {
                    case ProbeStatus.Reply:
                        // Reached the destination
                        hop.RecordReply(result.Address!, result.RoundTripMs);
                        TriggerDns(hop, result.Address!);
                        _destHopIndex = i;
                        _activeHops   = i + 1;
                        break;

                    case ProbeStatus.TtlExpired:
                        // Found an intermediate hop
                        hop.RecordReply(result.Address!, result.RoundTripMs);
                        TriggerDns(hop, result.Address!);
                        // Expand the active range if we haven't found the dest yet
                        if (_destHopIndex < 0 && i == _activeHops - 1)
                            _activeHops = Math.Min(_activeHops + 1, MaxHops);
                        break;

                    case ProbeStatus.Timeout:
                        hop.RecordTimeout();
                        // Still expand if we haven't reached dest
                        if (_destHopIndex < 0 && i == _activeHops - 1)
                            _activeHops = Math.Min(_activeHops + 1, MaxHops);
                        break;

                    case ProbeStatus.Unreachable:
                        if (result.Address is not null)
                        {
                            hop.RecordReply(result.Address, result.RoundTripMs);
                            TriggerDns(hop, result.Address);
                        }
                        else
                        {
                            hop.RecordTimeout();
                        }
                        break;
                }

                if (_intervalMs > 0)
                    await Task.Delay(_intervalMs, ct).ConfigureAwait(false);
            }
        }
    }

    private void TriggerDns(HopStats hop, IPAddress address)
    {
        // Only queue if we don't already have a hostname for this hop
        if (hop.HasAddress && hop.Address!.Equals(address) && hop.GetSnapshot().Host != address.ToString())
            return;

        _dns.Resolve(address, hostname => hop.SetHostname(hostname));
    }

    public int ActiveHopCount => _activeHops;

    public void Dispose() => _probe.Dispose();
}
