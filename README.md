# CtrlIt LAN Monitor

**See every device on your home or office network in one table, with a green dot for devices that are up and a red dot for the ones that have stopped answering.**

CtrlIt LAN Monitor is a small portable Windows app. It scans your local subnet every 30 minutes, adds anything new it finds, and lets you fill in the details it can't work out for itself (a friendly name, location, owner, serial number, notes).

## Features

- **Finds devices automatically.** Pings every address in your subnet. Devices that ignore ping (many phones and printers) are still found, because they answer Windows' ARP request.
- **Green, red and grey dots.**
  - Green: the device answered the last check.
  - Red: a device already in the list that didn't answer. Before marking it red, the app also tries some common ports, so phones and printers that block ping aren't reported as down by mistake.
  - Grey: you added the device by hand and it hasn't been checked yet.
- **Scans on a schedule.** Every 30 minutes by default; you can change this in Settings. There's also a **Scan now** button (F5), and the status bar counts down to the next scan.
- **Fills in what it can.**
  - **Hostname:** from DNS, then NetBIOS, then mDNS.
  - **MAC address:** from the ARP cache.
  - **Vendor:** from the IEEE manufacturer list built into the app (works offline).
  - **OS:** guessed from the ping reply.
  - **Type:** guessed from the vendor and open ports (e.g. Raspberry Pi, Printer, Virtual machine, Proxmox host, NAS).
- **You fill in the rest.** Double-click a cell to edit it, or press Enter for the full Edit window. Anything you type is kept, and later scans never overwrite it.
- **Add and delete devices by hand.**
  - Add any device by hand, even one that's usually switched off.
  - **Delete and ignore** removes a device and stops future scans adding it again.
- **Show or hide columns.** Use the **Columns** button, the View menu, or right-click a column header. Drag headers to reorder them. Your layout is remembered.
  - Columns: Status, IP address, Hostname, Name, Type, MAC address, Vendor, OS, Model, Serial number, Location, Owner, Open ports, Response, Last seen, First seen, Status changed, Last checked, Added by, Notes.
- **New devices stand out.** Devices found since the last scan are highlighted in blue until you edit them or choose **Mark all as seen**.
- **Filter, sort and export.**
  - Type in the filter box (Ctrl+F) to narrow the list.
  - Click a column header to sort. IP addresses sort in proper numeric order.
  - Export to CSV (Ctrl+S).
- **Right-click a device** to:
  - Check it now
  - Ping it continuously
  - Open it in a browser (http or https)
  - Connect with Remote Desktop
  - Open its file share
  - Copy its IP or MAC address
- **Portable.** It's one `.exe` with no installer. Your device list and settings are saved in `ctrlit-lan-data.xml` next to it, so the whole folder can go on a USB stick. If that folder can't be written to, the app uses `%APPDATA%\CtrlIt LAN Monitor` instead.

## Download

Get `ctrlit-lan.exe` from the [latest release](../../releases/latest), put it in a folder of its own and run it. The first scan starts straight away.

Requires Windows 10 or 11 (or Windows Server 2016 or later) with .NET Framework 4.x, which is built in.

## Settings

| Setting | Default | |
|---|---|---|
| Subnet to scan | automatic | The network of the adapter that has a default gateway. Enter one yourself (e.g. `192.168.1.0/24`) to override it. Between /20 and /30 is supported. |
| Scan every | 30 minutes | 1 minute to 24 hours |
| Ping timeout | 1000 ms | |
| Check common ports | on | Fills in **Open ports** and helps guess the type. Checks 21, 22, 23, 53, 80, 139, 443, 445, 548, 554, 631, 1883, 3389, 5000, 5900, 8006, 8080, 8123, 8443, 9100, 32400 and 62078. |
| Scan when the program starts | on | |
| Keep scanning in the system tray when the window is closed | off | |

## Good to know

- **The app must be running to scan.** Leave it open, or turn on the system tray option in Settings.
- **Windows Firewall:** the first scan may trigger a firewall prompt for the name lookups (NetBIOS and mDNS). If you allow it on private networks, more hostnames will be filled in.
- **Changed IP addresses:** if a device gets a different IP address from DHCP, it's matched by its MAC address and moved rather than added twice. This doesn't work for phones that use a private (random) MAC address.

## Privacy

CtrlIt LAN Monitor only talks to devices on the subnet you scan (plus any you add by hand). It sends nothing to the internet and collects nothing.

## Building from source

No Visual Studio needed. The C# compiler that ships with Windows is enough:

```
build.bat
```

This compiles `CtrlItLAN.cs` into `ctrlit-lan.exe` and starts it. Compiler output is saved in `build.log`. `build.bat ci` only compiles, which is what the release workflow uses.

`oui.gz` is the IEEE MAC address manufacturer list (MA-L), trimmed to prefix and short name and built into the program.

## Releases

Publishing a release on GitHub (for example with tag `v1.0.0`) runs the workflow in `.github/workflows/release.yml`, which builds `ctrlit-lan.exe` on a clean Windows machine and attaches it to that release.

## Support the project

CtrlIt LAN Monitor is free and open source. If it saves you time, you can support its development via [Ko-fi](https://ko-fi.com/ctrlittools) or the **Sponsor** button at the top of this page.

## Licence

[MIT](LICENSE)
