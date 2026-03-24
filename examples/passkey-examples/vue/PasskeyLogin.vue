<template>
  <div class="passkey-login">
    <h2>{{ mode === 'login' ? 'Login with Passkey' : 'Register Passkey' }}</h2>

    <div class="form">
      <input
        v-model="username"
        type="text"
        placeholder="Username or email"
        :disabled="isLoading"
      />

      <template v-if="mode === 'register'">
        <input
          v-model="displayName"
          type="text"
          placeholder="Display name"
          :disabled="isLoading"
        />
        <input
          v-model="credentialName"
          type="text"
          placeholder="Credential name (e.g., 'iPhone 15 Pro')"
          :disabled="isLoading"
        />
      </template>

      <button
        @click="mode === 'login' ? handleLogin() : handleRegister()"
        :disabled="isLoading"
      >
        {{ isLoading ? 'Processing...' : mode === 'login' ? 'Login with Passkey' : 'Register Passkey' }}
      </button>

      <button
        @click="mode = mode === 'login' ? 'register' : 'login'"
        :disabled="isLoading"
        style="margin-top: 10px"
      >
        {{ mode === 'login' ? 'Register new passkey' : 'Back to login' }}
      </button>
    </div>

    <div class="info" style="margin-top: 20px; font-size: 14px; color: #666">
      <p><strong>Supported authenticators:</strong></p>
      <ul>
        <li>Face ID (iPhone, iPad, Mac)</li>
        <li>Touch ID (iPhone, iPad, Mac)</li>
        <li>Windows Hello (fingerprint, face, PIN)</li>
        <li>Hardware security keys (YubiKey, etc.)</li>
      </ul>
      <p><em>Note: HTTPS connection required for WebAuthn</em></p>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue';

/**
 * Passkey Login Component for Vue.js
 *
 * This component demonstrates passkey (WebAuthn) authentication using the IAM system.
 * It supports both registration and authentication flows with Face ID, Touch ID,
 * Windows Hello, or hardware security keys.
 *
 * Prerequisites:
 * - @iam-system/sdk installed: npm install @iam-system/sdk
 * - HTTPS connection (required for WebAuthn)
 * - Modern browser with WebAuthn support
 */

interface Props {
  apiUrl: string;
}

const props = defineProps<Props>();

const emit = defineEmits<{
  loginSuccess: [userId: string, token?: string];
  error: [error: string];
}>();

const username = ref('');
const displayName = ref('');
const credentialName = ref('');
const isLoading = ref(false);
const mode = ref<'login' | 'register'>('login');

/**
 * Begin passkey registration
 */
const handleRegister = async () => {
  if (!username.value || !displayName.value || !credentialName.value) {
    emit('error', 'Please fill in all fields');
    return;
  }

  isLoading.value = true;
  try {
    // Step 1: Get registration options from server
    const optionsResponse = await fetch(`${props.apiUrl}/api/passkey/register/begin`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${getAccessToken()}`, // Requires authentication
      },
      body: JSON.stringify({
        username: username.value,
        displayName: displayName.value,
      }),
    });

    if (!optionsResponse.ok) {
      throw new Error('Failed to begin registration');
    }

    const { options } = await optionsResponse.json();

    // Step 2: Convert base64 strings to ArrayBuffers (WebAuthn requirement)
    const credentialOptions = {
      ...options,
      challenge: base64ToArrayBuffer(options.challenge),
      user: {
        ...options.user,
        id: base64ToArrayBuffer(options.user.id),
      },
      excludeCredentials: options.excludeCredentials?.map((cred: any) => ({
        ...cred,
        id: base64ToArrayBuffer(cred.id),
      })),
    };

    // Step 3: Prompt user to create credential (Face ID, Touch ID, etc.)
    const credential = await navigator.credentials.create({
      publicKey: credentialOptions,
    }) as PublicKeyCredential;

    if (!credential) {
      throw new Error('Failed to create credential');
    }

    // Step 4: Prepare attestation response
    const attestationResponse = credential.response as AuthenticatorAttestationResponse;
    const attestationData = {
      id: credential.id,
      rawId: arrayBufferToBase64(credential.rawId),
      type: credential.type,
      response: {
        attestationObject: arrayBufferToBase64(attestationResponse.attestationObject),
        clientDataJSON: arrayBufferToBase64(attestationResponse.clientDataJSON),
      },
    };

    // Step 5: Complete registration on server
    const completeResponse = await fetch(`${props.apiUrl}/api/passkey/register/complete`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${getAccessToken()}`,
      },
      body: JSON.stringify({
        credentialName: credentialName.value,
        attestationResponse: attestationData,
      }),
    });

    if (!completeResponse.ok) {
      throw new Error('Failed to complete registration');
    }

    const result = await completeResponse.json();
    alert(`Success! ${result.message}`);
    mode.value = 'login';
    username.value = '';
    displayName.value = '';
    credentialName.value = '';
  } catch (error: any) {
    console.error('Registration error:', error);
    emit('error', error.message || 'Failed to register passkey');
  } finally {
    isLoading.value = false;
  }
};

/**
 * Begin passkey authentication
 */
const handleLogin = async () => {
  if (!username.value) {
    emit('error', 'Please enter your username');
    return;
  }

  isLoading.value = true;
  try {
    // Step 1: Get authentication options from server
    const optionsResponse = await fetch(`${props.apiUrl}/api/passkey/authenticate/begin`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        username: username.value,
      }),
    });

    if (!optionsResponse.ok) {
      throw new Error('Failed to begin authentication');
    }

    const { options } = await optionsResponse.json();

    // Step 2: Convert base64 strings to ArrayBuffers
    const assertionOptions = {
      ...options,
      challenge: base64ToArrayBuffer(options.challenge),
      allowCredentials: options.allowCredentials?.map((cred: any) => ({
        ...cred,
        id: base64ToArrayBuffer(cred.id),
      })),
    };

    // Step 3: Prompt user for authentication (Face ID, Touch ID, etc.)
    const assertion = await navigator.credentials.get({
      publicKey: assertionOptions,
    }) as PublicKeyCredential;

    if (!assertion) {
      throw new Error('Failed to get assertion');
    }

    // Step 4: Prepare assertion response
    const assertionResponse = assertion.response as AuthenticatorAssertionResponse;
    const assertionData = {
      id: assertion.id,
      rawId: arrayBufferToBase64(assertion.rawId),
      type: assertion.type,
      response: {
        authenticatorData: arrayBufferToBase64(assertionResponse.authenticatorData),
        clientDataJSON: arrayBufferToBase64(assertionResponse.clientDataJSON),
        signature: arrayBufferToBase64(assertionResponse.signature),
        userHandle: assertionResponse.userHandle
          ? arrayBufferToBase64(assertionResponse.userHandle)
          : null,
      },
    };

    // Step 5: Complete authentication on server
    const completeResponse = await fetch(`${props.apiUrl}/api/passkey/authenticate/complete`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(assertionData),
    });

    if (!completeResponse.ok) {
      throw new Error('Failed to complete authentication');
    }

    const result = await completeResponse.json();
    emit('loginSuccess', result.userId, result.token);
  } catch (error: any) {
    console.error('Authentication error:', error);
    emit('error', error.message || 'Failed to authenticate');
  } finally {
    isLoading.value = false;
  }
};

// Utility functions for base64/ArrayBuffer conversion
const base64ToArrayBuffer = (base64: string): ArrayBuffer => {
  const binaryString = atob(base64.replace(/-/g, '+').replace(/_/g, '/'));
  const bytes = new Uint8Array(binaryString.length);
  for (let i = 0; i < binaryString.length; i++) {
    bytes[i] = binaryString.charCodeAt(i);
  }
  return bytes.buffer;
};

const arrayBufferToBase64 = (buffer: ArrayBuffer): string => {
  const bytes = new Uint8Array(buffer);
  let binary = '';
  for (let i = 0; i < bytes.byteLength; i++) {
    binary += String.fromCharCode(bytes[i]);
  }
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=/g, '');
};

const getAccessToken = (): string => {
  // Replace with your actual token retrieval logic
  return localStorage.getItem('accessToken') || '';
};
</script>

<style scoped>
.passkey-login {
  max-width: 400px;
  margin: 0 auto;
  padding: 20px;
}

.form {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

input {
  padding: 10px;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 14px;
}

button {
  padding: 12px;
  background-color: #0066cc;
  color: white;
  border: none;
  border-radius: 4px;
  font-size: 14px;
  cursor: pointer;
}

button:hover:not(:disabled) {
  background-color: #0052a3;
}

button:disabled {
  background-color: #ccc;
  cursor: not-allowed;
}

.info ul {
  padding-left: 20px;
}

.info li {
  margin: 5px 0;
}
</style>
