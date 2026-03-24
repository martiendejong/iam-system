import React, { useState } from 'react';

/**
 * Passkey Login Component for React
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

interface PasskeyLoginProps {
  apiUrl: string;
  onLoginSuccess: (userId: string, token?: string) => void;
  onError: (error: string) => void;
}

export const PasskeyLogin: React.FC<PasskeyLoginProps> = ({
  apiUrl,
  onLoginSuccess,
  onError,
}) => {
  const [username, setUsername] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [credentialName, setCredentialName] = useState('');
  const [isLoading, setIsLoading] = useState(false);
  const [mode, setMode] = useState<'login' | 'register'>('login');

  /**
   * Begin passkey registration
   */
  const handleRegister = async () => {
    if (!username || !displayName || !credentialName) {
      onError('Please fill in all fields');
      return;
    }

    setIsLoading(true);
    try {
      // Step 1: Get registration options from server
      const optionsResponse = await fetch(`${apiUrl}/api/passkey/register/begin`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${getAccessToken()}`, // Requires authentication
        },
        body: JSON.stringify({
          username,
          displayName,
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
      const completeResponse = await fetch(`${apiUrl}/api/passkey/register/complete`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${getAccessToken()}`,
        },
        body: JSON.stringify({
          credentialName,
          attestationResponse: attestationData,
        }),
      });

      if (!completeResponse.ok) {
        throw new Error('Failed to complete registration');
      }

      const result = await completeResponse.json();
      alert(`Success! ${result.message}`);
      setMode('login');
      setUsername('');
      setDisplayName('');
      setCredentialName('');
    } catch (error: any) {
      console.error('Registration error:', error);
      onError(error.message || 'Failed to register passkey');
    } finally {
      setIsLoading(false);
    }
  };

  /**
   * Begin passkey authentication
   */
  const handleLogin = async () => {
    if (!username) {
      onError('Please enter your username');
      return;
    }

    setIsLoading(true);
    try {
      // Step 1: Get authentication options from server
      const optionsResponse = await fetch(`${apiUrl}/api/passkey/authenticate/begin`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          username,
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
      const completeResponse = await fetch(`${apiUrl}/api/passkey/authenticate/complete`, {
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
      onLoginSuccess(result.userId, result.token);
    } catch (error: any) {
      console.error('Authentication error:', error);
      onError(error.message || 'Failed to authenticate');
    } finally {
      setIsLoading(false);
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

  return (
    <div className="passkey-login">
      <h2>{mode === 'login' ? 'Login with Passkey' : 'Register Passkey'}</h2>

      <div className="form">
        <input
          type="text"
          placeholder="Username or email"
          value={username}
          onChange={(e) => setUsername(e.target.value)}
          disabled={isLoading}
        />

        {mode === 'register' && (
          <>
            <input
              type="text"
              placeholder="Display name"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
              disabled={isLoading}
            />
            <input
              type="text"
              placeholder="Credential name (e.g., 'iPhone 15 Pro')"
              value={credentialName}
              onChange={(e) => setCredentialName(e.target.value)}
              disabled={isLoading}
            />
          </>
        )}

        <button
          onClick={mode === 'login' ? handleLogin : handleRegister}
          disabled={isLoading}
        >
          {isLoading
            ? 'Processing...'
            : mode === 'login'
            ? 'Login with Passkey'
            : 'Register Passkey'}
        </button>

        <button
          onClick={() => setMode(mode === 'login' ? 'register' : 'login')}
          disabled={isLoading}
          style={{ marginTop: '10px' }}
        >
          {mode === 'login' ? 'Register new passkey' : 'Back to login'}
        </button>
      </div>

      <div className="info" style={{ marginTop: '20px', fontSize: '14px', color: '#666' }}>
        <p>
          <strong>Supported authenticators:</strong>
        </p>
        <ul>
          <li>Face ID (iPhone, iPad, Mac)</li>
          <li>Touch ID (iPhone, iPad, Mac)</li>
          <li>Windows Hello (fingerprint, face, PIN)</li>
          <li>Hardware security keys (YubiKey, etc.)</li>
        </ul>
        <p>
          <em>Note: HTTPS connection required for WebAuthn</em>
        </p>
      </div>
    </div>
  );
};

export default PasskeyLogin;
