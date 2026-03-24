using HarmonyLib;
using Timberborn.ModManagerScene;
using UnityEngine;

namespace RemoteApiAccess
{
    public class RemoteApiAccessModStarter : IModStarter
    {
        public void StartMod(IModEnvironment modEnvironment)
        {
            Debug.Log("[Remote Api Access] Applying HttpListener patch...");
            var harmony = new Harmony("Agroqirax.RemoteApiAccess");
            harmony.PatchAll();
            Debug.Log("[Remote Api Access] Patch applied.");
        }
    }
}