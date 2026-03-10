using HarmonyLib;
using System.Reflection;
using Timberborn.HttpApiSystem;
using UnityEngine;

namespace HttpApiLan
{
    [HarmonyPatch(typeof(HttpApi), "Start")]
    internal static class HttpApiStartPatch
    {
        static void Prefix(HttpApi __instance)
        {
            if (__instance.IsRunning) return;

            // Temporarily set Url to the + variant so HttpListener binds correctly
            string lanUrl = $"http://+:{__instance.Port}/";
            Traverse.Create(__instance).Property("Url").SetValue(lanUrl);
            Debug.Log($"[HttpApiLan] Temporarily set URL to {lanUrl} for HttpListener binding");
        }

        static void Postfix(HttpApi __instance)
        {
            // Restore Url to localhost so UriBuilder and the UI work correctly
            // (Do this whether Start succeeded or failed)
            string localhostUrl = $"http://localhost:{__instance.Port}/";
            Traverse.Create(__instance).Property("Url").SetValue(localhostUrl);
            Debug.Log($"[HttpApiLan] Restored URL to {localhostUrl}");
        }
    }
}