/* MatPaper service worker */
const CACHE_NAME = "matpaper-v1";
const APP_SHELL = [
  "/css/app.css",
  "/css/components.css",
  "/js/app.js",
  "/offline.html"
];

self.addEventListener("install", (event) => {
  event.waitUntil(
    (async () => {
      const cache = await caches.open(CACHE_NAME);
      await cache.addAll(APP_SHELL);
      await self.skipWaiting();
    })()
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    (async () => {
      const keys = await caches.keys();
      await Promise.all(
        keys.filter((key) => key !== CACHE_NAME).map((key) => caches.delete(key))
      );
      await self.clients.claim();
    })()
  );
});

function isNoCachePath(pathname) {
  if (pathname.startsWith("/Share/") || pathname.startsWith("/System/")) {
    return true;
  }
  // /Documents/{something}/file | download | view
  if (/^\/Documents\/[^/]+\/(file|download|view)/i.test(pathname)) {
    return true;
  }
  return false;
}

function isStaticAsset(pathname) {
  return (
    pathname.startsWith("/css/") ||
    pathname.startsWith("/js/") ||
    pathname.startsWith("/icons/") ||
    pathname.startsWith("/lib/") ||
    /\.(css|js|png|jpg|jpeg|gif|svg|webp|ico|woff2?|ttf)$/i.test(pathname)
  );
}

async function networkFirstNavigation(request) {
  try {
    return await fetch(request);
  } catch (err) {
    const cache = await caches.open(CACHE_NAME);
    const offline = await cache.match("/offline.html");
    if (offline) {
      return offline;
    }
    return new Response("Offline", {
      status: 503,
      statusText: "Offline",
      headers: { "Content-Type": "text/plain; charset=utf-8" }
    });
  }
}

async function cacheFirstAsset(request) {
  const cache = await caches.open(CACHE_NAME);
  const cached = await cache.match(request);
  const fetchAndUpdate = fetch(request)
    .then((response) => {
      if (response && response.ok && response.type === "basic") {
        cache.put(request, response.clone()).catch(() => {});
      }
      return response;
    })
    .catch(() => cached);

  return cached || fetchAndUpdate;
}

self.addEventListener("fetch", (event) => {
  const request = event.request;

  if (request.method !== "GET") {
    return;
  }

  let url;
  try {
    url = new URL(request.url);
  } catch (err) {
    return;
  }

  if (url.origin !== self.location.origin) {
    return;
  }

  if (isNoCachePath(url.pathname)) {
    return; // always go to network (default browser behavior)
  }

  if (request.mode === "navigate") {
    event.respondWith(networkFirstNavigation(request));
    return;
  }

  if (isStaticAsset(url.pathname)) {
    event.respondWith(cacheFirstAsset(request));
  }
});
