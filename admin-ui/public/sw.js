/* Service worker for the Jengo IAM app: shows "approve this sign-in?" notifications and opens the approve page.
   Deliberately no caching and no fetch handler: sign-in state must never be served from a stale cache. */

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));

self.addEventListener('push', (event) => {
  let data = {};
  try {
    data = event.data ? event.data.json() : {};
  } catch (e) {
    data = {};
  }

  if (data.type !== 'login-approval') {
    return;
  }

  const device = data.device || 'a new device';
  event.waitUntil(
    self.registration.showNotification('Sign-in request', {
      body: device + ' wants to sign in to your account. Tap to review.',
      icon: 'icon-192.png',
      badge: 'icon-192.png',
      tag: 'login-approval-' + data.id,
      requireInteraction: true,
      data: { url: data.url || './' },
    })
  );
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  // The payload url is relative to this worker's scope (the app's base path).
  const target = new URL((event.notification.data && event.notification.data.url) || './', self.registration.scope).href;

  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((clients) => {
      for (const client of clients) {
        if (client.url.startsWith(self.registration.scope) && 'focus' in client) {
          if ('navigate' in client) {
            return client.navigate(target).then((c) => c && c.focus());
          }
          return client.focus();
        }
      }
      return self.clients.openWindow(target);
    })
  );
});
