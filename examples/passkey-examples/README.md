# Passkey Examples

This directory contains example implementations of passkey (WebAuthn) authentication using the IAM system.

## Overview

Passkeys are a modern, passwordless authentication method that uses Face ID, Touch ID, Windows Hello, or hardware security keys (like YubiKey) for secure, phishing-resistant authentication.

## Available Examples

### React (`react/PasskeyLogin.tsx`)
- Complete TypeScript React component
- Supports both registration and authentication
- Includes base64/ArrayBuffer conversion utilities
- Production-ready with error handling

### Vue.js (`vue/PasskeyLogin.vue`)
- Complete Vue 3 Composition API component with TypeScript
- Supports both registration and authentication
- Includes scoped styles
- Production-ready with error handling

## Prerequisites

### Browser Requirements
- Modern browser with WebAuthn support:
  - Chrome 67+
  - Firefox 60+
  - Safari 13+
  - Edge 18+

### Connection Requirements
- **HTTPS required** (WebAuthn only works over secure connections)
- For local development: `https://localhost:5161` or use ngrok/similar

### IAM System Requirements
- IAM API running with passkey endpoints
- Fido2 configured in appsettings.json

## Usage

### React Example

```tsx
import PasskeyLogin from './passkey-examples/react/PasskeyLogin';

function App() {
  return (
    <PasskeyLogin
      apiUrl="https://localhost:5161"
      onLoginSuccess={(userId, token) => {
        console.log('Logged in!', userId);
        if (token) {
          localStorage.setItem('accessToken', token);
        }
      }}
      onError={(error) => {
        console.error('Login failed:', error);
        alert(error);
      }}
    />
  );
}
```

### Vue Example

```vue
<template>
  <PasskeyLogin
    :api-url="'https://localhost:5161'"
    @login-success="handleLoginSuccess"
    @error="handleError"
  />
</template>

<script setup lang="ts">
import PasskeyLogin from './passkey-examples/vue/PasskeyLogin.vue';

const handleLoginSuccess = (userId: string, token?: string) => {
  console.log('Logged in!', userId);
  if (token) {
    localStorage.setItem('accessToken', token);
  }
};

const handleError = (error: string) => {
  console.error('Login failed:', error);
  alert(error);
};
</script>
```

## Registration Flow

1. User enters username, display name, and credential name
2. Component calls `POST /api/passkey/register/begin`
3. Server returns WebAuthn credential creation options
4. Component prompts user with Face ID/Touch ID/etc.
5. User authenticates with biometric/PIN
6. Component sends attestation response to `POST /api/passkey/register/complete`
7. Server verifies and stores credential

## Authentication Flow

1. User enters username
2. Component calls `POST /api/passkey/authenticate/begin`
3. Server returns WebAuthn assertion options
4. Component prompts user with Face ID/Touch ID/etc.
5. User authenticates with biometric/PIN
6. Component sends assertion response to `POST /api/passkey/authenticate/complete`
7. Server verifies and returns JWT token

## Supported Authenticators

### Platform Authenticators (Built-in)
- **iOS/iPadOS**: Face ID, Touch ID
- **macOS**: Face ID, Touch ID
- **Windows**: Windows Hello (fingerprint, face, PIN)
- **Android**: Fingerprint, face unlock

### Cross-Platform Authenticators (Hardware)
- **YubiKey 5 Series** (NFC, USB-A, USB-C, Lightning)
- **Titan Security Key** (Google)
- **SoloKeys**
- **Nitrokey**

## Security Features

✅ **Phishing-resistant**: Credentials are bound to origin
✅ **No shared secrets**: Private keys never leave device
✅ **Replay protection**: Sign counter prevents replay attacks
✅ **FIDO2/WebAuthn**: Industry standard protocol
✅ **No passwords**: Passwordless authentication

## Development Tips

### Testing Locally with HTTPS

```bash
# Option 1: Use mkcert to create local certificates
mkcert -install
mkcert localhost

# Option 2: Use ngrok for HTTPS tunneling
ngrok http 5173

# Option 3: Vite with HTTPS
vite --https
```

### Browser DevTools

1. Chrome: Open DevTools → Application → Web Authentication
2. Firefox: about:config → security.webauthn.enable_uvm_extension
3. Safari: Develop → Experimental Features → Web Authentication

### Testing Without Biometrics

Most browsers allow testing WebAuthn with a virtual authenticator:
- Chrome DevTools: Application → WebAuthn → Enable virtual authenticator

## Troubleshooting

### "SecurityError: The operation is insecure"
- **Cause**: Not using HTTPS
- **Fix**: Use HTTPS or localhost

### "NotAllowedError: The operation either timed out or was not allowed"
- **Cause**: User cancelled, timeout, or invalid domain
- **Fix**: Check HTTPS, verify domain matches registration

### "NotSupportedError: The operation is not supported"
- **Cause**: Browser doesn't support WebAuthn
- **Fix**: Update browser or use supported browser

### "InvalidStateError: The user attempted to register an authenticator that is already registered"
- **Cause**: Credential already registered
- **Fix**: Use different authenticator or delete existing credential

## API Reference

### Registration Endpoints

**Begin Registration**
```
POST /api/passkey/register/begin
Authorization: Bearer <token>

{
  "username": "user@example.com",
  "displayName": "John Doe"
}

Response:
{
  "options": { ... } // CredentialCreateOptions
}
```

**Complete Registration**
```
POST /api/passkey/register/complete
Authorization: Bearer <token>

{
  "credentialName": "iPhone 15 Pro",
  "attestationResponse": { ... }
}

Response:
{
  "message": "Passkey registered successfully"
}
```

### Authentication Endpoints

**Begin Authentication**
```
POST /api/passkey/authenticate/begin

{
  "username": "user@example.com"
}

Response:
{
  "options": { ... } // AssertionOptions
}
```

**Complete Authentication**
```
POST /api/passkey/authenticate/complete

{
  "id": "...",
  "rawId": "...",
  "type": "public-key",
  "response": {
    "authenticatorData": "...",
    "clientDataJSON": "...",
    "signature": "...",
    "userHandle": "..."
  }
}

Response:
{
  "userId": "guid",
  "message": "Authentication successful",
  "token": "jwt-token" // When implemented
}
```

### Credential Management Endpoints

**List Credentials**
```
GET /api/passkey/credentials
Authorization: Bearer <token>

Response: [
  {
    "id": "guid",
    "name": "iPhone 15 Pro",
    "deviceType": "phone",
    "createdAt": "2026-03-23T10:30:00Z",
    "lastUsedAt": "2026-03-23T10:35:00Z",
    "isBackupEligible": true,
    "transports": ["internal", "nfc"]
  }
]
```

**Delete Credential**
```
DELETE /api/passkey/credentials/{id}
Authorization: Bearer <token>

Response:
{
  "message": "Credential deleted successfully"
}
```

**Rename Credential**
```
PATCH /api/passkey/credentials/{id}
Authorization: Bearer <token>

{
  "newName": "My YubiKey"
}

Response:
{
  "message": "Credential renamed successfully"
}
```

## Further Reading

- [WebAuthn Guide](https://webauthn.guide/)
- [W3C WebAuthn Specification](https://www.w3.org/TR/webauthn-2/)
- [FIDO Alliance](https://fidoalliance.org/)
- [Passkeys.dev](https://passkeys.dev/)

## License

MIT License - See main project LICENSE file
