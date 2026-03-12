# Remote Api Access

Accepts API requests from any host on the network, instead of only localhost.
Also adds the [CORS](https://developer.mozilla.org/en-US/docs/Web/HTTP/Guides/CORS) headers so websites can make requests without being blocked by the browser.

TL;DR This allows other devices (and websites) to access the timberborn API

## Security notice

This mod removes some security restrictions. This is fine as long as you understand the consequences:

1. **Any computer on the same network can access the API.**
   (Network meaning the same Wi-Fi or anything connected to the same router.)
   - Using this on **public or shared Wi-Fi is a very bad idea.**

2. **Any website can access the API from your browser.**
   Because the API is accessible on the network and CORS is enabled, a website you visit can send requests to Timberborn.
   - This also applies to **other devices on your network**. For example, a website opened on your phone could interact with Timberborn running on your PC.

3. **If the API port is exposed to the internet (for example via port forwarding), anyone on the internet could access it.**

4. **Timberborn itself does not expose sensitive information through the API.**
   However, if another mod extends the API, those endpoints would also be accessible as described above.

## Install

- [Steam workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3682669754): Click subscribe
- [Mod.io](https://mod.io/g/timberborn/m/remote-api-access): Download & extract to `~/Documents/Timberborn/Mods/RemoteApiAccess`.
- [Github](https://github.com/agroqirax/remoteapiaccess/releases/latest): Download & extract to `~/Documents/Timberborn/Mods/RemoteApiAccess`.
