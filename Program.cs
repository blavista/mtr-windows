using System.Net;
using MtrWindows;

// ── Argument parsing ────────────────────────────────────────────────────────
if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage: mtr-windows [options] <host>");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -n              No DNS resolution");
    Console.WriteLine("  -c <count>      Stop after <count> cycles (default: run until Q)");
    Console.WriteLine("  -i <ms>         Inter-probe interval in ms (default: 100)");
    Console.WriteLine("  -t <ms>         Probe timeout in ms (default: 3000)");
    Console.WriteLine();
    Console.WriteLine("Keys while running:");
    Console.WriteLine("  Q  Quit      D  Toggle DNS      R  Reset stats");
    return;
}

bool   noDns      = false;
int    interval   = 100;
uint   timeout    = 3000;
int    maxCycles  = int.MaxValue;
string? targetArg = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-n": noDns = true; break;
        case "-c": maxCycles = int.Parse(args[++i]); break;
        case "-i": interval  = int.Parse(args[++i]); break;
        case "-t": timeout   = uint.Parse(args[++i]); break;
        default:   targetArg = args[i]; break;
    }
}

if (targetArg is null)
{
    Console.Error.WriteLine("Error: no host specified.");
    return;
}

// ── Resolve target ──────────────────────────────────────────────────────────
IPAddress destination;
try
{
    if (!IPAddress.TryParse(targetArg, out destination!))
    {
        var entry = await Dns.GetHostEntryAsync(targetArg);
        destination = entry.AddressList.First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Cannot resolve '{targetArg}': {ex.Message}");
    return;
}

string localHost = Dns.GetHostName();

// ── Setup ───────────────────────────────────────────────────────────────────
bool dnsEnabled = !noDns;          // runtime-mutable: flipped by the D key

var dns    = new DnsResolver(enabled: dnsEnabled);
var engine = new TracerouteEngine(destination, dns, timeout, interval);
var ui     = new ConsoleUi();

using var cts = new CancellationTokenSource();

// Ctrl+C handler
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

ui.DrawHeader(localHost, destination, dnsEnabled);

// ── Engine runs on background thread ────────────────────────────────────────
var engineTask = Task.Run(() => engine.RunAsync(cts.Token));

// ── UI refresh + keyboard loop ───────────────────────────────────────────────
try
{
    while (!cts.Token.IsCancellationRequested)
    {
        ui.Refresh(engine.Hops, engine.ActiveHopCount, dnsEnabled);

        // mtr -c: exit after N completed probe rounds across all active hops
        if (engine.CompletedRounds >= maxCycles) { cts.Cancel(); break; }

        // Non-blocking keyboard check (~100ms budget split into 10 × 10ms)
        for (int t = 0; t < 10 && !cts.Token.IsCancellationRequested; t++)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
                switch (key)
                {
                    case ConsoleKey.Q:
                    case ConsoleKey.Escape:
                        cts.Cancel();
                        break;

                    case ConsoleKey.R:
                        foreach (var hop in engine.Hops) hop.Reset();
                        break;

                    case ConsoleKey.D:
                        // Toggle both the resolver (stop/start future lookups) and the
                        // display preference (show hostnames vs raw IPs). Already-cached
                        // names are kept — flipping D back on makes them reappear.
                        dnsEnabled = dns.Toggle();
                        ui.UpdateDnsState(dnsEnabled);
                        break;
                }
            }
            try { await Task.Delay(10, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
catch (OperationCanceledException) { /* normal exit */ }
finally
{
    try { await engineTask.ConfigureAwait(false); }
    catch (OperationCanceledException) { /* normal shutdown */ }
    engine.Dispose();
    ui.Teardown();
}
