package provider

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"time"
)

// IamClient wraps HTTP communication with the IAM System API.
type IamClient struct {
	BaseURL    string
	HTTPClient *http.Client
	Token      string
}

// authResponse is the response from the login endpoint.
type authResponse struct {
	AccessToken string `json:"accessToken"`
}

// apiError represents an error response from the IAM API.
type apiError struct {
	Error   string `json:"error"`
	Message string `json:"message"`
}

// NewClient creates a new IAM API client. It authenticates with either an API key
// (passed as a Bearer token) or username/password credentials (via /api/auth/login).
func NewClient(baseURL, apiKey, username, password string) (*IamClient, error) {
	client := &IamClient{
		BaseURL: baseURL,
		HTTPClient: &http.Client{
			Timeout: 30 * time.Second,
		},
	}

	// If an API key is provided, use it directly as a bearer token.
	if apiKey != "" {
		client.Token = apiKey
		return client, nil
	}

	// Otherwise authenticate with username/password.
	if username != "" && password != "" {
		token, err := client.authenticate(username, password)
		if err != nil {
			return nil, fmt.Errorf("authentication failed: %w", err)
		}
		client.Token = token
		return client, nil
	}

	return nil, fmt.Errorf("either api_key or username/password must be provided")
}

// authenticate performs a login request and returns the access token.
func (c *IamClient) authenticate(email, password string) (string, error) {
	loginBody := map[string]string{
		"email":    email,
		"password": password,
	}

	bodyBytes, err := json.Marshal(loginBody)
	if err != nil {
		return "", fmt.Errorf("failed to marshal login request: %w", err)
	}

	resp, err := c.HTTPClient.Post(
		fmt.Sprintf("%s/api/auth/login", c.BaseURL),
		"application/json",
		bytes.NewBuffer(bodyBytes),
	)
	if err != nil {
		return "", fmt.Errorf("login request failed: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		body, _ := io.ReadAll(resp.Body)
		return "", fmt.Errorf("login failed with status %d: %s", resp.StatusCode, string(body))
	}

	var authResp authResponse
	if err := json.NewDecoder(resp.Body).Decode(&authResp); err != nil {
		return "", fmt.Errorf("failed to decode login response: %w", err)
	}

	if authResp.AccessToken == "" {
		return "", fmt.Errorf("login response did not contain an access token")
	}

	return authResp.AccessToken, nil
}

// doRequest executes an HTTP request against the IAM API with authentication.
// The body parameter is JSON-serialized if non-nil.
// Returns the response body bytes and any error.
func (c *IamClient) doRequest(method, path string, body interface{}) ([]byte, error) {
	var reqBody io.Reader

	if body != nil {
		bodyBytes, err := json.Marshal(body)
		if err != nil {
			return nil, fmt.Errorf("failed to marshal request body: %w", err)
		}
		reqBody = bytes.NewBuffer(bodyBytes)
	}

	url := fmt.Sprintf("%s%s", c.BaseURL, path)
	req, err := http.NewRequest(method, url, reqBody)
	if err != nil {
		return nil, fmt.Errorf("failed to create request: %w", err)
	}

	req.Header.Set("Authorization", fmt.Sprintf("Bearer %s", c.Token))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json")

	resp, err := c.HTTPClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf("request failed: %w", err)
	}
	defer resp.Body.Close()

	respBody, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, fmt.Errorf("failed to read response body: %w", err)
	}

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		var apiErr apiError
		if json.Unmarshal(respBody, &apiErr) == nil && apiErr.Error != "" {
			return nil, fmt.Errorf("API error (HTTP %d): %s", resp.StatusCode, apiErr.Error)
		}
		return nil, fmt.Errorf("API request failed with status %d: %s", resp.StatusCode, string(respBody))
	}

	return respBody, nil
}

// doRequestWithStatus is like doRequest but also returns the HTTP status code.
func (c *IamClient) doRequestWithStatus(method, path string, body interface{}) ([]byte, int, error) {
	var reqBody io.Reader

	if body != nil {
		bodyBytes, err := json.Marshal(body)
		if err != nil {
			return nil, 0, fmt.Errorf("failed to marshal request body: %w", err)
		}
		reqBody = bytes.NewBuffer(bodyBytes)
	}

	url := fmt.Sprintf("%s%s", c.BaseURL, path)
	req, err := http.NewRequest(method, url, reqBody)
	if err != nil {
		return nil, 0, fmt.Errorf("failed to create request: %w", err)
	}

	req.Header.Set("Authorization", fmt.Sprintf("Bearer %s", c.Token))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json")

	resp, err := c.HTTPClient.Do(req)
	if err != nil {
		return nil, 0, fmt.Errorf("request failed: %w", err)
	}
	defer resp.Body.Close()

	respBody, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, resp.StatusCode, fmt.Errorf("failed to read response body: %w", err)
	}

	return respBody, resp.StatusCode, nil
}
