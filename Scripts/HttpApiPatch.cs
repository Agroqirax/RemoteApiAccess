using HarmonyLib;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Timberborn.HttpApiSystem;
using UnityEngine;

namespace RemoteApiAccess
{
    [HarmonyPatch(typeof(HttpApi), "Start")]
    internal static class HttpApiStartPatch
    {
        private static string GetLanIp()
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (UnicastIPAddressInformation addr in
                         nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        return addr.Address.ToString();
                }
            }

            try
            {
                using Socket s = new Socket(AddressFamily.InterNetwork,
                                            SocketType.Dgram, ProtocolType.Udp);
                s.Connect("8.8.8.8", 53);
                return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
            }
            catch
            {
                return null;
            }
        }

        static void Prefix(HttpApi __instance)
        {
            if (__instance.IsRunning) return;
            string lanUrl = $"http://+:{__instance.Port}/";
            Traverse.Create(__instance).Property("Url").SetValue(lanUrl);
            Debug.Log($"[Remote Api Access] Temporarily set URL to {lanUrl} for HttpListener binding");
        }

        static void Postfix(HttpApi __instance)
        {
            string ip = GetLanIp() ?? "localhost";
            string displayUrl = $"http://{ip}:{__instance.Port}/";
            Traverse.Create(__instance).Property("Url").SetValue(displayUrl);
            Debug.Log($"[Remote Api Access] Set display URL to {displayUrl}");
        }
    }
}