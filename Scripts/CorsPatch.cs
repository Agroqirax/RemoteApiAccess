using HarmonyLib;
using System.Net;
using System.Threading.Tasks;
using Timberborn.HttpApiSystem;

namespace HttpApiLan
{
    // Add CORS headers to every response via the single Write() method
    [HarmonyPatch(typeof(HttpListenerContextExtensions), "Write")]
    internal static class HttpListenerContextExtensionsWritePatch
    {
        static void Prefix(HttpListenerContext context)
        {
            context.Response.Headers.Set("Access-Control-Allow-Origin", "*");
            context.Response.Headers.Set("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            context.Response.Headers.Set("Access-Control-Allow-Headers", "*");
            context.Response.Headers.Set("Access-Control-Max-Age", "86400");
        }
    }

    // Handle OPTIONS preflight requests that browsers send before cross-origin requests
    public class CorsPreflightEndpoint : IHttpApiEndpoint
    {
        public async Task<bool> TryHandle(HttpListenerContext context)
        {
            if (context.Request.HttpMethod != "OPTIONS") return false;

            context.Response.Headers.Set("Access-Control-Allow-Origin", "*");
            context.Response.Headers.Set("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            context.Response.Headers.Set("Access-Control-Allow-Headers", "*");
            context.Response.Headers.Set("Access-Control-Max-Age", "86400");
            context.Response.StatusCode = 204;
            context.Response.Close();
            return true;
        }
    }
}