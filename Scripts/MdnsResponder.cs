using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace RemoteApiAccess
{
    /// <summary>
    /// mDNS/DNS-SD responder advertising Timberborn's HTTP API as
    ///   _timberborn._tcp.local.
    ///
    /// Implements:
    ///   RFC 6762 – Multicast DNS
    ///   RFC 6763 – DNS-Based Service Discovery
    ///
    /// Features:
    ///   • Dual-stack: IPv4 (224.0.0.251) + IPv6 (ff02::fb) sockets
    ///   • _services._dns-sd._udp.local. enumeration (so avahi-browse -a works)
    ///   • RFC 6762 §8 probing before first announcement
    ///   • Proper DNS message structure: answers vs additional records
    ///   • IP TTL / hop-limit 255 (RFC 6762 §11.3)
    ///   • MulticastLoopback enabled (same-host discovery)
    ///   • Goodbye packets (TTL 0) on shutdown
    ///   • A + AAAA records for all non-loopback, non-virtual interfaces
    ///   • Interface preference: physical ethernet/wifi over virtual bridges
    /// </summary>
    internal sealed class MdnsResponder : IDisposable
    {
        // ── mDNS multicast addresses ──────────────────────────────────────
        private static readonly IPAddress  Mdns4Group = IPAddress.Parse("224.0.0.251");
        private static readonly IPAddress  Mdns6Group = IPAddress.Parse("ff02::fb");
        private const           int        MdnsPort   = 5353;

        // ── DNS-SD names ──────────────────────────────────────────────────
        private const string DnsSdEnumeration = "_services._dns-sd._udp.local.";
        private const string ServiceType      = "_timberborn._tcp.local.";

        // ── DNS record types / classes ────────────────────────────────────
        private const ushort TypeA    = 1;
        private const ushort TypePTR  = 12;
        private const ushort TypeTXT  = 16;
        private const ushort TypeAAAA = 28;
        private const ushort TypeSRV  = 33;
        private const ushort ClassIN  = 1;
        private const ushort CacheFl  = 0x8000; // cache-flush bit (unique records)

        private const uint TtlEnum   = 4500; // 75 min – _services._dns-sd PTR
        private const uint TtlShared = 4500; // 75 min – service PTR / TXT
        private const uint TtlUnique = 4500; // 75 min – SRV / A / AAAA

        // ── RFC 6762 §8 probing parameters ───────────────────────────────
        private const int ProbeCount      = 3;
        private const int ProbeIntervalMs = 250;
        private const int ProbeSettleMs   = 250;

        // ── Instance identity ─────────────────────────────────────────────
        private readonly int    _port;
        private readonly string _instanceName; // e.g. "Timberborn on myhostname"
        private readonly string _fqInstance;   // _instanceName + "." + ServiceType
        private readonly string _hostname;     // "myhostname.local."
        private readonly string _version;      // e.g. "0.7.5.0"

        // Resolved once at Start() so all records agree on the same IP.
        private string _bestIp;               // e.g. "192.168.1.42"
        private string _baseUrl;              // e.g. "http://192.168.1.42:8080/"

        // ── Sockets + threads ─────────────────────────────────────────────
        private Socket        _sock4;
        private Socket        _sock6;
        private Thread        _thread4;
        private Thread        _thread6;
        private Thread        _threadProbe;
        private volatile bool _running;

        // ── Construction ──────────────────────────────────────────────────

        /// <param name="port">HTTP API port.</param>
        /// <param name="version">Game version string (e.g. "0.7.5.0").</param>
        /// <param name="sanitisedHost">Hostname with spaces replaced by hyphens.</param>
        internal MdnsResponder(int port, string version, string sanitisedHost)
        {
            _port         = port;
            _version      = version ?? "";
            _instanceName = $"Timberborn on {sanitisedHost}";
            _fqInstance   = $"{_instanceName}.{ServiceType}";
            _hostname     = $"{sanitisedHost}.local.";
        }

        // ── Lifecycle ─────────────────────────────────────────────────────

        internal void Start()
        {
            if (_running) return;

            // Resolve the best IP now so all records and logs are consistent.
            _bestIp  = GetBestLanIp() ?? "127.0.0.1";
            _baseUrl = $"http://{_bestIp}:{_port}/";
            Debug.Log($"[Remote Api Access] mDNS using IP {_bestIp} for advertisements");

            try
            {
                _sock4 = BuildSocket4();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Remote Api Access] mDNS IPv4 socket failed: {ex.Message}");
                return;
            }

            try   { _sock6 = BuildSocket6(); }
            catch { _sock6 = null; }

            _running = true;

            _thread4 = MakeThread("mDNS-v4", () => ReceiveLoop(_sock4));
            _thread4.Start();

            if (_sock6 != null)
            {
                _thread6 = MakeThread("mDNS-v6", () => ReceiveLoop(_sock6));
                _thread6.Start();
            }

            _threadProbe = MakeThread("mDNS-probe", ProbeAndAnnounce);
            _threadProbe.Start();

            Debug.Log(
                $"[Remote Api Access] mDNS responder starting – " +
                $"advertising \"{_instanceName}\" at {_baseUrl}" +
                (_sock6 != null ? " (IPv4 + IPv6)" : " (IPv4 only)"));
        }

        internal void Stop()
        {
            if (!_running) return;
            _running = false;

            try { SendGoodbye(); } catch { }

            try { _sock4?.Close(); } catch { }
            try { _sock6?.Close(); } catch { }

            _thread4?.Join(1000);
            _thread6?.Join(1000);
            _threadProbe?.Join(2000);

            Debug.Log("[Remote Api Access] mDNS responder stopped.");
        }

        public void Dispose() => Stop();

        // ── Interface selection ───────────────────────────────────────────

        /// <summary>
        /// Returns a score for an interface: higher = more preferred.
        /// Physical ethernet and wifi beat everything else; virtual/tunnel
        /// interfaces (virbr*, docker*, veth*, tun*, tap*) are deprioritised.
        /// Works on Linux, Windows and macOS — the virtual-name patterns only
        /// match on Linux; on Windows/macOS those names won't appear.
        /// </summary>
        private static int InterfaceScore(NetworkInterface nic)
        {
            // Hard-reject by type first
            switch (nic.NetworkInterfaceType)
            {
                case NetworkInterfaceType.Loopback:
                case NetworkInterfaceType.Tunnel:
                    return -1;
            }

            // On Linux, virtual bridge / container / VPN adapters have
            // recognisable name prefixes. On Windows/macOS these won't match.
            string name = nic.Name.ToLowerInvariant();
            if (name.StartsWith("virbr")  ||   // libvirt bridge
                name.StartsWith("docker") ||   // Docker bridge
                name.StartsWith("veth")   ||   // veth pairs (containers)
                name.StartsWith("tun")    ||   // OpenVPN / WireGuard tun
                name.StartsWith("tap")    ||   // tap devices
                name.StartsWith("br-")    ||   // Docker user-defined bridges
                name.StartsWith("vbox")   ||   // VirtualBox
                name.StartsWith("vmnet"))      // VMware
            {
                return 0;
            }

            // Prefer physical types
            switch (nic.NetworkInterfaceType)
            {
                case NetworkInterfaceType.Ethernet:       return 3;
                case NetworkInterfaceType.Wireless80211:  return 3;
                case NetworkInterfaceType.GigabitEthernet: return 3;
                case NetworkInterfaceType.FastEthernetT:  return 2;
                case NetworkInterfaceType.FastEthernetFx: return 2;
                default:                                  return 1;
            }
        }

        /// <summary>
        /// Returns all non-virtual IPv4/IPv6 addresses, sorted so that
        /// physical interfaces come first.
        /// </summary>
        private static IEnumerable<IPAddress> GetAddresses(AddressFamily family)
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Select(n => (score: InterfaceScore(n), nic: n))
                .Where(t => t.score > 0)
                .OrderByDescending(t => t.score)
                .SelectMany(t => t.nic.GetIPProperties().UnicastAddresses
                    .Where(u => u.Address.AddressFamily == family)
                    .Select(u => u.Address));
        }

        /// <summary>
        /// Picks the single best LAN IP to advertise in the UI and TXT record.
        /// Prefers physical interfaces; falls back to the routing-table trick.
        /// </summary>
        private static string GetBestLanIp()
        {
            // Try ranked physical interfaces first
            IPAddress best = GetAddresses(AddressFamily.InterNetwork).FirstOrDefault();
            if (best != null) return best.ToString();

            // Fallback: ask the OS which source address it would use for outbound
            // traffic. Doesn't send any data but forces a route lookup.
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork,
                                         SocketType.Dgram, ProtocolType.Udp);
                s.Connect("8.8.8.8", 53);
                return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
            }
            catch
            {
                return null;
            }
        }

        // ── Socket construction ───────────────────────────────────────────

        private static Socket BuildSocket4()
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

            foreach (var ip in GetAddresses(AddressFamily.InterNetwork))
            {
                try
                {
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(Mdns4Group, ip));
                }
                catch { }
            }

            s.Blocking = false;
            return s;
        }

        private static Socket BuildSocket6()
        {
            var s = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
            s.Bind(new IPEndPoint(IPAddress.IPv6Any, MdnsPort));
            s.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 255);
            s.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, true);

            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (InterfaceScore(iface) <= 0) continue;
                if (iface.OperationalStatus != OperationalStatus.Up) continue;

                var ipv6Props = iface.GetIPProperties().GetIPv6Properties();
                if (ipv6Props == null) continue;

                try
                {
                    s.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
                        new IPv6MulticastOption(Mdns6Group, ipv6Props.Index));
                }
                catch { }
            }

            s.Blocking = false;
            return s;
        }

        // ── Probing + first announcement (RFC 6762 §8) ────────────────────

        private void ProbeAndAnnounce()
        {
            for (int i = 0; i < ProbeCount && _running; i++)
            {
                SendProbe();
                Thread.Sleep(ProbeIntervalMs);
            }

            if (!_running) return;
            Thread.Sleep(ProbeSettleMs);
            if (!_running) return;

            Announce(TtlShared);
            Debug.Log($"[Remote Api Access] mDNS announced \"{_instanceName}\"");
        }

        // ── Receive loops ─────────────────────────────────────────────────

        private void ReceiveLoop(Socket sock)
        {
            var buf      = new byte[4096];
            var remoteEp = sock.AddressFamily == AddressFamily.InterNetwork
                ? (EndPoint)new IPEndPoint(IPAddress.Any, 0)
                : (EndPoint)new IPEndPoint(IPAddress.IPv6Any, 0);

            while (_running)
            {
                try
                {
                    if (sock.Available == 0) { Thread.Sleep(10); continue; }

                    int len = sock.ReceiveFrom(buf, ref remoteEp);
                    if (len > 0) HandlePacket(buf, len, sock);
                }
                catch (SocketException ex) when (
                    ex.SocketErrorCode == SocketError.WouldBlock ||
                    ex.SocketErrorCode == SocketError.TryAgain)
                {
                    Thread.Sleep(10);
                }
                catch (SocketException ex) when (
                    ex.SocketErrorCode == SocketError.Interrupted ||
                    ex.SocketErrorCode == SocketError.Shutdown    ||
                    ex.SocketErrorCode == SocketError.NotSocket)
                {
                    break;
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (_running)
                        Debug.LogWarning($"[Remote Api Access] mDNS recv error: {ex.Message}");
                }
            }
        }

        // ── Query handling ────────────────────────────────────────────────

        private void HandlePacket(byte[] data, int len, Socket incomingSock)
        {
            if (len < 12) return;
            bool isQuery = (data[2] & 0x80) == 0;
            if (!isQuery) return;

            ushort qdCount = (ushort)((data[4] << 8) | data[5]);
            int offset = 12;

            for (int i = 0; i < qdCount && offset < len; i++)
            {
                string name = ReadName(data, len, ref offset);
                if (offset + 4 > len) break;

                ushort qtype = (ushort)((data[offset] << 8) | data[offset + 1]);
                offset += 4;

                bool isPtrOrAny = qtype == TypePTR || qtype == 255;
                if (!isPtrOrAny) continue;

                if (string.Equals(name, DnsSdEnumeration, StringComparison.OrdinalIgnoreCase))
                {
                    SendEnumerationResponse(incomingSock);
                    return;
                }

                if (string.Equals(name, ServiceType, StringComparison.OrdinalIgnoreCase))
                {
                    SendServiceResponse(incomingSock, TtlShared);
                    return;
                }
            }
        }

        // ── Response senders ──────────────────────────────────────────────

        private void SendEnumerationResponse(Socket sock)
        {
            var answers = new List<byte[]>
            {
                BuildRecord(EncodeName(DnsSdEnumeration), TypePTR, ClassIN,
                    TtlEnum, EncodeName(ServiceType))
            };
            SendMessage(sock, BuildMessage(answers, additionalRecords: null));
        }

        private void SendServiceResponse(Socket sock, uint ttl)
        {
            SendMessage(sock, BuildFullServicePacket(ttl));
        }

        private void Announce(uint ttl)
        {
            byte[] pkt = BuildFullServicePacket(ttl);
            SendMessage(_sock4, pkt);
            if (_sock6 != null) SendMessage(_sock6, pkt);
        }

        private void SendGoodbye()
        {
            byte[] pkt = BuildFullServicePacket(ttl: 0);
            try { SendMessage(_sock4, pkt, force: true); } catch { }
            if (_sock6 != null) { try { SendMessage(_sock6, pkt, force: true); } catch { } }
        }

        private void SendMessage(Socket sock, byte[] packet, bool force = false)
        {
            if (sock == null) return;
            if (!force && !_running) return;
            EndPoint dest = sock.AddressFamily == AddressFamily.InterNetwork
                ? (EndPoint)new IPEndPoint(Mdns4Group, MdnsPort)
                : (EndPoint)new IPEndPoint(Mdns6Group, MdnsPort);
            sock.SendTo(packet, dest);
        }

        // ── Probe packet ──────────────────────────────────────────────────

        private void SendProbe()
        {
            var q = new List<byte>();
            q.AddRange(EncodeName(_fqInstance));
            q.AddRange(new byte[] { 0x00, 0xFF }); // QTYPE = ANY
            q.AddRange(new byte[] { 0x00, 0x01 }); // QCLASS = IN

            var authority = new List<byte[]>
            {
                BuildRecord(EncodeName(_fqInstance), TypeSRV,
                    (ushort)(ClassIN | CacheFl), 0,
                    BuildSrvRdata(_hostname, (ushort)_port))
            };

            byte[] pkt = BuildMessage(answers: null,
                authority: authority, additional: null, isQuery: true, qdCount: 1,
                questionBytes: new List<byte[]> { q.ToArray() });

            try { _sock4?.SendTo(pkt, new IPEndPoint(Mdns4Group, MdnsPort)); } catch { }
            if (_sock6 != null)
            {
                try { _sock6.SendTo(pkt, new IPEndPoint(Mdns6Group, MdnsPort)); } catch { }
            }
        }

        // ── Packet builders ───────────────────────────────────────────────

        private byte[] BuildFullServicePacket(uint ttl)
        {
            bool goodbye = ttl == 0;

            var answers = new List<byte[]>
            {
                BuildRecord(EncodeName(ServiceType), TypePTR, ClassIN,
                    goodbye ? 0u : TtlShared,
                    EncodeName(_fqInstance))
            };

            var additional = new List<byte[]>
            {
                BuildRecord(EncodeName(_fqInstance), TypeSRV,
                    (ushort)(ClassIN | CacheFl),
                    goodbye ? 0u : TtlUnique,
                    BuildSrvRdata(_hostname, (ushort)_port)),

                BuildRecord(EncodeName(_fqInstance), TypeTXT,
                    (ushort)(ClassIN | CacheFl),
                    goodbye ? 0u : TtlShared,
                    BuildTxtRdata(new[]
                    {
                        $"version={_version}",
                        $"base_url={_baseUrl}",
                        $"location_name={_instanceName}",
                    }))
            };

            // A records — only from preferred (non-virtual) interfaces
            foreach (var ip in GetAddresses(AddressFamily.InterNetwork))
            {
                additional.Add(BuildRecord(
                    EncodeName(_hostname), TypeA,
                    (ushort)(ClassIN | CacheFl),
                    goodbye ? 0u : TtlUnique,
                    ip.GetAddressBytes()));
            }

            // AAAA records — non-link-local only
            foreach (var ip in GetAddresses(AddressFamily.InterNetworkV6))
            {
                if (IsLinkLocalV6(ip)) continue;
                additional.Add(BuildRecord(
                    EncodeName(_hostname), TypeAAAA,
                    (ushort)(ClassIN | CacheFl),
                    goodbye ? 0u : TtlUnique,
                    ip.GetAddressBytes()));
            }

            return BuildMessage(answers, additional);
        }

        // ── Wire-format helpers ───────────────────────────────────────────

        private static byte[] BuildMessage(
            IList<byte[]> answers,
            IList<byte[]> additionalRecords)
        {
            int anCount = answers?.Count ?? 0;
            int arCount = additionalRecords?.Count ?? 0;

            var header = new byte[12];
            header[2] = 0x84; // QR=1, AA=1
            header[6]  = (byte)(anCount >> 8);  header[7]  = (byte)(anCount & 0xFF);
            header[10] = (byte)(arCount >> 8);  header[11] = (byte)(arCount & 0xFF);

            int totalLen = 12
                + (answers?.Sum(r => r.Length) ?? 0)
                + (additionalRecords?.Sum(r => r.Length) ?? 0);

            var pkt = new byte[totalLen];
            Buffer.BlockCopy(header, 0, pkt, 0, 12);
            int pos = 12;

            if (answers != null)
                foreach (var r in answers) { Buffer.BlockCopy(r, 0, pkt, pos, r.Length); pos += r.Length; }
            if (additionalRecords != null)
                foreach (var r in additionalRecords) { Buffer.BlockCopy(r, 0, pkt, pos, r.Length); pos += r.Length; }

            return pkt;
        }

        private static byte[] BuildMessage(
            IList<byte[]> answers,
            IList<byte[]> authority,
            IList<byte[]> additional,
            bool isQuery,
            int qdCount,
            IList<byte[]> questionBytes)
        {
            int anCount = answers?.Count ?? 0;
            int nsCount = authority?.Count ?? 0;
            int arCount = additional?.Count ?? 0;

            var header = new byte[12];
            if (!isQuery) header[2] = 0x84;
            header[4] = (byte)(qdCount >> 8); header[5] = (byte)(qdCount & 0xFF);
            header[6] = (byte)(anCount >> 8); header[7] = (byte)(anCount & 0xFF);
            header[8] = (byte)(nsCount >> 8); header[9] = (byte)(nsCount & 0xFF);
            header[10] = (byte)(arCount >> 8); header[11] = (byte)(arCount & 0xFF);

            int totalLen = 12
                + (questionBytes?.Sum(q => q.Length) ?? 0)
                + (answers?.Sum(r => r.Length) ?? 0)
                + (authority?.Sum(r => r.Length) ?? 0)
                + (additional?.Sum(r => r.Length) ?? 0);

            var pkt = new byte[totalLen];
            Buffer.BlockCopy(header, 0, pkt, 0, 12);
            int pos = 12;

            void CopyAll(IList<byte[]> rrs)
            {
                if (rrs == null) return;
                foreach (var r in rrs) { Buffer.BlockCopy(r, 0, pkt, pos, r.Length); pos += r.Length; }
            }
            CopyAll(questionBytes);
            CopyAll(answers);
            CopyAll(authority);
            CopyAll(additional);

            return pkt;
        }

        private static byte[] BuildRecord(byte[] name, ushort type, ushort rrClass,
            uint ttl, byte[] rdata)
        {
            var buf = new byte[name.Length + 10 + rdata.Length];
            int i = 0;
            Buffer.BlockCopy(name, 0, buf, i, name.Length); i += name.Length;
            buf[i++] = (byte)(type    >> 8);  buf[i++] = (byte)(type    & 0xFF);
            buf[i++] = (byte)(rrClass >> 8);  buf[i++] = (byte)(rrClass & 0xFF);
            buf[i++] = (byte)(ttl     >> 24); buf[i++] = (byte)(ttl     >> 16);
            buf[i++] = (byte)(ttl     >>  8); buf[i++] = (byte)(ttl     & 0xFF);
            buf[i++] = (byte)(rdata.Length >> 8);
            buf[i++] = (byte)(rdata.Length & 0xFF);
            Buffer.BlockCopy(rdata, 0, buf, i, rdata.Length);
            return buf;
        }

        private static byte[] BuildSrvRdata(string target, ushort port)
        {
            byte[] targetBytes = EncodeName(target);
            var rdata = new byte[6 + targetBytes.Length];
            rdata[4] = (byte)(port >> 8);
            rdata[5] = (byte)(port & 0xFF);
            Buffer.BlockCopy(targetBytes, 0, rdata, 6, targetBytes.Length);
            return rdata;
        }

        private static byte[] BuildTxtRdata(string[] entries)
        {
            var buf = new List<byte>();
            foreach (var entry in entries)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(entry);
                int len = Math.Min(bytes.Length, 255);
                buf.Add((byte)len);
                for (int i = 0; i < len; i++) buf.Add(bytes[i]);
            }
            if (buf.Count == 0) buf.Add(0);
            return buf.ToArray();
        }

        private static byte[] EncodeName(string fqdn)
        {
            var buf = new List<byte>();
            foreach (var label in fqdn.TrimEnd('.').Split('.'))
            {
                if (label.Length == 0) continue;
                byte[] encoded = Encoding.ASCII.GetBytes(label);
                buf.Add((byte)encoded.Length);
                buf.AddRange(encoded);
            }
            buf.Add(0);
            return buf.ToArray();
        }

        // ── DNS name reader (with pointer/compression support) ────────────

        private static string ReadName(byte[] data, int dataLen, ref int offset)
        {
            var sb = new StringBuilder();
            int safetyLimit = 0;

            while (offset < dataLen)
            {
                byte labelLen = data[offset];

                if (labelLen == 0) { offset++; break; }

                if ((labelLen & 0xC0) == 0xC0)
                {
                    if (offset + 1 >= dataLen) break;
                    int ptr = ((labelLen & 0x3F) << 8) | data[offset + 1];
                    offset += 2;
                    int ptrOff = ptr;
                    string rest = ReadName(data, dataLen, ref ptrOff);
                    if (sb.Length > 0 && sb[sb.Length - 1] != '.') sb.Append('.');
                    sb.Append(rest);
                    return sb.ToString();
                }

                offset++;
                if (offset + labelLen > dataLen) break;
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.ASCII.GetString(data, offset, labelLen));
                offset += labelLen;

                if (++safetyLimit > 128) break;
            }

            if (sb.Length > 0 && sb[sb.Length - 1] != '.') sb.Append('.');
            return sb.ToString();
        }

        // ── Network helpers ───────────────────────────────────────────────

        private static bool IsLinkLocalV6(IPAddress addr)
        {
            var bytes = addr.GetAddressBytes();
            return bytes.Length == 16 && bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80;
        }

        private static Thread MakeThread(string name, ThreadStart body)
            => new Thread(body) { IsBackground = true, Name = name };
    }
}