using System.Net;
using System.Text;

namespace MtrWindows;

/// <summary>
/// Renders the mtr-style hop table to the Windows console using VT100 escape
/// sequences (supported natively since Windows 10 Anniversary Update).
/// Moves the cursor back to the header row on each refresh so the display
/// updates in-place, just like the original mtr curses UI.
/// </summary>
internal sealed class ConsoleUi
{
    // ── Column widths ────────────────────────────────────────────────────
    private const int ColHop   = 3;
    private const int ColHost  = 40;
    private const int ColLoss  = 7;   // "100.0%"
    private const int ColSnt   = 5;
    private const int ColLast  = 7;
    private const int ColAvg   = 7;
    private const int ColBest  = 7;
    private const int ColWrst  = 7;
    private const int ColStDev = 7;

    private int    _headerRow = -1;   // console row where header was printed
    private bool   _vt100Enabled;

    // Remembered so the hint line can be rewritten when DNS is toggled
    private string _localHost = string.Empty;
    private IPAddress? _destination;

    public ConsoleUi()
    {
        TryEnableVt100();
        Console.OutputEncoding = Encoding.UTF8;
        Console.CursorVisible  = false;
    }

    // ── Public API ───────────────────────────────────────────────────────

    public void DrawHeader(string localHost, IPAddress destination, bool dnsEnabled)
    {
        _localHost   = localHost;
        _destination = destination;

        Console.Clear();
        _headerRow = Console.CursorTop;

        WriteColor($"  {localHost} → {destination}", ConsoleColor.Cyan);
        Console.WriteLine();

        WriteHintLine(dnsEnabled);
        Console.WriteLine();

        // Column header row
        string header = string.Format(
            "  {0,3}  {1,-40}  {2,7}  {3,5}  {4,7}  {5,7}  {6,7}  {7,7}  {8,7}",
            "", "Host", "Loss%", "Snt", "Last", "Avg", "Best", "Wrst", "StDev");
        WriteColor(header, ConsoleColor.White);
        Console.WriteLine();
        WriteColor("  " + new string('─', ColHop + 2 + ColHost + 2 + ColLoss + 2 + ColSnt +
                                        2 + ColLast + 2 + ColAvg + 2 + ColBest + 2 + ColWrst +
                                        2 + ColStDev), ConsoleColor.DarkGray);
        Console.WriteLine();
    }

    /// <summary>
    /// Rewrites the hint line in place, reflecting the current DNS state.
    /// </summary>
    public void UpdateDnsState(bool dnsEnabled)
    {
        if (_headerRow < 0) return;
        SetCursorRow(_headerRow + 1);
        WriteHintLine(dnsEnabled);
        Console.Write("\x1b[0K"); // clear any leftover chars from a longer previous hint
    }

    /// <summary>
    /// Redraws all hop rows in-place. Should be called ~10 times/second.
    /// </summary>
    public void Refresh(HopStats[] hops, int activeCount, bool dnsEnabled)
    {
        if (_headerRow < 0) return;

        // Move cursor to the first hop row (header + 5 lines)
        int firstHopRow = _headerRow + 5;
        SetCursorRow(firstHopRow);

        for (int i = 0; i < activeCount; i++)
        {
            var s = hops[i].GetSnapshot();
            DrawHopRow(i + 1, s, dnsEnabled);
        }
    }

    private static void WriteHintLine(bool dnsEnabled)
    {
        string dnsNote = dnsEnabled ? "" : "  [DNS off — press D]";
        WriteColor($"  Keys: Q=Quit  D=Toggle DNS  R=Reset{dnsNote}", ConsoleColor.DarkGray);
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static void DrawHopRow(int hopNum, HopStats.Snapshot s, bool dnsEnabled)
    {
        bool isUnknown = s.Address is null;

        // When DNS display is off, show the IP even if we happen to have resolved a hostname.
        string label = dnsEnabled
            ? s.Host
            : (s.Address?.ToString() ?? "???");

        string hostDisplay = label.Length > ColHost
            ? label[..(ColHost - 1)] + "…"
            : label;

        string loss = $"{s.LossPct:F1}%";

        string last  = s.Received == 0 ? "  ---" : $"{s.Last,ColLast:F1}";
        string avg   = s.Received == 0 ? "  ---" : $"{s.Avg,ColAvg:F1}";
        string best  = s.Received == 0 ? "  ---" : $"{s.Best,ColBest:F1}";
        string worst = s.Received == 0 ? "  ---" : $"{s.Worst,ColWrst:F1}";
        string stdev = s.Received < 2  ? "  ---" : $"{s.StDev,ColStDev:F1}";

        // Loss colour: green < 5%, yellow < 20%, red otherwise
        var lossColor = s.LossPct switch
        {
            0.0      => ConsoleColor.Green,
            < 5.0    => ConsoleColor.Green,
            < 20.0   => ConsoleColor.Yellow,
            100.0    => ConsoleColor.DarkGray,
            _        => ConsoleColor.Red,
        };

        // Hop number
        Console.Write("  ");
        WriteColor($"{hopNum,ColHop}.", ConsoleColor.DarkGray);
        Console.Write(" ");

        // Host
        WriteColor($"{hostDisplay,-ColHost}", isUnknown ? ConsoleColor.DarkGray : ConsoleColor.White);
        Console.Write("  ");

        // Loss
        WriteColor($"{loss,ColLoss}", lossColor);
        Console.Write("  ");

        // Counters + RTT columns
        WriteColor($"{s.Sent,ColSnt}", ConsoleColor.DarkGray);
        Console.Write("  ");
        Console.Write($"{last}  {avg}  {best}  {worst}  {stdev}");

        // Clear to end of line (handles shrinking lines)
        Console.Write("\x1b[0K");
        Console.WriteLine();
    }

    private void SetCursorRow(int row)
    {
        if (_vt100Enabled)
            Console.Write($"\x1b[{row + 1};1H");   // 1-based ANSI
        else
            Console.SetCursorPosition(0, row);
    }

    private static void WriteColor(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ResetColor();
    }

    private void TryEnableVt100()
    {
        // Enable ENABLE_VIRTUAL_TERMINAL_PROCESSING on stdout
        const uint ENABLE_VT = 0x0004;
        var stdout = GetStdHandle(-11);
        if (stdout == IntPtr.Zero) return;

        if (!GetConsoleMode(stdout, out uint mode)) return;

        if (!SetConsoleMode(stdout, mode | ENABLE_VT)) return;

        _vt100Enabled = true;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    public void Teardown()
    {
        Console.CursorVisible = true;
        Console.ResetColor();
        Console.WriteLine();
    }
}
