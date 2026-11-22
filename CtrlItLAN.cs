// CtrlIt LAN Monitor - lists every device on your local network in a table,
// with a green dot for devices that answer and a red dot for known devices
// that have stopped answering. Scans automatically every 30 minutes.
//
// Build: double-click build.bat (uses the C# compiler that ships with Windows).
// Written in C# 5 so the built-in .NET Framework 4.x compiler accepts it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;

[assembly: System.Reflection.AssemblyTitle(CtrlItLan.AppInfo.Name)]
[assembly: System.Reflection.AssemblyProduct(CtrlItLan.AppInfo.Name)]
[assembly: System.Reflection.AssemblyDescription("Lists the devices on your local network and shows which ones are up or down")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 " + CtrlItLan.AppInfo.Author + " - MIT licence")]
[assembly: System.Reflection.AssemblyVersion(CtrlItLan.AppInfo.Version + ".0")]
[assembly: System.Reflection.AssemblyFileVersion(CtrlItLan.AppInfo.Version + ".0")]

namespace CtrlItLan
{
    // --------------------------------------------------------------- about

    // Edit these to change what the About window shows. Empty links are hidden.
    static class AppInfo
    {
        public const string Name = "CtrlIt LAN Monitor";
        public const string Version = "1.0.0";
        public const string Author = "ctrlittools";
        public const string Website = "https://github.com/ctrlittools/ctrlit-localLAN";
        public const string Donate = "https://ko-fi.com/ctrlittools";
    }

    // ---------------------------------------------------------------- native

    static class Native
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("iphlpapi.dll")]
        static extern int GetIpNetTable2(ushort family, out IntPtr table);

        [DllImport("iphlpapi.dll")]
        static extern void FreeMibTable(IntPtr memory);

        [DllImport("iphlpapi.dll")]
        static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint macLen);

        public static void EnableDpiAwareness()
        {
            // System DPI awareness: Windows Forms scales the whole window itself.
            try { SetProcessDPIAware(); }
            catch (Exception) { }
        }

        public class Neighbour
        {
            public string Mac;
            public int State;   // 2 probe, 3 delay, 4 stale, 5 reachable, 6 permanent
        }

        // The Windows neighbour (ARP) cache, IPv4 only, keyed by IP address.
        public static Dictionary<string, Neighbour> ReadNeighbours()
        {
            Dictionary<string, Neighbour> result = new Dictionary<string, Neighbour>();
            IntPtr table;
            try
            {
                if (GetIpNetTable2(2, out table) != 0) return result;
            }
            catch (Exception) { return result; }
            try
            {
                int count = Marshal.ReadInt32(table);
                // MIB_IPNET_ROW2 is 88 bytes and starts 8 bytes into the table.
                IntPtr row = IntPtr.Add(table, 8);
                for (int i = 0; i < count; i++, row = IntPtr.Add(row, 88))
                {
                    if (Marshal.ReadInt16(row, 0) != 2) continue;
                    byte[] ip = new byte[4];
                    Marshal.Copy(IntPtr.Add(row, 4), ip, 0, 4);
                    int macLen = Marshal.ReadInt32(row, 72);
                    int state = Marshal.ReadInt32(row, 76);
                    if (macLen != 6) continue;
                    byte[] mac = new byte[6];
                    Marshal.Copy(IntPtr.Add(row, 40), mac, 0, 6);
                    if (!Net.IsUsableMac(mac)) continue;
                    string key = new IPAddress(ip).ToString();
                    Neighbour existing;
                    if (result.TryGetValue(key, out existing) && existing.State >= state) continue;
                    Neighbour n = new Neighbour();
                    n.Mac = Net.FormatMac(mac);
                    n.State = state;
                    result[key] = n;
                }
            }
            finally { FreeMibTable(table); }
            return result;
        }

        // Asks the device for its MAC address directly (local subnet only).
        public static string ArpLookup(IPAddress ip)
        {
            try
            {
                byte[] mac = new byte[6];
                uint len = 6;
                uint dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
                if (SendARP(dest, 0, mac, ref len) != 0 || len != 6) return null;
                return Net.IsUsableMac(mac) ? Net.FormatMac(mac) : null;
            }
            catch (Exception) { return null; }
        }
    }

    // ----------------------------------------------------------------- data

    public enum DeviceStatus { Unknown, Up, Down }

    public class Device
    {
        public string Ip = "";
        public string Hostname = "";
        public string Name = "";
        public string Type = "";
        public string Mac = "";
        public string Vendor = "";
        public string Os = "";
        public string Model = "";
        public string Serial = "";
        public string Location = "";
        public string Owner = "";
        public string Notes = "";
        public string OpenPorts = "";
        public string Source = "Scan";
        public DeviceStatus Status = DeviceStatus.Unknown;
        public int ResponseMs = -1;
        public DateTime? FirstSeen;
        public DateTime? LastSeen;
        public DateTime? LastChecked;
        public DateTime? StatusChanged;
        public bool IsNew;
        // Fields typed in by you. Scans never overwrite these.
        public List<string> Locked = new List<string>();

        public bool IsLocked(string key) { return Locked.Contains(key); }

        public void Lock(string key)
        {
            if (!Locked.Contains(key)) Locked.Add(key);
        }

        public void SetStatus(DeviceStatus s, DateTime now)
        {
            if (Status != s) StatusChanged = now;
            Status = s;
            LastChecked = now;
        }
    }

    public class ColumnState
    {
        [XmlAttribute] public string Key;
        [XmlAttribute] public int Width;
        [XmlAttribute] public int Index;
        [XmlAttribute] public bool Visible;
    }

    public class AppSettings
    {
        public string Subnet = "";              // empty = detect automatically
        public int IntervalMinutes = 30;
        public int PingTimeoutMs = 1000;
        public bool CheckPorts = true;
        public bool ScanOnStart = true;
        public bool CloseToTray = false;
        public string SortKey = "Ip";
        public bool SortAscending = true;
        public List<ColumnState> Columns = new List<ColumnState>();
        public List<string> Ignored = new List<string>();   // MAC or IP addresses
        public int WindowX = int.MinValue, WindowY, WindowW, WindowH;
        public bool WindowMaximised;
        public DateTime? LastScan;
    }

    [XmlRoot("CtrlItLan")]
    public class DataFile
    {
        public AppSettings Settings = new AppSettings();
        public List<Device> Devices = new List<Device>();
    }

    static class Store
    {
        const string FileName = "ctrlit-lan-data.xml";
        static string path;

        // Kept next to the .exe so the folder is portable. Falls back to
        // AppData if that folder can't be written to (e.g. Program Files).
        public static string FilePath
        {
            get
            {
                if (path != null) return path;
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                string p = Path.Combine(dir, FileName);
                if (CanWrite(dir)) return path = p;
                string alt = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData), "CtrlIt LAN Monitor");
                Directory.CreateDirectory(alt);
                return path = Path.Combine(alt, FileName);
            }
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string test = Path.Combine(dir, ".ctrlit-write-test");
                File.WriteAllText(test, "x");
                File.Delete(test);
                return true;
            }
            catch (Exception) { return false; }
        }

        public static DataFile Load()
        {
            string p = FilePath;
            if (!File.Exists(p)) return new DataFile();
            try
            {
                using (FileStream fs = File.OpenRead(p))
                {
                    DataFile d = (DataFile)new XmlSerializer(typeof(DataFile)).Deserialize(fs);
                    if (d.Settings == null) d.Settings = new AppSettings();
                    if (d.Devices == null) d.Devices = new List<Device>();
                    foreach (Device dev in d.Devices)
                        if (dev.Locked == null) dev.Locked = new List<string>();
                    return d;
                }
            }
            catch (Exception ex)
            {
                string bad = p + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Move(p, bad); } catch (Exception) { }
                MessageBox.Show("Your saved device list couldn't be read, so it has been set aside as:\n\n"
                    + bad + "\n\n" + ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return new DataFile();
            }
        }

        public static void Save(DataFile data)
        {
            string p = FilePath;
            string tmp = p + ".tmp";
            try
            {
                using (FileStream fs = File.Create(tmp))
                    new XmlSerializer(typeof(DataFile)).Serialize(fs, data);
                if (File.Exists(p)) File.Replace(tmp, p, null);
                else File.Move(tmp, p);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Save failed: " + ex.Message);
            }
        }
    }

    // ------------------------------------------------------------- network

    class Subnet
    {
        public uint Network;
        public int Prefix;
        public IPAddress Gateway;
        public IPAddress LocalIp;
        public string LocalMac;

        public uint Mask { get { return Prefix == 0 ? 0 : 0xFFFFFFFFu << (32 - Prefix); } }

        public int HostCount { get { return Prefix >= 31 ? 0 : (int)((~Mask) - 1); } }

        public bool Contains(IPAddress ip)
        {
            return (Net.ToUInt(ip) & Mask) == Network;
        }

        public IEnumerable<IPAddress> Hosts()
        {
            uint last = Network | ~Mask;
            for (uint a = Network + 1; a < last; a++) yield return Net.FromUInt(a);
        }

        public override string ToString()
        {
            return Net.FromUInt(Network) + "/" + Prefix;
        }
    }

    static class Net
    {
        public static uint ToUInt(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        public static IPAddress FromUInt(uint a)
        {
            return new IPAddress(new byte[] { (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a });
        }

        public static IPAddress ParseIPv4(string s)
        {
            IPAddress ip;
            if (s == null) return null;
            s = s.Trim();
            if (s.Split('.').Length != 4) return null;
            if (!IPAddress.TryParse(s, out ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;
            return ip;
        }

        // For sorting: IP addresses in numeric order, anything else after them.
        public static long SortValue(string ip)
        {
            IPAddress a = ParseIPv4(ip);
            return a == null ? long.MaxValue : ToUInt(a);
        }

        public static bool IsUsableMac(byte[] mac)
        {
            bool allZero = true, allFF = true;
            foreach (byte b in mac)
            {
                if (b != 0) allZero = false;
                if (b != 0xFF) allFF = false;
            }
            if (allZero || allFF) return false;
            if ((mac[0] & 1) == 1) return false;   // multicast
            return true;
        }

        public static string FormatMac(byte[] mac)
        {
            return BitConverter.ToString(mac).Replace('-', ':');
        }

        // Accepts AA:BB:CC:DD:EE:FF, AA-BB-..., AABB.CCDD.EEFF or AABBCCDDEEFF.
        public static string NormaliseMac(string s)
        {
            if (s == null) return null;
            StringBuilder hex = new StringBuilder();
            foreach (char c in s.Trim())
            {
                if (Uri.IsHexDigit(c)) hex.Append(char.ToUpperInvariant(c));
                else if (c != ':' && c != '-' && c != '.' && c != ' ') return null;
            }
            if (hex.Length != 12) return null;
            StringBuilder o = new StringBuilder();
            for (int i = 0; i < 12; i += 2)
            {
                if (i > 0) o.Append(':');
                o.Append(hex[i]).Append(hex[i + 1]);
            }
            return o.ToString();
        }

        // Phones and newer laptops use a made-up MAC address per network.
        public static bool IsRandomMac(string mac)
        {
            if (string.IsNullOrEmpty(mac) || mac.Length < 2) return false;
            int first;
            if (!int.TryParse(mac.Substring(0, 2), NumberStyles.HexNumber, null, out first)) return false;
            return (first & 2) == 2;
        }

        // The subnet of the network adapter that has a default gateway.
        public static Subnet Detect()
        {
            Subnet best = null;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback
                        || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    IPInterfaceProperties props = ni.GetIPProperties();
                    IPAddress gw = null;
                    foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
                        if (g.Address.AddressFamily == AddressFamily.InterNetwork
                            && !g.Address.Equals(IPAddress.Any)) { gw = g.Address; break; }
                    foreach (UnicastIPAddressInformation u in props.UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (u.IPv4Mask == null) continue;
                        byte[] ab = u.Address.GetAddressBytes();
                        if (ab[0] == 169 && ab[1] == 254) continue;   // no DHCP address
                        int prefix = 0;
                        uint mask = ToUInt(u.IPv4Mask);
                        while (prefix < 32 && (mask & (0x80000000u >> prefix)) != 0) prefix++;
                        Subnet s = new Subnet();
                        s.Prefix = prefix;
                        s.Network = ToUInt(u.Address) & s.Mask;
                        s.Gateway = gw;
                        s.LocalIp = u.Address;
                        byte[] mac = ni.GetPhysicalAddress().GetAddressBytes();
                        if (mac.Length == 6) s.LocalMac = FormatMac(mac);
                        if (best == null || (best.Gateway == null && gw != null)) best = s;
                    }
                }
            }
            catch (Exception) { }
            if (best != null && best.Prefix < 22)
            {
                // Very large networks: scan the /24 this PC is in.
                best.Prefix = 24;
                best.Network = ToUInt(best.LocalIp) & best.Mask;
            }
            return best;
        }

        // "192.168.1.0/24" or a bare address (taken as its /24). Returns null
        // and an error message if it can't be used.
        public static Subnet Parse(string text, out string error)
        {
            error = null;
            text = (text ?? "").Trim();
            int prefix = 24;
            string ipPart = text;
            int slash = text.IndexOf('/');
            if (slash >= 0)
            {
                ipPart = text.Substring(0, slash);
                if (!int.TryParse(text.Substring(slash + 1), out prefix))
                {
                    error = "The part after / should be a number, for example 192.168.1.0/24.";
                    return null;
                }
            }
            IPAddress ip = ParseIPv4(ipPart);
            if (ip == null)
            {
                error = "Enter a subnet like 192.168.1.0/24.";
                return null;
            }
            if (prefix < 20 || prefix > 30)
            {
                error = "Use a subnet between /20 (4,094 addresses) and /30.";
                return null;
            }
            Subnet s = new Subnet();
            s.Prefix = prefix;
            s.Network = ToUInt(ip) & s.Mask;
            Subnet local = Detect();
            if (local != null)
            {
                if (s.Contains(local.LocalIp))
                {
                    s.LocalIp = local.LocalIp;
                    s.LocalMac = local.LocalMac;
                }
                if (local.Gateway != null && s.Contains(local.Gateway)) s.Gateway = local.Gateway;
            }
            return s;
        }
    }

    // MAC address prefix -> manufacturer, from the IEEE list built into the program.
    static class Vendors
    {
        static Dictionary<string, string> map;
        static readonly object gate = new object();

        static void Load()
        {
            lock (gate)
            {
                if (map != null) return;
                Dictionary<string, string> m = new Dictionary<string, string>(60000);
                try
                {
                    using (Stream s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("oui.gz"))
                    {
                        if (s != null)
                        {
                            using (GZipStream gz = new GZipStream(s, CompressionMode.Decompress))
                            using (StreamReader r = new StreamReader(gz, Encoding.UTF8))
                            {
                                string line;
                                while ((line = r.ReadLine()) != null)
                                {
                                    int tab = line.IndexOf('\t');
                                    if (tab == 6) m[line.Substring(0, 6)] = line.Substring(7);
                                }
                            }
                        }
                    }
                }
                catch (Exception) { }
                map = m;
            }
        }

        public static string Lookup(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return "";
            if (Net.IsRandomMac(mac)) return "Private (random) MAC";
            Load();
            string key = mac.Replace(":", "").ToUpperInvariant();
            if (key.Length < 6) return "";
            string v;
            return map.TryGetValue(key.Substring(0, 6), out v) ? v : "";
        }
    }

    // --------------------------------------------------------------- scanner

    class ProbeResult
    {
        public IPAddress Ip;
        public bool Up;
        public int Rtt = -1;
        public int Ttl = -1;
        public string Mac;
        public string Hostname;
        public List<int> OpenPorts;
        public bool IsThisPc;
    }

    class ScanOptions
    {
        public Subnet Subnet;
        public int PingTimeoutMs = 1000;
        public bool CheckPorts = true;
        public bool LookupNames = true;
    }

    static class Scanner
    {
        // Ports checked for "Open ports" and, for devices that ignore ping,
        // to see whether anything is there at all.
        public static readonly int[] CommonPorts = {
            21, 22, 23, 53, 80, 139, 443, 445, 548, 554, 631, 1883, 3389,
            5000, 5900, 8006, 8080, 8123, 8443, 9100, 32400, 62078 };

        static readonly int[] AlivePorts = { 80, 443, 22, 445, 139, 3389, 62078, 8080, 53, 554 };

        public static async Task<List<ProbeResult>> ScanAsync(ScanOptions opt,
            IEnumerable<string> knownIps, IProgress<int> progress, CancellationToken ct)
        {
            HashSet<string> known = new HashSet<string>(knownIps);
            List<IPAddress> targets = new List<IPAddress>();
            HashSet<string> seen = new HashSet<string>();
            if (opt.Subnet != null)
                foreach (IPAddress ip in opt.Subnet.Hosts())
                    if (seen.Add(ip.ToString())) targets.Add(ip);
            foreach (string k in known)
            {
                IPAddress ip = Net.ParseIPv4(k);
                if (ip != null && seen.Add(k)) targets.Add(ip);
            }

            // 1. Who answers? (0 - 60 %)
            ProbeResult[] results = new ProbeResult[targets.Count];
            int done = 0;
            SemaphoreSlim gate = new SemaphoreSlim(64);
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < targets.Count; i++)
            {
                int idx = i;
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        results[idx] = await CheckAliveAsync(targets[idx], opt,
                            known.Contains(targets[idx].ToString())).ConfigureAwait(false);
                    }
                    finally
                    {
                        gate.Release();
                        int d = Interlocked.Increment(ref done);
                        if (progress != null) progress.Report(d * 60 / Math.Max(1, targets.Count));
                    }
                }, ct));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);

            // 2. Devices that ignore ping still answered Windows' ARP request
            //    during the sweep, so they show up in the neighbour cache.
            Dictionary<string, Native.Neighbour> neigh = Native.ReadNeighbours();
            foreach (ProbeResult r in results)
            {
                Native.Neighbour n;
                if (neigh.TryGetValue(r.Ip.ToString(), out n))
                {
                    if (!r.Up && opt.Subnet != null && opt.Subnet.Contains(r.Ip)
                        && (n.State == 2 || n.State == 3 || n.State == 5)) r.Up = true;
                    if (r.Up && n.State != 0 && n.State != 1) r.Mac = n.Mac;
                }
                if (opt.Subnet != null && opt.Subnet.LocalIp != null && r.Ip.Equals(opt.Subnet.LocalIp))
                {
                    r.Up = true;
                    r.IsThisPc = true;
                    r.Mac = opt.Subnet.LocalMac;
                    r.Hostname = Environment.MachineName;
                }
            }

            // 3. Details for everything that's up (60 - 100 %)
            List<ProbeResult> up = results.Where(r => r.Up).ToList();
            done = 0;
            tasks.Clear();
            SemaphoreSlim gate2 = new SemaphoreSlim(24);
            foreach (ProbeResult r in up)
            {
                ProbeResult res = r;
                tasks.Add(Task.Run(async () =>
                {
                    await gate2.WaitAsync(ct).ConfigureAwait(false);
                    try { await EnrichAsync(res, opt).ConfigureAwait(false); }
                    finally
                    {
                        gate2.Release();
                        int d = Interlocked.Increment(ref done);
                        if (progress != null) progress.Report(60 + d * 40 / Math.Max(1, up.Count));
                    }
                }, ct));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.ToList();
        }

        // Checks one device on its own (Add device, "Check now").
        public static async Task<ProbeResult> ProbeOneAsync(IPAddress ip, ScanOptions opt)
        {
            ProbeResult r = await CheckAliveAsync(ip, opt, true).ConfigureAwait(false);
            if (opt.Subnet != null && opt.Subnet.LocalIp != null && ip.Equals(opt.Subnet.LocalIp))
            {
                r.Up = true;
                r.IsThisPc = true;
                r.Mac = opt.Subnet.LocalMac;
                r.Hostname = Environment.MachineName;
            }
            if (!r.Up && opt.Subnet != null && opt.Subnet.Contains(ip))
            {
                string mac = Native.ArpLookup(ip);
                if (mac != null) { r.Up = true; r.Mac = mac; }
            }
            if (r.Up) await EnrichAsync(r, opt).ConfigureAwait(false);
            return r;
        }

        static async Task<ProbeResult> CheckAliveAsync(IPAddress ip, ScanOptions opt, bool tryHarder)
        {
            ProbeResult r = new ProbeResult();
            r.Ip = ip;
            try
            {
                using (Ping p = new Ping())
                {
                    PingReply reply = await p.SendPingAsync(ip, opt.PingTimeoutMs).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success)
                    {
                        r.Up = true;
                        r.Rtt = (int)reply.RoundtripTime;
                        if (reply.Options != null) r.Ttl = reply.Options.Ttl;
                        return r;
                    }
                }
            }
            catch (Exception) { }

            // A device we already know about: make sure it's really gone
            // before turning it red (phones and some printers ignore ping).
            if (tryHarder)
            {
                Task<int>[] probes = AlivePorts.Select(port => TryConnectAsync(ip, port, 600)).ToArray();
                int[] outcome = await Task.WhenAll(probes).ConfigureAwait(false);
                if (outcome.Any(o => o >= 0)) r.Up = true;
            }
            return r;
        }

        static async Task EnrichAsync(ProbeResult r, ScanOptions opt)
        {
            if (r.Mac == null && opt.Subnet != null && opt.Subnet.Contains(r.Ip))
                r.Mac = await Task.Run(() => Native.ArpLookup(r.Ip)).ConfigureAwait(false);

            Task<List<int>> ports = opt.CheckPorts ? OpenPortsAsync(r.Ip) : Task.FromResult<List<int>>(null);
            if (opt.LookupNames && r.Hostname == null) r.Hostname = await LookupNameAsync(r.Ip).ConfigureAwait(false);
            r.OpenPorts = await ports.ConfigureAwait(false);
        }

        static async Task<List<int>> OpenPortsAsync(IPAddress ip)
        {
            Task<int>[] probes = CommonPorts.Select(port => TryConnectAsync(ip, port, 700)).ToArray();
            int[] outcome = await Task.WhenAll(probes).ConfigureAwait(false);
            List<int> open = new List<int>();
            for (int i = 0; i < CommonPorts.Length; i++)
                if (outcome[i] == 1) open.Add(CommonPorts[i]);
            return open;
        }

        // 1 = open, 0 = refused (so something is there), -1 = no answer.
        static async Task<int> TryConnectAsync(IPAddress ip, int port, int timeoutMs)
        {
            TcpClient c = new TcpClient();
            try
            {
                Task t = c.ConnectAsync(ip, port);
                Task first = await Task.WhenAny(t, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (first != t)
                {
                    Observe(t);
                    return -1;
                }
                if (t.IsFaulted)
                {
                    SocketException se = t.Exception.InnerException as SocketException;
                    if (se != null && se.SocketErrorCode == SocketError.ConnectionRefused) return 0;
                    return -1;
                }
                return 1;
            }
            catch (Exception) { return -1; }
            finally { c.Close(); }
        }

        static void Observe(Task t)
        {
            t.ContinueWith(x => { var unused = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        // DNS first, then the device's own answer (NetBIOS for Windows / NAS,
        // mDNS for Apple, Linux and most smart devices).
        static async Task<string> LookupNameAsync(IPAddress ip)
        {
            try
            {
                Task<IPHostEntry> dns = Dns.GetHostEntryAsync(ip);
                if (await Task.WhenAny(dns, Task.Delay(2000)).ConfigureAwait(false) == dns && !dns.IsFaulted)
                {
                    string h = dns.Result.HostName;
                    if (!string.IsNullOrEmpty(h) && h != ip.ToString()) return h;
                }
                else Observe(dns);
            }
            catch (Exception) { }

            Task<string> nb = NetBiosNameAsync(ip, 900);
            Task<string> md = MdnsNameAsync(ip, 900);
            string nbName = await nb.ConfigureAwait(false);
            string mdName = await md.ConfigureAwait(false);
            if (!string.IsNullOrEmpty(nbName)) return nbName;
            if (!string.IsNullOrEmpty(mdName)) return mdName;
            return "";
        }

        static async Task<byte[]> UdpAskAsync(IPAddress ip, int port, byte[] query, int timeoutMs)
        {
            UdpClient u = new UdpClient(0, AddressFamily.InterNetwork);
            try
            {
                await u.SendAsync(query, query.Length, new IPEndPoint(ip, port)).ConfigureAwait(false);
                Task<UdpReceiveResult> rx = u.ReceiveAsync();
                if (await Task.WhenAny(rx, Task.Delay(timeoutMs)).ConfigureAwait(false) != rx || rx.IsFaulted)
                {
                    Observe(rx);
                    return null;
                }
                return rx.Result.Buffer;
            }
            catch (Exception) { return null; }
            finally { u.Close(); }
        }

        static async Task<string> NetBiosNameAsync(IPAddress ip, int timeoutMs)
        {
            byte[] q = new byte[50];
            q[0] = 0x4C; q[1] = 0x41;              // transaction id
            q[5] = 1;                             // one question
            q[12] = 0x20;                         // name length 32
            q[13] = (byte)'C'; q[14] = (byte)'K'; // "*" encoded
            for (int i = 15; i < 45; i++) q[i] = (byte)'A';
            q[47] = 0x21;                         // NBSTAT
            q[49] = 1;                            // class IN
            byte[] a = await UdpAskAsync(ip, 137, q, timeoutMs).ConfigureAwait(false);
            if (a == null || a.Length < 57) return null;
            int count = a[56];
            for (int i = 0; i < count; i++)
            {
                int off = 57 + i * 18;
                if (off + 18 > a.Length) break;
                string name = Encoding.ASCII.GetString(a, off, 15).Trim(' ', '\0');
                byte suffix = a[off + 15];
                bool group = (a[off + 16] & 0x80) != 0;
                if (suffix == 0 && !group && name.Length > 0) return name;
            }
            return null;
        }

        static async Task<string> MdnsNameAsync(IPAddress ip, int timeoutMs)
        {
            byte[] b = ip.GetAddressBytes();
            string qname = b[3] + "." + b[2] + "." + b[1] + "." + b[0] + ".in-addr.arpa";
            List<byte> q = new List<byte> { 0x4C, 0x42, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (string label in qname.Split('.'))
            {
                q.Add((byte)label.Length);
                q.AddRange(Encoding.ASCII.GetBytes(label));
            }
            q.AddRange(new byte[] { 0, 0, 12, 0x80, 1 });   // PTR, unicast reply wanted
            byte[] a = await UdpAskAsync(ip, 5353, q.ToArray(), timeoutMs).ConfigureAwait(false);
            if (a == null || a.Length < 12) return null;
            try
            {
                int qd = (a[4] << 8) | a[5];
                int an = (a[6] << 8) | a[7];
                int off = 12;
                for (int i = 0; i < qd; i++) { ReadDnsName(a, ref off); off += 4; }
                for (int i = 0; i < an; i++)
                {
                    ReadDnsName(a, ref off);
                    int type = (a[off] << 8) | a[off + 1];
                    int rdlen = (a[off + 8] << 8) | a[off + 9];
                    off += 10;
                    if (type == 12)
                    {
                        int p = off;
                        string name = ReadDnsName(a, ref p).TrimEnd('.');
                        if (name.Length > 0) return name;
                    }
                    off += rdlen;
                }
            }
            catch (Exception) { }
            return null;
        }

        static string ReadDnsName(byte[] a, ref int off)
        {
            StringBuilder sb = new StringBuilder();
            int p = off;
            bool jumped = false;
            int guard = 0;
            while (guard++ < 64)
            {
                int len = a[p];
                if (len == 0) { p++; break; }
                if ((len & 0xC0) == 0xC0)
                {
                    int target = ((len & 0x3F) << 8) | a[p + 1];
                    if (!jumped) off = p + 2;
                    jumped = true;
                    p = target;
                    continue;
                }
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.UTF8.GetString(a, p + 1, len));
                p += 1 + len;
            }
            if (!jumped) off = p;
            return sb.ToString();
        }

        // ---- guesses (only used for fields you haven't typed in yourself)

        public static string GuessOs(int ttl)
        {
            if (ttl <= 0) return "";
            if (ttl > 128) return "Network device";
            if (ttl > 64) return "Windows";
            return "Linux / macOS / Android";
        }

        public static string GuessType(ProbeResult r, string vendor, Subnet sn)
        {
            if (r.IsThisPc) return "This PC";
            List<int> ports = r.OpenPorts ?? new List<int>();
            string v = (vendor ?? "").ToLowerInvariant();
            string mac = r.Mac ?? "";
            Func<string[], bool> vendorIs = names => names.Any(n => v.Contains(n));

            if (sn != null && sn.Gateway != null && r.Ip.Equals(sn.Gateway)) return "Router";
            if (vendorIs(new[] { "proxmox", "vmware", "xensource", "parallels" })
                || mac.StartsWith("52:54:00") || mac.StartsWith("00:15:5D")) return "Virtual machine";
            if (ports.Contains(8006)) return "Proxmox host";
            if (vendorIs(new[] { "raspberry" })) return "Raspberry Pi";
            if (ports.Contains(9100) || vendorIs(new[] { "epson", "brother", "canon", "kyocera", "lexmark", "xerox", "ricoh" }))
                return "Printer";
            if (vendorIs(new[] { "synology", "qnap", "western digital", "asustor" })) return "NAS";
            if (vendorIs(new[] { "hikvision", "dahua", "reolink", "axis comm", "amcrest", "wyze" })) return "Camera";
            if (vendorIs(new[] { "sonos", "bose" })) return "Speaker";
            if (vendorIs(new[] { "roku", "lg electronics", "vizio", "tcl", "hisense" })) return "TV / media";
            if (vendorIs(new[] { "nintendo", "sony interactive" })) return "Games console";
            if (vendorIs(new[] { "espressif", "tuya", "shelly", "samjin", "smartthings", "philips lighting",
                "signify", "ezviz", "meross", "lifx" })) return "IoT";
            if (vendorIs(new[] { "ubiquiti", "netgear", "cisco", "mikrotik", "aruba", "juniper", "ruckus",
                "zyxel", "draytek", "eero" })) return "Network";
            if (ports.Contains(62078) || vendorIs(new[] { "apple" })) return "Apple device";
            if (vendorIs(new[] { "google", "nest" })) return "Google device";
            if (vendorIs(new[] { "amazon" })) return "Amazon device";
            if (ports.Contains(3389) || (ports.Contains(445) && r.Ttl > 64 && r.Ttl <= 128)) return "Windows PC";
            if (ports.Contains(53)) return "DNS server";
            if (ports.Contains(8123)) return "Home Assistant";
            if (ports.Contains(32400)) return "Plex server";
            if (Net.IsRandomMac(mac)) return "Phone / laptop";
            if (ports.Contains(22) && r.Ttl > 0 && r.Ttl <= 64) return "Linux";
            return "";
        }
    }

    // --------------------------------------------------------------- columns

    class ColumnDef
    {
        public string Key, Header;
        public bool Editable, DefaultVisible;
        public int Width;
        public string Tip;

        public ColumnDef(string key, string header, int width, bool visible, bool editable, string tip)
        {
            Key = key; Header = header; Width = width; DefaultVisible = visible; Editable = editable; Tip = tip;
        }
    }

    static class Columns
    {
        public static readonly ColumnDef[] All = {
            new ColumnDef("Status", "Status", 54, true, false, "Green: answered the last check. Red: didn't answer. Grey: not checked yet."),
            new ColumnDef("Ip", "IP address", 110, true, false, null),
            new ColumnDef("Hostname", "Hostname", 140, true, true, null),
            new ColumnDef("Name", "Name", 150, true, true, "Your own name for the device"),
            new ColumnDef("Type", "Type", 110, true, true, null),
            new ColumnDef("Mac", "MAC address", 125, true, true, null),
            new ColumnDef("Vendor", "Vendor", 140, true, true, "Manufacturer, from the MAC address"),
            new ColumnDef("Os", "OS", 120, false, true, "Guessed from the ping reply until you type your own"),
            new ColumnDef("Model", "Model", 120, false, true, null),
            new ColumnDef("Serial", "Serial number", 110, false, true, null),
            new ColumnDef("Location", "Location", 100, true, true, null),
            new ColumnDef("Owner", "Owner", 100, false, true, null),
            new ColumnDef("OpenPorts", "Open ports", 130, false, false, "Common TCP ports that accepted a connection"),
            new ColumnDef("ResponseMs", "Response", 70, false, false, "Ping time"),
            new ColumnDef("LastSeen", "Last seen", 100, true, false, null),
            new ColumnDef("FirstSeen", "First seen", 100, false, false, null),
            new ColumnDef("StatusChanged", "Status changed", 110, false, false, "When it last went up or down"),
            new ColumnDef("LastChecked", "Last checked", 100, false, false, null),
            new ColumnDef("Source", "Added by", 70, false, false, "Scan or Manual"),
            new ColumnDef("Notes", "Notes", 220, true, true, null),
        };

        public static ColumnDef Find(string key)
        {
            foreach (ColumnDef c in All) if (c.Key == key) return c;
            return null;
        }

        public static string Get(Device d, string key)
        {
            switch (key)
            {
                case "Status":
                    return d.Status == DeviceStatus.Up ? "Up" : d.Status == DeviceStatus.Down ? "Down" : "Not checked";
                case "Ip": return d.Ip;
                case "Hostname": return d.Hostname;
                case "Name": return d.Name;
                case "Type": return d.Type;
                case "Mac": return d.Mac;
                case "Vendor": return d.Vendor;
                case "Os": return d.Os;
                case "Model": return d.Model;
                case "Serial": return d.Serial;
                case "Location": return d.Location;
                case "Owner": return d.Owner;
                case "OpenPorts": return d.OpenPorts;
                case "ResponseMs": return d.ResponseMs < 0 ? "" : (d.ResponseMs == 0 ? "<1 ms" : d.ResponseMs + " ms");
                case "LastSeen": return When(d.LastSeen);
                case "FirstSeen": return When(d.FirstSeen);
                case "StatusChanged": return When(d.StatusChanged);
                case "LastChecked": return When(d.LastChecked);
                case "Source": return d.Source;
                case "Notes": return d.Notes;
            }
            return "";
        }

        public static void Set(Device d, string key, string v)
        {
            v = v ?? "";
            switch (key)
            {
                case "Hostname": d.Hostname = v; break;
                case "Name": d.Name = v; break;
                case "Type": d.Type = v; break;
                case "Mac": d.Mac = v; break;
                case "Vendor": d.Vendor = v; break;
                case "Os": d.Os = v; break;
                case "Model": d.Model = v; break;
                case "Serial": d.Serial = v; break;
                case "Location": d.Location = v; break;
                case "Owner": d.Owner = v; break;
                case "Notes": d.Notes = v; break;
            }
        }

        public static string When(DateTime? t)
        {
            if (!t.HasValue) return "";
            DateTime v = t.Value;
            if (v.Date == DateTime.Today) return v.ToString("HH:mm");
            if (v.Year == DateTime.Today.Year) return v.ToString("d MMM HH:mm");
            return v.ToString("d MMM yyyy");
        }

        public static IComparable SortKey(Device d, string key)
        {
            switch (key)
            {
                case "Ip": return Net.SortValue(d.Ip);
                case "Status": return d.Status == DeviceStatus.Down ? 0 : d.Status == DeviceStatus.Unknown ? 1 : 2;
                case "ResponseMs": return d.ResponseMs < 0 ? int.MaxValue : d.ResponseMs;
                case "LastSeen": return d.LastSeen ?? DateTime.MinValue;
                case "FirstSeen": return d.FirstSeen ?? DateTime.MinValue;
                case "StatusChanged": return d.StatusChanged ?? DateTime.MinValue;
                case "LastChecked": return d.LastChecked ?? DateTime.MinValue;
            }
            return (Get(d, key) ?? "").ToLowerInvariant();
        }
    }

    static class AppIcon
    {
        public static Icon Load(Size size)
        {
            try
            {
                using (Stream st = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("ctrlit-lan.ico"))
                {
                    if (st != null) return new Icon(st, size);
                }
            }
            catch (Exception) { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { return SystemIcons.Application; }
        }
    }

    static class Dots
    {
        public static readonly Color Up = Color.FromArgb(46, 160, 67);
        public static readonly Color Down = Color.FromArgb(218, 54, 51);
        public static readonly Color Unknown = Color.FromArgb(160, 160, 160);
        public static readonly Color NewRow = Color.FromArgb(226, 239, 255);

        public static Color For(DeviceStatus s)
        {
            return s == DeviceStatus.Up ? Up : s == DeviceStatus.Down ? Down : Unknown;
        }

        public static Bitmap Make(Color c, int size)
        {
            Bitmap b = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (SolidBrush br = new SolidBrush(c)) g.FillEllipse(br, 1, 1, size - 3, size - 3);
            }
            return b;
        }
    }

    // ------------------------------------------------------------ main form

    class MainForm : Form
    {
        readonly DataFile data;
        AppSettings S { get { return data.Settings; } }

        readonly DataGridView grid = new DataGridView();
        readonly ToolStrip toolbar = new ToolStrip();
        readonly MenuStrip menu = new MenuStrip();
        readonly StatusStrip status = new StatusStrip();
        readonly ToolStripButton scanButton;
        readonly ToolStripTextBox filterBox = new ToolStripTextBox();
        readonly ToolStripLabel subnetLabel = new ToolStripLabel();
        readonly ToolStripStatusLabel upLabel = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel downLabel = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel unknownLabel = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel newLabel = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel scanLabel = new ToolStripStatusLabel();
        readonly ToolStripProgressBar progressBar = new ToolStripProgressBar();
        readonly ContextMenuStrip rowMenu = new ContextMenuStrip();
        readonly ContextMenuStrip headerMenu = new ContextMenuStrip();
        readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer();
        readonly NotifyIcon tray = new NotifyIcon();

        bool scanning;
        bool reallyExit;
        bool refreshPending;
        bool loadingColumns;
        DateTime nextScan = DateTime.MaxValue;
        TimeSpan lastScanTook;
        Subnet subnet;
        AboutForm about;

        public MainForm(DataFile d)
        {
            data = d;
            Text = AppInfo.Name;
            Icon = AppIcon.Load(new Size(32, 32));
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1180, 640);
            MinimumSize = new Size(640, 320);
            KeyPreview = true;

            // ---- menus
            ToolStripMenuItem file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(Item("&Export to CSV...", null, Keys.Control | Keys.S, delegate { ExportCsv(); }));
            file.DropDownItems.Add(Item("&Open data folder", null, Keys.None, delegate { OpenDataFolder(); }));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Item("E&xit", null, Keys.None, delegate { ExitApp(); }));

            ToolStripMenuItem devices = new ToolStripMenuItem("&Devices");
            devices.DropDownItems.Add(Item("&Add device...", null, Keys.Control | Keys.N, delegate { AddDevice(); }));
            devices.DropDownItems.Add(Item("&Edit device...", null, Keys.Control | Keys.E, delegate { EditSelected(); }));
            devices.DropDownItems.Add(Item("&Delete", null, Keys.None, delegate { DeleteSelected(false); }));
            devices.DropDownItems.Add(Item("Delete and &ignore", null, Keys.None, delegate { DeleteSelected(true); }));
            devices.DropDownItems.Add(new ToolStripSeparator());
            devices.DropDownItems.Add(Item("&Check selected now", null, Keys.None, delegate { CheckSelected(); }));
            devices.DropDownItems.Add(Item("Mark all as &seen", null, Keys.None, delegate { MarkAllSeen(); }));

            ToolStripMenuItem view = new ToolStripMenuItem("&View");
            view.DropDownOpening += delegate { FillColumnMenu(view.DropDownItems); };
            view.DropDownItems.Add("(columns)");   // so the menu opens; replaced on opening
            view.DropDown.Closing += KeepOpenOnColumnClick;

            ToolStripMenuItem scanMenu = new ToolStripMenuItem("&Scan");
            scanMenu.DropDownItems.Add(Item("&Scan now", null, Keys.F5, delegate { ScanNow(); }));
            scanMenu.DropDownItems.Add(Item("Se&ttings...", null, Keys.None, delegate { ShowSettings(); }));

            ToolStripMenuItem help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add(Item("&About " + AppInfo.Name, null, Keys.None, delegate { ShowAbout(); }));

            menu.Items.AddRange(new ToolStripItem[] { file, devices, view, scanMenu, help });

            // ---- toolbar
            toolbar.GripStyle = ToolStripGripStyle.Hidden;
            toolbar.Padding = new Padding(6, 2, 6, 2);
            scanButton = TextButton("Scan now", "Scan the network now (F5)", delegate { ScanNow(); });
            toolbar.Items.Add(scanButton);
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(TextButton("Add", "Add a device by hand (Ctrl+N)", delegate { AddDevice(); }));
            toolbar.Items.Add(TextButton("Edit", "Edit the selected device (Enter)", delegate { EditSelected(); }));
            toolbar.Items.Add(TextButton("Delete", "Delete the selected devices (Delete)", delegate { DeleteSelected(false); }));
            toolbar.Items.Add(new ToolStripSeparator());
            ToolStripDropDownButton colButton = new ToolStripDropDownButton("Columns");
            colButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            colButton.ToolTipText = "Show or hide columns";
            colButton.DropDownItems.Add("(columns)");
            colButton.DropDownOpening += delegate { FillColumnMenu(colButton.DropDownItems); };
            colButton.DropDown.Closing += KeepOpenOnColumnClick;
            toolbar.Items.Add(colButton);
            toolbar.Items.Add(TextButton("Export CSV", "Save the list as a CSV file (Ctrl+S)", delegate { ExportCsv(); }));
            toolbar.Items.Add(TextButton("Settings", "Subnet, scan interval and other options", delegate { ShowSettings(); }));

            filterBox.Alignment = ToolStripItemAlignment.Right;
            filterBox.Size = new Size(180, 25);
            filterBox.ToolTipText = "Filter: type part of an IP, name, vendor, note... (Ctrl+F)";
            filterBox.TextChanged += delegate { RefreshGrid(); };
            ToolStripLabel filterLabel = new ToolStripLabel("Filter:");
            filterLabel.Alignment = ToolStripItemAlignment.Right;
            subnetLabel.Alignment = ToolStripItemAlignment.Right;
            subnetLabel.ForeColor = SystemColors.GrayText;
            toolbar.Items.Add(filterBox);
            toolbar.Items.Add(filterLabel);
            toolbar.Items.Add(subnetLabel);

            // ---- grid
            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToOrderColumns = true;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = true;
            grid.RowHeadersVisible = false;
            grid.BackgroundColor = SystemColors.Window;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = Color.FromArgb(230, 230, 230);
            grid.EditMode = DataGridViewEditMode.EditOnF2;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.EnableHeadersVisualStyles = true;
            grid.ShowCellToolTips = true;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(204, 228, 247);
            grid.DefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
            grid.RowTemplate.Height = (int)Math.Ceiling(Font.GetHeight() * 1.75);
            typeof(DataGridView).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(grid, true, null);

            BuildColumns();
            grid.CellPainting += GridCellPainting;
            grid.CellDoubleClick += GridCellDoubleClick;
            grid.CellEndEdit += GridCellEndEdit;
            grid.ColumnHeaderMouseClick += GridHeaderClick;
            grid.CellMouseDown += GridCellMouseDown;
            grid.KeyDown += GridKeyDown;
            grid.ColumnDisplayIndexChanged += delegate { if (!loadingColumns) SaveColumnLayout(); };
            grid.ColumnWidthChanged += delegate { if (!loadingColumns) SaveColumnLayout(); };
            grid.DataError += delegate(object s, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };

            // ---- row menu
            rowMenu.Items.Add(Item("Edit...", null, Keys.None, delegate { EditSelected(); }));
            rowMenu.Items.Add(Item("Check now", null, Keys.None, delegate { CheckSelected(); }));
            rowMenu.Items.Add(Item("Mark as seen", null, Keys.None, delegate { MarkSelectedSeen(); }));
            rowMenu.Items.Add(new ToolStripSeparator());
            rowMenu.Items.Add(Item("Ping (continuous)", null, Keys.None, delegate { RunForSelected("cmd.exe", "/k ping -t {0}"); }));
            rowMenu.Items.Add(Item("Open in browser (http)", null, Keys.None, delegate { OpenForSelected("http://{0}/"); }));
            rowMenu.Items.Add(Item("Open in browser (https)", null, Keys.None, delegate { OpenForSelected("https://{0}/"); }));
            rowMenu.Items.Add(Item("Remote desktop", null, Keys.None, delegate { RunForSelected("mstsc.exe", "/v:{0}"); }));
            rowMenu.Items.Add(Item("Open file share", null, Keys.None, delegate { OpenForSelected(@"\\{0}"); }));
            rowMenu.Items.Add(new ToolStripSeparator());
            rowMenu.Items.Add(Item("Copy IP address", null, Keys.None, delegate { CopyField("Ip"); }));
            rowMenu.Items.Add(Item("Copy MAC address", null, Keys.None, delegate { CopyField("Mac"); }));
            rowMenu.Items.Add(Item("Copy row", null, Keys.None, delegate { CopyRows(); }));
            rowMenu.Items.Add(new ToolStripSeparator());
            rowMenu.Items.Add(Item("Delete", null, Keys.None, delegate { DeleteSelected(false); }));
            rowMenu.Items.Add(Item("Delete and ignore", null, Keys.None, delegate { DeleteSelected(true); }));

            headerMenu.Items.Add("(columns)");
            headerMenu.Opening += delegate { FillColumnMenu(headerMenu.Items); };
            headerMenu.Closing += KeepOpenOnColumnClick;

            // ---- status bar
            upLabel.Image = Dots.Make(Dots.Up, 12);
            downLabel.Image = Dots.Make(Dots.Down, 12);
            unknownLabel.Image = Dots.Make(Dots.Unknown, 12);
            upLabel.ToolTipText = "Answered the last check";
            downLabel.ToolTipText = "Known devices that didn't answer the last check";
            unknownLabel.ToolTipText = "Added by hand and not checked yet";
            ToolStripStatusLabel spring = new ToolStripStatusLabel();
            spring.Spring = true;
            progressBar.Visible = false;
            progressBar.Size = new Size(120, 16);
            status.Items.AddRange(new ToolStripItem[] { upLabel, downLabel, unknownLabel, newLabel, spring, progressBar, scanLabel });
            status.SizingGrip = true;

            Controls.Add(grid);
            Controls.Add(toolbar);
            Controls.Add(menu);
            Controls.Add(status);
            MainMenuStrip = menu;

            // ---- tray (only shown while the window is hidden)
            tray.Icon = AppIcon.Load(new Size(16, 16));
            tray.Text = AppInfo.Name;
            ContextMenuStrip trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add(Item("Open", null, Keys.None, delegate { RestoreFromTray(); }));
            trayMenu.Items.Add(Item("Scan now", null, Keys.None, delegate { ScanNow(); }));
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(Item("Exit", null, Keys.None, delegate { ExitApp(); }));
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { RestoreFromTray(); };

            clock.Interval = 1000;
            clock.Tick += delegate { Tick(); };

            RestoreWindow();
            ResolveSubnet();
            RefreshGrid();
            UpdateStatusBar();
        }

        // ---------------------------------------------------------- helpers

        static ToolStripMenuItem Item(string text, Image img, Keys keys, EventHandler click)
        {
            ToolStripMenuItem i = new ToolStripMenuItem(text, img, click);
            if (keys != Keys.None) i.ShortcutKeys = keys;
            return i;
        }

        static ToolStripButton TextButton(string text, string tip, EventHandler click)
        {
            ToolStripButton b = new ToolStripButton(text, null, click);
            b.DisplayStyle = ToolStripItemDisplayStyle.Text;
            b.ToolTipText = tip;
            b.Padding = new Padding(4, 0, 4, 0);
            return b;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            clock.Start();
            if (S.ScanOnStart || data.Devices.Count == 0) ScanNow();
            else nextScan = DateTime.Now.AddMinutes(S.IntervalMinutes);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F))
            {
                filterBox.Focus();
                filterBox.SelectAll();
                return true;
            }
            if (keyData == Keys.Escape && filterBox.Focused && filterBox.Text.Length > 0)
            {
                filterBox.Text = "";
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void ResolveSubnet()
        {
            string err;
            subnet = S.Subnet.Trim().Length > 0 ? Net.Parse(S.Subnet, out err) : Net.Detect();
            subnetLabel.Text = subnet == null ? "No network found" : "Subnet " + subnet + (S.Subnet.Trim().Length == 0 ? " (auto)" : "");
        }

        ScanOptions Options()
        {
            ScanOptions o = new ScanOptions();
            o.Subnet = subnet;
            o.PingTimeoutMs = S.PingTimeoutMs;
            o.CheckPorts = S.CheckPorts;
            return o;
        }

        // ----------------------------------------------------------- columns

        void BuildColumns()
        {
            loadingColumns = true;
            grid.Columns.Clear();
            foreach (ColumnDef c in Columns.All)
            {
                DataGridViewTextBoxColumn col = new DataGridViewTextBoxColumn();
                col.Name = c.Key;
                col.HeaderText = c.Header;
                col.Width = c.Width;
                col.ReadOnly = !c.Editable;
                col.SortMode = DataGridViewColumnSortMode.Programmatic;
                col.Visible = c.DefaultVisible;
                if (c.Tip != null) col.ToolTipText = c.Tip;
                if (c.Key == "Ip" || c.Key == "Mac")
                    col.DefaultCellStyle.Font = new Font("Consolas", Font.SizeInPoints);
                grid.Columns.Add(col);
            }
            // Saved layout: order, width and which are shown.
            List<ColumnState> saved = S.Columns.Where(cs => grid.Columns.Contains(cs.Key)).OrderBy(cs => cs.Index).ToList();
            foreach (ColumnState cs in saved)
            {
                DataGridViewColumn col = grid.Columns[cs.Key];
                if (cs.Width > 10) col.Width = cs.Width;
                col.Visible = cs.Visible;
            }
            for (int i = 0; i < saved.Count; i++)
            {
                try { grid.Columns[saved[i].Key].DisplayIndex = i; }
                catch (Exception) { }
            }
            if (!grid.Columns.Cast<DataGridViewColumn>().Any(c => c.Visible)) grid.Columns["Ip"].Visible = true;
            loadingColumns = false;
            ShowSortGlyph();
        }

        void SaveColumnLayout()
        {
            S.Columns = grid.Columns.Cast<DataGridViewColumn>().Select(c => new ColumnState
            {
                Key = c.Name, Width = c.Width, Index = c.DisplayIndex, Visible = c.Visible
            }).ToList();
        }

        void FillColumnMenu(ToolStripItemCollection items)
        {
            items.Clear();
            foreach (DataGridViewColumn col in grid.Columns.Cast<DataGridViewColumn>().OrderBy(c => c.DisplayIndex))
            {
                ToolStripMenuItem i = new ToolStripMenuItem(col.HeaderText);
                i.Checked = col.Visible;
                i.CheckOnClick = true;
                i.Tag = col.Name;
                i.CheckedChanged += delegate(object s, EventArgs e)
                {
                    ToolStripMenuItem mi = (ToolStripMenuItem)s;
                    DataGridViewColumn c = grid.Columns[(string)mi.Tag];
                    int shown = grid.Columns.Cast<DataGridViewColumn>().Count(x => x.Visible);
                    if (!mi.Checked && shown <= 1) { mi.Checked = true; return; }
                    c.Visible = mi.Checked;
                    SaveColumnLayout();
                    Store.Save(data);
                };
                items.Add(i);
            }
            items.Add(new ToolStripSeparator());
            ToolStripMenuItem all = new ToolStripMenuItem("Show all columns");
            all.Tag = "close";
            all.Click += delegate
            {
                foreach (DataGridViewColumn c in grid.Columns) c.Visible = true;
                SaveColumnLayout();
                Store.Save(data);
            };
            items.Add(all);
            ToolStripMenuItem reset = new ToolStripMenuItem("Reset columns");
            reset.Tag = "close";
            reset.Click += delegate
            {
                S.Columns.Clear();
                BuildColumns();
                RefreshGrid();
                SaveColumnLayout();
                Store.Save(data);
            };
            items.Add(reset);
        }

        // Ticking a column on or off leaves the menu open so you can pick several.
        void KeepOpenOnColumnClick(object sender, ToolStripDropDownClosingEventArgs e)
        {
            if (e.CloseReason != ToolStripDropDownCloseReason.ItemClicked) return;
            ToolStripDropDown dd = sender as ToolStripDropDown;
            if (dd == null) return;
            Point p = dd.PointToClient(Cursor.Position);
            ToolStripItem hit = dd.GetItemAt(p);
            if (hit is ToolStripMenuItem && ((ToolStripMenuItem)hit).CheckOnClick) e.Cancel = true;
        }

        // -------------------------------------------------------------- grid

        Device DeviceAt(int row)
        {
            if (row < 0 || row >= grid.Rows.Count) return null;
            return grid.Rows[row].Tag as Device;
        }

        List<Device> Selected()
        {
            List<Device> list = new List<Device>();
            foreach (DataGridViewRow r in grid.SelectedRows)
            {
                Device d = r.Tag as Device;
                if (d != null) list.Add(d);
            }
            if (list.Count == 0 && grid.CurrentRow != null)
            {
                Device d = grid.CurrentRow.Tag as Device;
                if (d != null) list.Add(d);
            }
            return list.OrderBy(d => Net.SortValue(d.Ip)).ToList();
        }

        bool Matches(Device d, string filter)
        {
            if (filter.Length == 0) return true;
            foreach (ColumnDef c in Columns.All)
            {
                string v = Columns.Get(d, c.Key);
                if (v != null && v.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        void RefreshGrid()
        {
            if (grid.IsCurrentCellInEditMode) { refreshPending = true; return; }
            refreshPending = false;

            HashSet<Device> selected = new HashSet<Device>(Selected());
            Device current = grid.CurrentRow != null ? grid.CurrentRow.Tag as Device : null;
            int firstRow = grid.FirstDisplayedScrollingRowIndex;
            string currentCol = grid.CurrentCell != null ? grid.Columns[grid.CurrentCell.ColumnIndex].Name : "Ip";

            string filter = filterBox.Text.Trim();
            IEnumerable<Device> list = data.Devices.Where(d => Matches(d, filter));
            string key = Columns.Find(S.SortKey) != null ? S.SortKey : "Ip";
            list = S.SortAscending
                ? list.OrderBy(d => Columns.SortKey(d, key)).ThenBy(d => Net.SortValue(d.Ip))
                : list.OrderByDescending(d => Columns.SortKey(d, key)).ThenBy(d => Net.SortValue(d.Ip));

            grid.SuspendLayout();
            grid.Rows.Clear();
            List<DataGridViewRow> rows = new List<DataGridViewRow>();
            foreach (Device d in list)
            {
                DataGridViewRow row = new DataGridViewRow();
                row.CreateCells(grid);
                FillRow(row, d);
                rows.Add(row);
            }
            grid.Rows.AddRange(rows.ToArray());
            grid.ClearSelection();
            foreach (DataGridViewRow row in grid.Rows)
            {
                Device d = (Device)row.Tag;
                if (d == current && grid.Columns[currentCol].Visible)
                {
                    try { grid.CurrentCell = row.Cells[currentCol]; } catch (Exception) { }
                }
                if (selected.Contains(d)) row.Selected = true;
            }
            if (selected.Count == 0) grid.ClearSelection();
            if (firstRow >= 0 && firstRow < grid.Rows.Count)
            {
                try { grid.FirstDisplayedScrollingRowIndex = firstRow; } catch (Exception) { }
            }
            grid.ResumeLayout();
            UpdateStatusBar();
        }

        void FillRow(DataGridViewRow row, Device d)
        {
            row.Tag = d;
            foreach (DataGridViewColumn col in grid.Columns)
            {
                DataGridViewCell cell = row.Cells[col.Index];
                cell.Value = col.Name == "Status" ? "" : Columns.Get(d, col.Name);
                if (col.Name == "Status")
                {
                    string tip = Columns.Get(d, "Status");
                    if (d.Status == DeviceStatus.Up && d.ResponseMs >= 0) tip += " (" + Columns.Get(d, "ResponseMs") + ")";
                    if (d.Status == DeviceStatus.Down && d.StatusChanged.HasValue) tip += " since " + Columns.When(d.StatusChanged);
                    if (d.IsNew) tip += "\nNew: found by the last scan";
                    cell.ToolTipText = tip;
                }
                else if (d.IsLocked(col.Name)) cell.ToolTipText = "Typed in by you; scans won't change it";
                else cell.ToolTipText = "";
                cell.Style.BackColor = d.IsNew ? Dots.NewRow : Color.Empty;
            }
        }

        void RefreshRow(Device d)
        {
            foreach (DataGridViewRow row in grid.Rows)
                if (row.Tag == d) { FillRow(row, d); break; }
        }

        void GridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Status") return;
            Device d = DeviceAt(e.RowIndex);
            if (d == null) return;
            e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
            int size = Math.Max(10, (int)(Font.GetHeight() * 0.75));
            Rectangle r = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - size) / 2,
                e.CellBounds.Y + (e.CellBounds.Height - size) / 2, size, size);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Dots.For(d.Status))) e.Graphics.FillEllipse(b, r);
            e.Handled = true;
        }

        void GridCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            if (e.ColumnIndex >= 0 && !grid.Columns[e.ColumnIndex].ReadOnly)
            {
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                grid.BeginEdit(true);
            }
            else EditSelected();
        }

        void GridCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            Device d = DeviceAt(e.RowIndex);
            if (d != null)
            {
                string key = grid.Columns[e.ColumnIndex].Name;
                string value = Convert.ToString(grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value).Trim();
                string old = Columns.Get(d, key);
                if (key == "Mac" && value.Length > 0)
                {
                    string norm = Net.NormaliseMac(value);
                    if (norm == null)
                    {
                        MessageBox.Show(this, "That doesn't look like a MAC address. Use the form AA:BB:CC:DD:EE:FF.",
                            AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        value = old;
                    }
                    else value = norm;
                }
                if (value != old)
                {
                    Columns.Set(d, key, value);
                    d.Lock(key);
                    if (key == "Mac" && !d.IsLocked("Vendor")) d.Vendor = Vendors.Lookup(d.Mac);
                    d.IsNew = false;
                    Store.Save(data);
                }
                RefreshRow(d);
                UpdateStatusBar();
            }
            if (refreshPending) BeginInvoke(new MethodInvoker(RefreshGrid));
        }

        void GridHeaderClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                headerMenu.Show(Cursor.Position);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            string key = grid.Columns[e.ColumnIndex].Name;
            if (S.SortKey == key) S.SortAscending = !S.SortAscending;
            else
            {
                S.SortKey = key;
                S.SortAscending = !(key == "LastSeen" || key == "FirstSeen" || key == "StatusChanged" || key == "LastChecked");
            }
            ShowSortGlyph();
            RefreshGrid();
        }

        void ShowSortGlyph()
        {
            foreach (DataGridViewColumn c in grid.Columns)
                c.HeaderCell.SortGlyphDirection = c.Name == S.SortKey
                    ? (S.SortAscending ? SortOrder.Ascending : SortOrder.Descending) : SortOrder.None;
        }

        void GridCellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            DataGridViewRow row = grid.Rows[e.RowIndex];
            if (!row.Selected)
            {
                grid.ClearSelection();
                row.Selected = true;
            }
            if (e.ColumnIndex >= 0) grid.CurrentCell = row.Cells[e.ColumnIndex];
            rowMenu.Show(Cursor.Position);
        }

        void GridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete && !grid.IsCurrentCellInEditMode)
            {
                DeleteSelected(false);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None)
            {
                EditSelected();
                e.Handled = true;
            }
        }

        // ---------------------------------------------------------- scanning

        async void ScanNow()
        {
            if (scanning) return;
            ResolveSubnet();
            if (subnet == null && data.Devices.Count == 0)
            {
                scanLabel.Text = "No network connection found";
                nextScan = DateTime.Now.AddMinutes(1);
                return;
            }
            scanning = true;
            scanButton.Enabled = false;
            scanButton.Text = "Scanning...";
            progressBar.Value = 0;
            progressBar.Visible = true;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                Progress<int> progress = new Progress<int>(p =>
                {
                    progressBar.Value = Math.Max(0, Math.Min(100, p));
                    scanLabel.Text = "Scanning " + (subnet != null ? subnet.ToString() : "") + "... " + progressBar.Value + "%";
                });
                List<string> knownIps = data.Devices.Select(d => d.Ip).ToList();
                ScanOptions opt = Options();
                List<ProbeResult> results = await Task.Run(() =>
                    Scanner.ScanAsync(opt, knownIps, progress, CancellationToken.None));
                Merge(results, DateTime.Now, true);
                S.LastScan = DateTime.Now;
                Store.Save(data);
            }
            catch (Exception ex)
            {
                scanLabel.Text = "Scan failed: " + ex.Message;
            }
            finally
            {
                lastScanTook = sw.Elapsed;
                scanning = false;
                scanButton.Enabled = true;
                scanButton.Text = "Scan now";
                progressBar.Visible = false;
                nextScan = DateTime.Now.AddMinutes(Math.Max(1, S.IntervalMinutes));
                RefreshGrid();
                Tick();
            }
        }

        async void CheckDevices(List<Device> devices)
        {
            if (devices.Count == 0) return;
            ScanOptions opt = Options();
            foreach (Device d in devices) d.LastChecked = null;
            scanLabel.Text = "Checking " + (devices.Count == 1 ? devices[0].Ip : devices.Count + " devices") + "...";
            try
            {
                List<Task<ProbeResult>> tasks = new List<Task<ProbeResult>>();
                foreach (Device d in devices)
                {
                    IPAddress ip = Net.ParseIPv4(d.Ip);
                    if (ip != null) tasks.Add(Task.Run(() => Scanner.ProbeOneAsync(ip, opt)));
                }
                ProbeResult[] results = await Task.WhenAll(tasks);
                Merge(results.ToList(), DateTime.Now, false);
                Store.Save(data);
            }
            catch (Exception ex)
            {
                scanLabel.Text = "Check failed: " + ex.Message;
            }
            RefreshGrid();
            Tick();
        }

        // Folds scan results into the device list. Fields you typed in are kept.
        void Merge(List<ProbeResult> results, DateTime now, bool fullScan)
        {
            Dictionary<string, ProbeResult> byIp = new Dictionary<string, ProbeResult>();
            foreach (ProbeResult r in results) byIp[r.Ip.ToString()] = r;
            HashSet<string> ignored = new HashSet<string>(S.Ignored, StringComparer.OrdinalIgnoreCase);

            foreach (ProbeResult r in results.Where(x => x.Up))
            {
                string ip = r.Ip.ToString();
                Device d = data.Devices.FirstOrDefault(x => x.Ip == ip);

                // Same device, new address (DHCP gave it a different IP).
                if (d == null && !string.IsNullOrEmpty(r.Mac) && !Net.IsRandomMac(r.Mac))
                {
                    Device moved = data.Devices.FirstOrDefault(x => string.Equals(x.Mac, r.Mac, StringComparison.OrdinalIgnoreCase));
                    if (moved != null)
                    {
                        ProbeResult old;
                        bool oldStillUp = byIp.TryGetValue(moved.Ip, out old) && old.Up;
                        if (!oldStillUp) { moved.Ip = ip; d = moved; }
                    }
                }

                if (d == null)
                {
                    if (ignored.Contains(ip) || (!string.IsNullOrEmpty(r.Mac) && ignored.Contains(r.Mac))) continue;
                    d = new Device();
                    d.Ip = ip;
                    d.FirstSeen = now;
                    d.IsNew = fullScan && data.Devices.Count > 0;
                    d.Source = "Scan";
                    data.Devices.Add(d);
                }

                d.SetStatus(DeviceStatus.Up, now);
                d.LastSeen = now;
                if (!d.FirstSeen.HasValue) d.FirstSeen = now;
                d.ResponseMs = r.Rtt;
                if (!d.IsLocked("Mac") && !string.IsNullOrEmpty(r.Mac)) d.Mac = r.Mac;
                if (!d.IsLocked("Hostname") && !string.IsNullOrEmpty(r.Hostname)) d.Hostname = r.Hostname;
                if (!d.IsLocked("Vendor"))
                {
                    string v = Vendors.Lookup(d.Mac);
                    if (v.Length > 0) d.Vendor = v;
                }
                if (r.OpenPorts != null) d.OpenPorts = string.Join(", ", r.OpenPorts);
                if (!d.IsLocked("Os") && r.Ttl > 0 && !r.IsThisPc) d.Os = Scanner.GuessOs(r.Ttl);
                if (r.IsThisPc && !d.IsLocked("Os")) d.Os = "Windows";
                if (!d.IsLocked("Type"))
                {
                    string t = Scanner.GuessType(r, d.Vendor, subnet);
                    if (t.Length > 0) d.Type = t;
                }
            }

            foreach (ProbeResult r in results.Where(x => !x.Up))
            {
                Device d = data.Devices.FirstOrDefault(x => x.Ip == r.Ip.ToString());
                if (d == null) continue;
                d.SetStatus(DeviceStatus.Down, now);
                d.ResponseMs = -1;
            }
        }

        void Tick()
        {
            if (scanning) return;
            if (DateTime.Now >= nextScan)
            {
                ScanNow();
                return;
            }
            string last = S.LastScan.HasValue
                ? "Last scan " + Columns.When(S.LastScan) + (lastScanTook.TotalSeconds > 0 ? " (" + Math.Max(1, (int)Math.Round(lastScanTook.TotalSeconds)) + " s)" : "")
                : "Not scanned yet";
            string next = "";
            if (nextScan != DateTime.MaxValue)
            {
                TimeSpan left = nextScan - DateTime.Now;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                next = "  ·  Next scan in " + (left.TotalHours >= 1
                    ? ((int)left.TotalHours) + ":" + left.Minutes.ToString("00") + ":" + left.Seconds.ToString("00")
                    : ((int)left.TotalMinutes) + ":" + left.Seconds.ToString("00"));
            }
            scanLabel.Text = last + next;
            tray.Text = Truncate(AppInfo.Name + " - " + CountText(), 63);
        }

        static string Truncate(string s, int n) { return s.Length <= n ? s : s.Substring(0, n); }

        string CountText()
        {
            int up = data.Devices.Count(d => d.Status == DeviceStatus.Up);
            int down = data.Devices.Count(d => d.Status == DeviceStatus.Down);
            return up + " up, " + down + " down";
        }

        void UpdateStatusBar()
        {
            int up = data.Devices.Count(d => d.Status == DeviceStatus.Up);
            int down = data.Devices.Count(d => d.Status == DeviceStatus.Down);
            int unknown = data.Devices.Count(d => d.Status == DeviceStatus.Unknown);
            int fresh = data.Devices.Count(d => d.IsNew);
            upLabel.Text = up + " up";
            downLabel.Text = down + " down";
            unknownLabel.Text = unknown + " not checked";
            unknownLabel.Visible = unknown > 0;
            newLabel.Text = fresh + " new";
            newLabel.Visible = fresh > 0;
            newLabel.ToolTipText = "Highlighted in blue. Edit them or use Devices > Mark all as seen.";
            string shown = grid.Rows.Count != data.Devices.Count ? "  ·  showing " + grid.Rows.Count + " of " + data.Devices.Count : "";
            newLabel.Text += shown;
            if (!newLabel.Visible && shown.Length > 0) { newLabel.Text = shown.Substring(5); newLabel.Visible = true; }
        }

        // ----------------------------------------------------------- actions

        void AddDevice()
        {
            Device d = new Device();
            d.Source = "Manual";
            using (DeviceForm f = new DeviceForm(d, data.Devices, true))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
            }
            data.Devices.Add(d);
            S.Ignored.RemoveAll(x => string.Equals(x, d.Ip, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x, d.Mac, StringComparison.OrdinalIgnoreCase));
            Store.Save(data);
            RefreshGrid();
            SelectDevice(d);
            CheckDevices(new List<Device> { d });
        }

        void EditSelected()
        {
            List<Device> sel = Selected();
            if (sel.Count == 0) return;
            Device d = sel[0];
            string oldIp = d.Ip;
            using (DeviceForm f = new DeviceForm(d, data.Devices, false))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
            }
            d.IsNew = false;
            Store.Save(data);
            RefreshGrid();
            SelectDevice(d);
            if (d.Ip != oldIp) CheckDevices(new List<Device> { d });
        }

        void SelectDevice(Device d)
        {
            grid.ClearSelection();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.Tag != d) continue;
                row.Selected = true;
                DataGridViewColumn first = grid.Columns.Cast<DataGridViewColumn>()
                    .Where(c => c.Visible).OrderBy(c => c.DisplayIndex).FirstOrDefault();
                if (first != null) grid.CurrentCell = row.Cells[first.Index];
                break;
            }
        }

        void DeleteSelected(bool ignore)
        {
            List<Device> sel = Selected();
            if (sel.Count == 0) return;
            string what = sel.Count == 1
                ? (sel[0].Name.Length > 0 ? sel[0].Name + " (" + sel[0].Ip + ")" : sel[0].Ip)
                : sel.Count + " devices";
            string msg = ignore
                ? "Delete " + what + " and ignore it in future scans?\n\nYou can stop ignoring devices in Settings."
                : "Delete " + what + "?\n\nDevices that are still on the network will be added again (as new) by the next scan. Use Delete and ignore to keep them off the list.";
            if (MessageBox.Show(this, msg, AppInfo.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            foreach (Device d in sel)
            {
                data.Devices.Remove(d);
                if (ignore)
                {
                    string key = !string.IsNullOrEmpty(d.Mac) && !Net.IsRandomMac(d.Mac) ? d.Mac : d.Ip;
                    if (!S.Ignored.Contains(key)) S.Ignored.Add(key);
                }
            }
            Store.Save(data);
            RefreshGrid();
        }

        void CheckSelected() { CheckDevices(Selected()); }

        void MarkSelectedSeen()
        {
            foreach (Device d in Selected()) d.IsNew = false;
            Store.Save(data);
            RefreshGrid();
        }

        void MarkAllSeen()
        {
            foreach (Device d in data.Devices) d.IsNew = false;
            Store.Save(data);
            RefreshGrid();
        }

        void RunForSelected(string exe, string argFormat)
        {
            foreach (Device d in Selected().Take(5))
            {
                try { Process.Start(exe, string.Format(argFormat, d.Ip)); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, AppInfo.Name); }
            }
        }

        void OpenForSelected(string format)
        {
            foreach (Device d in Selected().Take(5))
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(string.Format(format, d.Ip));
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, AppInfo.Name); }
            }
        }

        void CopyField(string key)
        {
            string text = string.Join(Environment.NewLine, Selected().Select(d => Columns.Get(d, key)).Where(s => s.Length > 0));
            if (text.Length > 0) Clipboard.SetText(text);
        }

        List<DataGridViewColumn> VisibleColumns()
        {
            return grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex).ToList();
        }

        void CopyRows()
        {
            List<DataGridViewColumn> cols = VisibleColumns();
            StringBuilder sb = new StringBuilder();
            foreach (Device d in Selected())
                sb.AppendLine(string.Join("\t", cols.Select(c => Columns.Get(d, c.Name))));
            if (sb.Length > 0) Clipboard.SetText(sb.ToString());
        }

        void ExportCsv()
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "CSV file (*.csv)|*.csv";
                dlg.FileName = "LAN devices " + DateTime.Now.ToString("yyyy-MM-dd") + ".csv";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                List<DataGridViewColumn> cols = VisibleColumns();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(string.Join(",", cols.Select(c => Csv(c.HeaderText))));
                foreach (DataGridViewRow row in grid.Rows)
                {
                    Device d = (Device)row.Tag;
                    sb.AppendLine(string.Join(",", cols.Select(c => Csv(Columns.Get(d, c.Name)))));
                }
                try
                {
                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                    scanLabel.Text = "Saved " + Path.GetFileName(dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Couldn't save the file.\n\n" + ex.Message, AppInfo.Name,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        static string Csv(string s)
        {
            s = s ?? "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        void OpenDataFolder()
        {
            try { Process.Start("explorer.exe", "/select,\"" + Store.FilePath + "\""); }
            catch (Exception) { }
        }

        void ShowSettings()
        {
            using (SettingsForm f = new SettingsForm(S))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
            }
            Store.Save(data);
            ResolveSubnet();
            if (!scanning) nextScan = (S.LastScan ?? DateTime.Now).AddMinutes(Math.Max(1, S.IntervalMinutes));
            if (nextScan < DateTime.Now) nextScan = DateTime.Now.AddSeconds(2);
            Tick();
        }

        void ShowAbout()
        {
            if (about == null || about.IsDisposed) about = new AboutForm();
            about.Show();
            about.Activate();
        }

        // ------------------------------------------------- window and tray

        void RestoreWindow()
        {
            if (S.WindowX == int.MinValue || S.WindowW < 300 || S.WindowH < 200) return;
            Rectangle r = new Rectangle(S.WindowX, S.WindowY, S.WindowW, S.WindowH);
            if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) return;
            StartPosition = FormStartPosition.Manual;
            Bounds = r;
            if (S.WindowMaximised) WindowState = FormWindowState.Maximized;
        }

        void RememberWindow()
        {
            Rectangle r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            S.WindowX = r.X; S.WindowY = r.Y; S.WindowW = r.Width; S.WindowH = r.Height;
            S.WindowMaximised = WindowState == FormWindowState.Maximized;
            SaveColumnLayout();
        }

        void RestoreFromTray()
        {
            Show();
            ShowInTaskbar = true;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            tray.Visible = false;
        }

        void ExitApp()
        {
            reallyExit = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            if (!reallyExit && S.CloseToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                RememberWindow();
                Store.Save(data);
                Hide();
                tray.Visible = true;
                return;
            }
            RememberWindow();
            Store.Save(data);
            tray.Visible = false;
            clock.Stop();
            if (about != null && !about.IsDisposed) about.Close();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tray.Dispose();
            base.Dispose(disposing);
        }
    }

    // ---------------------------------------------------- add / edit device

    class DeviceForm : Form
    {
        static readonly string[] CommonTypes = {
            "Router", "Switch", "Access point", "Network", "Server", "Proxmox host", "Virtual machine",
            "Docker host", "NAS", "Desktop PC", "Windows PC", "Laptop", "Linux", "Raspberry Pi", "Phone",
            "Tablet", "Phone / laptop", "Apple device", "Printer", "Camera", "TV / media", "Speaker",
            "Games console", "Smart plug", "IoT", "DNS server", "Home Assistant", "Other" };

        static readonly string[] FieldKeys = { "Ip", "Name", "Hostname", "Type", "Mac", "Vendor", "Os",
            "Model", "Serial", "Location", "Owner", "Notes" };

        readonly Device device;
        readonly List<Device> all;
        readonly bool adding;
        readonly Dictionary<string, Control> boxes = new Dictionary<string, Control>();
        readonly ErrorProvider errors = new ErrorProvider();

        public DeviceForm(Device d, List<Device> devices, bool isNew)
        {
            device = d;
            all = devices;
            adding = isNew;
            Text = isNew ? "Add device" : "Edit device - " + d.Ip;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);
            errors.BlinkStyle = ErrorBlinkStyle.NeverBlink;

            TableLayoutPanel t = new TableLayoutPanel();
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.ColumnCount = 2;
            t.Location = new Point(Padding.Left, Padding.Top);
            t.Padding = new Padding(0, 0, 18, 0);
            Controls.Add(t);

            HashSet<string> types = new HashSet<string>(CommonTypes);
            foreach (Device x in devices) if (!string.IsNullOrEmpty(x.Type)) types.Add(x.Type);

            foreach (string key in FieldKeys)
            {
                ColumnDef c = Columns.Find(key);
                Label l = new Label();
                l.Text = c.Header + ":";
                l.AutoSize = true;
                l.Anchor = AnchorStyles.Left;
                l.Margin = new Padding(0, 6, 10, 0);

                Control box;
                if (key == "Type")
                {
                    ComboBox cb = new ComboBox();
                    cb.DropDownStyle = ComboBoxStyle.DropDown;
                    cb.Items.AddRange(types.OrderBy(x => x).Cast<object>().ToArray());
                    cb.Text = d.Type;
                    box = cb;
                }
                else
                {
                    TextBox tb = new TextBox();
                    tb.Text = Columns.Get(d, key);
                    if (key == "Notes")
                    {
                        tb.Multiline = true;
                        tb.Height = (int)(Font.GetHeight() * 4.5);
                        tb.ScrollBars = ScrollBars.Vertical;
                        tb.AcceptsReturn = true;
                        l.Anchor = AnchorStyles.Left | AnchorStyles.Top;
                    }
                    if (key == "Ip" || key == "Mac") tb.Font = new Font("Consolas", Font.SizeInPoints);
                    box = tb;
                }
                box.Width = (int)(Font.GetHeight() * 22);
                box.Margin = new Padding(0, 3, 0, 3);
                boxes[key] = box;
                int row = t.RowCount++;
                t.Controls.Add(l, 0, row);
                t.Controls.Add(box, 1, row);
            }

            boxes["Mac"].Leave += delegate
            {
                string mac = Net.NormaliseMac(boxes["Mac"].Text);
                if (mac == null) return;
                boxes["Mac"].Text = mac;
                if (boxes["Vendor"].Text.Trim().Length == 0) boxes["Vendor"].Text = Vendors.Lookup(mac);
            };

            if (!isNew)
            {
                Label info = new Label();
                info.AutoSize = true;
                info.ForeColor = SystemColors.GrayText;
                info.Margin = new Padding(0, 10, 0, 0);
                List<string> parts = new List<string>();
                parts.Add("Status: " + Columns.Get(d, "Status")
                    + (d.ResponseMs >= 0 ? " (" + Columns.Get(d, "ResponseMs") + ")" : ""));
                if (d.FirstSeen.HasValue) parts.Add("First seen: " + d.FirstSeen.Value.ToString("g"));
                if (d.LastSeen.HasValue) parts.Add("Last seen: " + d.LastSeen.Value.ToString("g"));
                if (d.OpenPorts.Length > 0) parts.Add("Open ports: " + d.OpenPorts);
                parts.Add("Fields you change here are kept; scans won't overwrite them.");
                info.Text = string.Join("\n", parts);
                int row = t.RowCount++;
                t.Controls.Add(info, 1, row);
            }

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 12, 0, 0);
            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.AutoSize = true;
            Button ok = new Button();
            ok.Text = isNew ? "Add" : "OK";
            ok.AutoSize = true;
            ok.Click += delegate { Save(); };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            int brow = t.RowCount++;
            t.Controls.Add(buttons, 0, brow);
            t.SetColumnSpan(buttons, 2);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Control first = adding ? boxes["Ip"] : boxes["Name"];
            first.Focus();
        }

        void Save()
        {
            errors.Clear();
            IPAddress ip = Net.ParseIPv4(boxes["Ip"].Text);
            if (ip == null)
            {
                Fail("Ip", "Enter an IPv4 address, for example 192.168.1.50.");
                return;
            }
            string ipText = ip.ToString();
            if (all.Any(x => x != device && x.Ip == ipText))
            {
                Fail("Ip", ipText + " is already in the list.");
                return;
            }
            string macText = boxes["Mac"].Text.Trim();
            string mac = "";
            if (macText.Length > 0)
            {
                mac = Net.NormaliseMac(macText);
                if (mac == null)
                {
                    Fail("Mac", "Use the form AA:BB:CC:DD:EE:FF, or leave it empty.");
                    return;
                }
            }

            device.Ip = ipText;
            foreach (string key in FieldKeys)
            {
                if (key == "Ip") continue;
                string v = key == "Mac" ? mac : boxes[key].Text.Trim();
                if (key == "Notes") v = boxes[key].Text.TrimEnd();
                if (v != Columns.Get(device, key))
                {
                    Columns.Set(device, key, v);
                    device.Lock(key);
                }
            }
            if (device.Vendor.Length == 0 && device.Mac.Length > 0) device.Vendor = Vendors.Lookup(device.Mac);
            DialogResult = DialogResult.OK;
            Close();
        }

        void Fail(string key, string message)
        {
            errors.SetError(boxes[key], message);
            boxes[key].Focus();
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // --------------------------------------------------------------- settings

    class SettingsForm : Form
    {
        readonly AppSettings s;
        readonly TextBox subnetBox = new TextBox();
        readonly NumericUpDown interval = new NumericUpDown();
        readonly NumericUpDown timeout = new NumericUpDown();
        readonly CheckBox ports = new CheckBox();
        readonly CheckBox scanOnStart = new CheckBox();
        readonly CheckBox closeToTray = new CheckBox();
        readonly Button forget = new Button();
        bool clearIgnored;

        public SettingsForm(AppSettings settings)
        {
            s = settings;
            Text = "Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            TableLayoutPanel t = new TableLayoutPanel();
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.ColumnCount = 2;
            t.Location = new Point(Padding.Left, Padding.Top);
            t.Padding = new Padding(0, 0, 12, 0);
            Controls.Add(t);

            Subnet detected = Net.Detect();
            subnetBox.Text = s.Subnet;
            subnetBox.Width = (int)(Font.GetHeight() * 14);
            subnetBox.Font = new Font("Consolas", Font.SizeInPoints);
            Label hint = Hint(detected != null
                ? "Leave empty to use this PC's network (" + detected + ")"
                : "Leave empty to detect it automatically");
            AddRow(t, "Subnet to scan:", subnetBox);
            AddRow(t, "", hint);

            interval.Minimum = 1;
            interval.Maximum = 1440;
            interval.Value = Math.Max(1, Math.Min(1440, s.IntervalMinutes));
            interval.Width = (int)(Font.GetHeight() * 5);
            AddRow(t, "Scan every (minutes):", interval);

            timeout.Minimum = 200;
            timeout.Maximum = 5000;
            timeout.Increment = 100;
            timeout.Value = Math.Max(200, Math.Min(5000, s.PingTimeoutMs));
            timeout.Width = (int)(Font.GetHeight() * 5);
            AddRow(t, "Ping timeout (ms):", timeout);

            ports.Text = "Check common ports (fills in Open ports and helps guess the type)";
            ports.AutoSize = true;
            ports.Checked = s.CheckPorts;
            AddRow(t, "", ports);

            scanOnStart.Text = "Scan when the program starts";
            scanOnStart.AutoSize = true;
            scanOnStart.Checked = s.ScanOnStart;
            AddRow(t, "", scanOnStart);

            closeToTray.Text = "Keep scanning in the system tray when the window is closed";
            closeToTray.AutoSize = true;
            closeToTray.Checked = s.CloseToTray;
            AddRow(t, "", closeToTray);

            forget.AutoSize = true;
            forget.Text = "Stop ignoring deleted devices (" + s.Ignored.Count + ")";
            forget.Enabled = s.Ignored.Count > 0;
            forget.Click += delegate
            {
                clearIgnored = true;
                forget.Text = "Stop ignoring deleted devices (0)";
                forget.Enabled = false;
            };
            AddRow(t, "", forget);

            AddRow(t, "", Hint("Your device list is saved in:\n" + Store.FilePath));

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 12, 0, 0);
            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.AutoSize = true;
            Button ok = new Button();
            ok.Text = "OK";
            ok.AutoSize = true;
            ok.Click += delegate { Save(); };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            int row = t.RowCount++;
            t.Controls.Add(buttons, 0, row);
            t.SetColumnSpan(buttons, 2);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        Label Hint(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = SystemColors.GrayText;
            return l;
        }

        static void AddRow(TableLayoutPanel t, string caption, Control c)
        {
            int row = t.RowCount++;
            Label l = new Label();
            l.Text = caption;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            l.Margin = new Padding(0, 6, 10, 0);
            c.Margin = new Padding(0, 3, 0, 3);
            c.Anchor = AnchorStyles.Left;
            t.Controls.Add(l, 0, row);
            t.Controls.Add(c, 1, row);
        }

        void Save()
        {
            string text = subnetBox.Text.Trim();
            if (text.Length > 0)
            {
                string err;
                Subnet sn = Net.Parse(text, out err);
                if (sn == null)
                {
                    MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    subnetBox.Focus();
                    return;
                }
                text = sn.ToString();
            }
            s.Subnet = text;
            s.IntervalMinutes = (int)interval.Value;
            s.PingTimeoutMs = (int)timeout.Value;
            s.CheckPorts = ports.Checked;
            s.ScanOnStart = scanOnStart.Checked;
            s.CloseToTray = closeToTray.Checked;
            if (clearIgnored) s.Ignored.Clear();
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // ------------------------------------------------------------------ about

    class AboutForm : Form
    {
        public AboutForm()
        {
            Text = "About " + AppInfo.Name;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14, 12, 14, 12);
            Icon = AppIcon.Load(new Size(32, 32));

            TableLayoutPanel main = new TableLayoutPanel();
            main.AutoSize = true;
            main.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            main.ColumnCount = 3;
            main.RowCount = 3;
            main.Location = new Point(Padding.Left, Padding.Top);
            Controls.Add(main);

            PictureBox pic = new PictureBox();
            int iconPx = LogicalToDevice(48);
            using (Icon big = AppIcon.Load(new Size(iconPx, iconPx))) pic.Image = big.ToBitmap();
            pic.SizeMode = PictureBoxSizeMode.AutoSize;
            pic.Margin = new Padding(0, 2, 14, 0);
            main.Controls.Add(pic, 0, 0);
            main.SetRowSpan(pic, 2);

            Label title = new Label();
            title.Text = AppInfo.Name + "   " + AppInfo.Version;
            title.Font = new Font(Font.FontFamily, Font.SizeInPoints * 1.35f, FontStyle.Bold);
            title.AutoSize = true;
            title.Margin = new Padding(0, 2, 24, 2);
            main.Controls.Add(title, 1, 0);

            Label free = new Label();
            free.Text = "This program is free and open source (MIT licence)";
            free.AutoSize = true;
            free.Margin = new Padding(0, 2, 24, 2);
            main.Controls.Add(free, 1, 1);

            Button ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.AutoSize = true;
            ok.MinimumSize = new Size(LogicalToDevice(80), 0);
            ok.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            ok.Click += delegate { Close(); };
            main.Controls.Add(ok, 2, 0);
            main.SetRowSpan(ok, 2);
            AcceptButton = ok;
            CancelButton = ok;

            TableLayoutPanel info = new TableLayoutPanel();
            info.AutoSize = true;
            info.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            info.ColumnCount = 2;
            info.Margin = new Padding(0, 16, 0, 0);
            main.Controls.Add(info, 0, 2);
            main.SetColumnSpan(info, 3);

            AddRow(info, "Written by", AppInfo.Author, null);
            if (AppInfo.Website.Length > 0)
            {
                string latest = AppInfo.Website.TrimEnd('/') + "/releases/latest";
                AddRow(info, "Check for the latest version at:", latest, latest);
                AddRow(info, "Source code and help:", AppInfo.Website, AppInfo.Website);
            }
            if (AppInfo.Donate.Length > 0)
                AddRow(info, "If it saves you time, support it at:", AppInfo.Donate, AppInfo.Donate);
        }

        int LogicalToDevice(int px)
        {
            using (Graphics g = CreateGraphics()) return (int)Math.Round(px * g.DpiX / 96f);
        }

        static void AddRow(TableLayoutPanel t, string caption, string text, string url)
        {
            int row = t.RowCount;
            t.RowCount = row + 1;

            Label cap = new Label();
            cap.Text = caption;
            cap.AutoSize = true;
            cap.Anchor = AnchorStyles.Right;
            cap.Margin = new Padding(0, 3, 8, 3);
            t.Controls.Add(cap, 0, row);

            Control value;
            if (url == null)
            {
                Label l = new Label();
                l.Text = text;
                value = l;
            }
            else
            {
                LinkLabel link = new LinkLabel();
                link.Text = text;
                link.LinkClicked += delegate
                {
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(url);
                        psi.UseShellExecute = true;
                        Process.Start(psi);
                    }
                    catch (Exception) { }
                };
                value = link;
            }
            value.AutoSize = true;
            value.Anchor = AnchorStyles.Left;
            value.Margin = new Padding(0, 3, 0, 3);
            t.Controls.Add(value, 1, row);
        }
    }

    // ---------------------------------------------------------------- program

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool first;
            Mutex single = new Mutex(true, "CtrlItLan_SingleInstance_3e9a", out first);
            if (!first)
            {
                MessageBox.Show(AppInfo.Name + " is already running. Look for it on the taskbar or in the system tray.",
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Native.EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, AppInfo.Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            int w, io;
            ThreadPool.GetMinThreads(out w, out io);
            ThreadPool.SetMinThreads(Math.Max(w, 64), Math.Max(io, 64));

            DataFile data = Store.Load();
            Application.Run(new MainForm(data));
            GC.KeepAlive(single);
        }
    }
}
