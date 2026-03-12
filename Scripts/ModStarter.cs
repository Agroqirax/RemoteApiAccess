using HarmonyLib;
using Timberborn.ModManagerScene;
using UnityEngine;

namespace HttpApiLan
{
    public class HttpApiLanModStarter : IModStarter
    {
        public void StartMod(IModEnvironment modEnvironment)
        {
            Debug.Log("[HttpApiLan] Applying HttpListener LAN patch...");
            var harmony = new Harmony("Agroqirax.RemoteApiAccess");
            harmony.PatchAll();
            Debug.Log("[HttpApiLan] Patch applied.");
        }
    }
}