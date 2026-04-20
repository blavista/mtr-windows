# mtr-windows

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Platform: Windows x64](https://img.shields.io/badge/platform-windows--x64-blue.svg)]()

A standalone Windows console traceroute in the style of [mtr](https://github.com/traviscross/mtr) — continuously probes every hop to a destination, showing live loss and latency statistics for each one. NativeAOT-compiled to a single `mtr.exe` with no .NET runtime dependency.

<img width="1096" height="588" alt="image" src="https://github.com/user-attachments/assets/06279e63-030a-44a5-a374-ec6bbb05c240" />


## Install

Download the latest `mtr.exe` from the [Releases](../../releases) page and run it from a terminal. No installation, no runtime, no dependencies.

```powershell
mtr google.com
```

### Verify the download

Each release ships with a `SHA256SUMS` file listing the expected hash. To check:

```powershell
(Get-FileHash mtr.exe -Algorithm SHA256).Hash.ToLower()
```

The output should match the hash in `SHA256SUMS` for that release.

### SmartScreen warning

The binary is unsigned, so the first time you run it Windows SmartScreen will show a **"Windows protected your PC"** dialog. Click **More info** → **Run anyway**. This is expected behaviour for unsigned open-source executables downloaded from the internet — the SHA256 above is how you confirm the file is exactly what the source in this repo built to.

## Usage

```
Usage: mtr [options] <host>

Options:
  -n              No DNS resolution
  -c <count>      Stop after <count> cycles (default: run until Q)
  -i <ms>         Inter-probe interval in ms (default: 100)
  -t <ms>         Probe timeout in ms (default: 3000)

Keys while running:
  Q  Quit      D  Toggle DNS      R  Reset stats
```

Examples:

```powershell
# Run until you press Q
mtr 8.8.8.8

# Run 30 rounds then exit (useful for scripting)
mtr -c 30 google.com

# Skip reverse DNS, slow the probe rate down
mtr -n -i 500 github.com
```

## Columns

| Column | Meaning                                                    |
|--------|------------------------------------------------------------|
| Loss%  | Percentage of probes to this hop with no reply             |
| Lost   | Absolute count of dropped probes                           |
| Snt    | Total probes sent to this hop                              |
| Last   | Most recent round-trip time (ms)                           |
| Avg    | Running mean round-trip time (ms), via Welford's algorithm |
| Best   | Minimum observed round-trip time (ms)                      |
| Wrst   | Maximum observed round-trip time (ms)                      |
| StDev  | Round-trip time standard deviation (ms)                    |

## Build from source

Requirements:

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Visual Studio Build Tools](https://visualstudio.microsoft.com/downloads/) with the **Desktop development with C++** workload (NativeAOT needs the MSVC linker and Windows SDK)

```powershell
git clone https://github.com/<you>/mtr-windows.git
cd mtr-windows
dotnet publish -c Release -r win-x64
```

The standalone binary lands in `bin\Release\net9.0-windows\win-x64\publish\mtr.exe` (~1.8 MB).

For iterating during development without AOT overhead:

```powershell
dotnet run -- google.com
```

## How it works

Probes are sent with the native Windows ICMP API (`IcmpSendEcho2Ex` via `iphlpapi.dll`), not raw sockets — which means `mtr` does **not** require administrator privileges. Each TTL gets its own `HopStats` instance; the engine walks TTL 1..N in a loop, stops when it reaches the destination, and then only probes the discovered path on subsequent rounds. Reverse DNS runs concurrently on a background task with a simple cache. The UI redraws in place using VT100 escape sequences.

## License

MIT — see [LICENSE](LICENSE).

## Credits

Inspired by the original Unix [mtr](https://github.com/traviscross/mtr) by Matt Kimball and Roger Wolff. The Windows implementation here is original; it reimplements the idea on top of the Windows ICMP API rather than porting any upstream code.
