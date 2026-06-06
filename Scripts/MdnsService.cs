using System.Net;
using Timberborn.HttpApiSystem;
using Timberborn.SingletonSystem;
using Timberborn.Versioning;
using UnityEngine;

namespace RemoteApiAccess
{
    /// <summary>
    /// Game-context singleton that starts and stops the mDNS responder in
    /// lock-step with Timberborn's own HTTP API.
    /// </summary>
    public class MdnsService : ILoadableSingleton, IUnloadableSingleton
    {
        private readonly HttpApi       _httpApi;
        private          MdnsResponder _responder;

        public MdnsService(HttpApi httpApi)
        {
            _httpApi = httpApi;
        }

        public void Load()
        {
            int    port    = _httpApi.Port;
            string version = GameVersions.CurrentVersion.Formatted;
            string host    = Dns.GetHostName().Replace(' ', '-');

            Debug.Log($"[Remote Api Access] Starting mDNS responder – port {port}, version {version}");

            _responder = new MdnsResponder(port, version, host);
            _responder.Start();
        }

        public void Unload()
        {
            _responder?.Stop();
            _responder = null;
        }
    }
}