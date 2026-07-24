import logging
from typing import Dict, Optional

import httpx

from .auth import RetryTransport, TokenCache
from .exceptions import IamApiError, IamAuthError
from .models import LoginResponse, UserInfo

logger = logging.getLogger("iam_sdk")


class IamClient:
    """IAM System authentication client for users.

    Handles login, proactive token refresh, and logout against the IAM
    API. Uses httpx async client for all HTTP operations, retries
    transient failures with exponential backoff (via `RetryTransport`),
    and refreshes the access token automatically shortly before it
    expires.

    Usage:
        async with IamClient("https://localhost:5161") as client:
            response = await client.login("user@example.com", "password")
            print(response.access_token)
    """

    def __init__(
        self,
        base_url: str = "https://localhost:5161",
        transport: Optional[httpx.AsyncBaseTransport] = None,
    ):
        self.base_url = base_url.rstrip("/")
        self._client = httpx.AsyncClient(
            base_url=self.base_url, transport=transport or RetryTransport()
        )
        self._tokens = TokenCache()
        self._refresh_token: Optional[str] = None

    async def login(self, email: str, password: str) -> LoginResponse:
        """Authenticate with email and password.

        The API returns the access token in the response body and sets
        the refresh token as an HttpOnly cookie. We capture both.

        Args:
            email: User email address.
            password: User password.

        Returns:
            LoginResponse with access token and user information.

        Raises:
            IamAuthError: If the credentials are rejected (401).
            IamApiError: If the request fails for any other reason.
        """
        response = await self._client.post(
            "/api/auth/login",
            json={"email": email, "password": password},
        )
        if response.status_code == 401:
            raise IamAuthError("Login failed: invalid credentials")
        self._raise_for_status(response)
        data = response.json()

        self._tokens.set(data.get("accessToken", ""), data.get("expiresIn", 0))

        for cookie_name, cookie_value in response.cookies.items():
            if cookie_name == "refreshToken":
                self._refresh_token = cookie_value

        user = data.get("user", {})
        logger.debug("iam_sdk: login succeeded for %s", email)
        return LoginResponse(
            access_token=self._tokens.token or "",
            refresh_token=self._refresh_token or "",
            expires_in=data.get("expiresIn", 0),
            user_id=str(user.get("id", "")),
            email=user.get("email", ""),
            roles=user.get("roles", []),
        )

    async def refresh(self) -> LoginResponse:
        """Refresh the access token using the stored refresh token.

        The IAM API uses single-use refresh token rotation: each refresh
        call returns a new refresh token that replaces the old one.

        Returns:
            LoginResponse with a new access token.

        Raises:
            IamAuthError: If no refresh token is available, or refresh fails.
        """
        if not self._refresh_token:
            raise IamAuthError("No refresh token available. Call login() first.")

        self._client.cookies.set("refreshToken", self._refresh_token)

        response = await self._client.post("/api/auth/refresh")
        if response.status_code == 401:
            self._tokens.clear()
            self._refresh_token = None
            raise IamAuthError("Refresh failed: refresh token expired or revoked")
        self._raise_for_status(response)
        data = response.json()

        self._tokens.set(data.get("accessToken", ""), data.get("expiresIn", 0))

        for cookie_name, cookie_value in response.cookies.items():
            if cookie_name == "refreshToken":
                self._refresh_token = cookie_value

        logger.debug("iam_sdk: access token refreshed")
        return LoginResponse(
            access_token=self._tokens.token or "",
            refresh_token=self._refresh_token or "",
            expires_in=data.get("expiresIn", 0),
        )

    async def _ensure_fresh_token(self) -> None:
        """Proactively refresh the access token if it's about to expire.

        Called before authenticated requests so callers never have to
        manage token lifetime manually.
        """
        if self._tokens.is_expiring_soon() and self._refresh_token:
            await self.refresh()

    async def logout(self) -> None:
        """Logout and invalidate the current refresh token.

        Sends the refresh token cookie so the server can revoke it.
        Clears local token state regardless of server response.
        """
        if self._refresh_token:
            self._client.cookies.set("refreshToken", self._refresh_token)

        try:
            response = await self._client.post(
                "/api/auth/logout",
                headers=self.headers,
            )
            self._raise_for_status(response)
        finally:
            self._tokens.clear()
            self._refresh_token = None

    async def get_current_user(self) -> UserInfo:
        """Get the currently authenticated user's profile.

        Proactively refreshes the token first if it's about to expire.

        Returns:
            UserInfo with id, email, first_name, last_name.

        Raises:
            IamApiError: If the request fails (e.g. 401).
        """
        await self._ensure_fresh_token()
        response = await self._client.get(
            "/api/users/me",
            headers=self.headers,
        )
        self._raise_for_status(response)
        data = response.json()

        return UserInfo(
            id=str(data.get("id", "")),
            email=data.get("email", ""),
            first_name=data.get("firstName"),
            last_name=data.get("lastName"),
        )

    @property
    def is_authenticated(self) -> bool:
        """Check whether an access token is currently stored."""
        return self._tokens.token is not None

    @property
    def access_token(self) -> Optional[str]:
        """Return the current access token, or None."""
        return self._tokens.token

    @property
    def headers(self) -> Dict[str, str]:
        """Return an Authorization header dict for authenticated requests."""
        if self._tokens.token:
            return {"Authorization": f"Bearer {self._tokens.token}"}
        return {}

    def set_access_token(self, token: str, expires_in: int = 0) -> None:
        """Manually set the access token (e.g. from external storage)."""
        self._tokens.set(token, expires_in)

    @staticmethod
    def _raise_for_status(response: httpx.Response) -> None:
        try:
            response.raise_for_status()
        except httpx.HTTPStatusError as exc:
            try:
                body = response.json()
            except ValueError:
                body = response.text
            raise IamApiError(
                f"IAM API request failed: {exc}",
                status_code=response.status_code,
                response_body=body,
            ) from exc

    async def close(self) -> None:
        """Close the underlying HTTP client."""
        await self._client.aclose()

    async def __aenter__(self) -> "IamClient":
        return self

    async def __aexit__(self, *args: object) -> None:
        await self.close()
