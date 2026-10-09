# Memory Monitor widget

A Windows 11 Widgets board widget that shows RAM in use and checks that the GPU/NPU shared memory limit stays at or under your target (5 GB by default).

- **Small:** RAM %, used/total, and a ✓/⚠ line for the GPU/NPU limit.
- **Medium:** adds available RAM, the limit with an explanation, and current GPU and NPU memory use. When the limit is over your target the card turns red and shows an **Open Intel Graphics Software** button.
- **Large:** adds committed memory and the top 5 apps by memory (the same "Memory" figure Task Manager shows).

It refreshes every 3 seconds while the Widgets board is open and does nothing while it's closed.

## Why

I had claude generate this because I use an intel based Dell pc for work against my better judgement. It is neat that I can assign the amount of VRAM to the NPU/GPU side of things given that windows 11 uses a stick of ram to remember where your cursor is. What is not so neat is that when you update the system it seems to forget that you asked it to limit the vram to a certain number and restores it back to the 8GB Dell/Intel thinks you want. So I had this made to notify me if that number changed so I don't need to wait until my computer is fully booted to find out that I need to adjust that setting and reboot. Hopefully this can help you out - feel free to make changes or expand the capability!

## Settings

On the widget, click **...** → **Customize widget** to set the GB target (0.25–256 GB, decimals allowed). The settings card also shows the current limit as a percentage of RAM, and what percentage your target works out to. Each pinned widget keeps its own target; the Widgets board saves it as the widget's custom state, so it survives restarts and updates.

## Install

1. Right-click `Install.ps1` → **Run with PowerShell**. The first time, it asks to trust the package's self-signed certificate (Y, then approve the admin prompt).
2. Open Widgets (Win+W) → **+** → **Memory Monitor** → pin it.

`Uninstall.ps1` removes it. To also remove the certificate trust, delete `CN=MemoryMonitorWidget` from *Local Machine → Trusted People* in `certlm.msc`, and from *Current User → Personal* in `certmgr.msc`.

## Build

Run `Build.ps1` (needs Visual Studio 2026 MSBuild). It creates the signing certificate on first run, builds through the folder's 8.3 short path (MakePri/MakeAppx fail beyond 260 characters), and writes `AppPackages\...\MemoryWidget_<version>_x64.msix`. Bump `Version` in `Package.appxmanifest` for updates.

`MemoryWidget.exe --dump <file>` writes the card template and live data as JSON, for previewing in the Adaptive Cards designer.

## Where the numbers come from

- **RAM:** `GlobalMemoryStatusEx`.
- **GPU/NPU limit:** `D3DKMTQueryAdapterInfo(KMTQAITYPE_GETSEGMENTSIZE).SharedSystemMemorySize` per adapter, the same value as Task Manager's "Shared GPU memory" total. On this machine the Intel driver sets it from `GpuSystemMemoryPinninglimit` (a percentage of RAM) under the display adapter's `GMM` registry key. 29% ≈ 4.48 GB; 32% is the most that stays under 5 GB with 15.5 GB of usable RAM.
- **GPU/NPU in use:** `\GPU Adapter Memory(*)\Dedicated Usage` + `Shared Usage` performance counters.
- **Top apps:** private working set from `NtQuerySystemInformation(SystemProcessInformation)`, grouped by executable.
