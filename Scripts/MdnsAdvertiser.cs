using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace RemoteApiAccess
{
    public class MdnsAdvertiser : IDisposable
    {
        private const string MulticastAddress = "224.0.0.251";
        private const int MdnsPort = 5353;
        private const string ServiceType = "_timberborn._tcp.local.";
        private const string InstanceName = "Timberborn Game";

        private Timer _timer;
        private UdpClient _udpClient;
        private readonly int _port;

        public MdnsAdvertiser(int port)
        {
            _port = port;
        }

        public void Start()
        {
            try
            {
                _udpClient = new UdpClient();
                _udpClient.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));
                // Announce immediately, then every 20s
                _timer = new Timer(_ => SendAnnouncement(), null, 0, 20000);
                Debug.Log("[Remote Api Access] mDNS advertiser started");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Remote Api Access] mDNS failed to start: {e.Message}");
            }
        }

        private void SendAnnouncement()
        {
            try
            {
                byte[] packet = BuildMdnsPacket();
                var endpoint = new IPEndPoint(IPAddress.Parse(MulticastAddress), MdnsPort);
                _udpClient.Send(packet, packet.Length, endpoint);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Remote Api Access] mDNS send failed: {e.Message}");
            }
        }

        private byte[] BuildMdnsPacket()
        {
            // Minimal mDNS PTR + SRV + TXT answer packet
            var buf = new System.IO.MemoryStream();
            var w = new System.IO.BinaryWriter(buf);

            // Header: ID=0, QR=1 (response), AA=1, 3 answers
            w.Write(ToBytes((ushort)0x0000)); // ID
            w.Write(ToBytes((ushort)0x8400)); // Flags: response + authoritative
            w.Write(ToBytes((ushort)0));      // Questions
            w.Write(ToBytes((ushort)3));      // Answer RRs
            w.Write(ToBytes((ushort)0));      // Authority RRs
            w.Write(ToBytes((ushort)0));      // Additional RRs

            string instanceFull = $"{InstanceName}.{ServiceType}";

            // PTR record: _timberborn._tcp.local. -> "Timberborn Game._timberborn._tcp.local."
            WriteDnsName(w, ServiceType);
            w.Write(ToBytes((ushort)12));     // PTR type
            w.Write(ToBytes((ushort)1));      // IN class
            w.Write(ToBytes((uint)4500));     // TTL 75 min
            var ptrData = EncodeDnsName(instanceFull);
            w.Write(ToBytes((ushort)ptrData.Length));
            w.Write(ptrData);

            // SRV record
            WriteDnsName(w, instanceFull);
            w.Write(ToBytes((ushort)33));     // SRV type
            w.Write(ToBytes((ushort)1));      // IN class
            w.Write(ToBytes((uint)120));      // TTL 2 min
            string hostName = $"{Dns.GetHostName()}.local.";
            var srvTarget = EncodeDnsName(hostName);
            w.Write(ToBytes((ushort)(6 + srvTarget.Length)));
            w.Write(ToBytes((ushort)0));      // Priority
            w.Write(ToBytes((ushort)0));      // Weight
            w.Write(ToBytes((ushort)_port));  // Port
            w.Write(srvTarget);

            // TXT record (empty but required)
            WriteDnsName(w, instanceFull);
            w.Write(ToBytes((ushort)16));     // TXT type
            w.Write(ToBytes((ushort)1));      // IN class
            w.Write(ToBytes((uint)4500));     // TTL
            w.Write(ToBytes((ushort)1));      // RDLENGTH
            w.Write((byte)0);                 // Empty TXT

            return buf.ToArray();
        }

        private static void WriteDnsName(System.IO.BinaryWriter w, string name)
            => w.Write(EncodeDnsName(name));

        private static byte[] EncodeDnsName(string name)
        {
            var buf = new System.IO.MemoryStream();
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                buf.WriteByte((byte)bytes.Length);
                buf.Write(bytes, 0, bytes.Length);
            }
            buf.WriteByte(0); // root
            return buf.ToArray();
        }

        private static byte[] ToBytes(ushort v)
            => new[] { (byte)(v >> 8), (byte)(v & 0xFF) };
        private static byte[] ToBytes(uint v)
            => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)(v & 0xFF) };

        public void Dispose()
        {
            _timer?.Dispose();
            _udpClient?.Close();
        }
    }
}