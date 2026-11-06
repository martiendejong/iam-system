# MQTT broker: forwarding the client certificate fingerprint

IAM no longer trusts the client-chosen `clientId` for devices with `AuthenticationMethod = certificate`.
On every CONNECT the broker must send the fingerprint of the client certificate it verified in the TLS
handshake. IAM allows the device only if that fingerprint belongs to an **Active**, not revoked, currently
valid (`NotBefore <= now < NotAfter`) certificate registered for **that same device**. A missing or
non-matching fingerprint returns 403 (Mosquitto) or `{"result":"deny"}` (EMQX). HMAC devices are unchanged.

Fingerprint format: hex, any case, optional `:` separators. IAM accepts the stored thumbprint of the
certificate or its SHA-256 over the DER bytes.

The broker must require client certificates (`require_certificate true` / `verify peer` +
`fail_if_no_peer_cert`), otherwise there is no fingerprint to forward.

## Mosquitto (HTTP auth plugin, POST /api/mqtt/auth)
Send the fingerprint as JSON field `certFingerprint` (or form-encoded `certFingerprint`) in the auth request body, e.g. with
mosquitto-go-auth HTTP backend `params_mode json` and the TLS-derived value mapped to `certFingerprint`
(plugins exposing the peer certificate hash, e.g. `%{cert_fingerprint}` style variables). Keep the
`X-Broker-Secret` header.

## EMQX (HTTP authentication, POST /api/mqtt/emqx/auth)
```
body: {
  "clientId": "${clientid}",
  "username": "${username}",
  "password": "${password}",
  "peerHost": "${peerhost}",
  "certFingerprint": "${cert_fingerprint}"
}
```
Enable `ssl.verify = verify_peer` and `fail_if_no_peer_cert = true` on the listener.

## Rollout
Deploy broker configuration first (or together with IAM). IAM without a forwarded fingerprint denies every
certificate device. Out of scope: with no `Mqtt:BrokerSecret` configured, the broker secret check allows all callers.
