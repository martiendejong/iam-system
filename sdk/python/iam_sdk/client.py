import httpx
from typing import Optional

from .models import LoginResponse, UserInfo


class IamClient:
    """IAM System authentication client for users.

    Handles login, token refresh, and logout against the IAM API.
    Uses httpx async client for all HTTP operations.

    Usage:
        async with IamClient("https://localhost:5161") as client:
            response = await client.login("user@example.com", "password")
            print(response.access_token)
    """

    def __init__(self, base_url: str = "https://localhost:5161"):
        self.base_url = base_url.rstrip("/")
        self._client = httpx.AsyncClient(base_url=self.base_url, verify=False)
        self._access_token: Optional[str] = None
        self._refresh_token: Optional[str] = None

    async def login(self, email: str, password: str) -> LoginResponse:
        """Authenticate with email and password.

        The API returns the access token in the response body and sets
        the refresh token as an HttpOnly cookie.  We capture both.

        Args:
            email: User email address.
            password: User password.

        Returns:
            LoginResponse with access token and user information.

        Raises:
            httpx.HTTPStatusError: If the login request fails.
        """
        response = await self._client.post(
            "/api/auth/login",
            json={"email": email, "password": password},
        )
        response.raise_for_status()
        data = response.json()

        self._access_token = data.get("accessToken")

        # The refresh token is set as an HttpOnly cookie by the server.
        # httpx captures cookies automatically on the client instance.
        # Also extract from the Set-Cookie header for explicit storage.
        for cookie_name, cookie_value in response.cookies.items():
            if cookie_name == "refreshToken":
                self._refresh_token = cookie_value

        user = data.get("user", {})
        return LoginResponse(
            access_token=self._access_token or "",
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
            httpx.HTTPStatusError: If the refresh request fails.
            ValueError: If no refresh token is available.
        """
        if not self._refresh_token:
            raise ValueError(
                "No refresh token available. Call login() first."
            )

        # Send the refresh token as a cookie, matching the API expectation.
        self._client.cookies.set("refreshToken", self._refresh_token)

        response = await self._client.post("/api/auth/refresh")
        response.raise_for_status()
        data = response.json()

        self._access_token = data.get("accessToken")

        # Capture the rotated refresh token from cookies.
        for cookie_name, cookie_value in response.cookies.items():
            if cookie_name == "refreshToken":
                self._refresh_token = cookie_value

        return LoginResponse(
            access_token=self._access_token or "",
            refresh_token=self._refresh_token or "",
            expires_in=data.get("expiresIn", 0),
        )

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
            response.raise_for_status()
        finally:
            self._access_token = None
            self._refresh_token = None

    async def get_current_user(self) -> UserInfo:
        """Get the currently authenticated user's profile.

        Returns:
            UserInfo with id, email, first_name, last_name.

        Raises:
            httpx.HTTPStatusError: If the request fails (e.g. 401).
        """
        response = await self._client.get(
            "/api/users/me",
            headers=self.headers,
        )
        response.raise_for_status()
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
        return self._access_token is not None

    @property
    def access_token(self) -> Optional[str]:
        """Return the current access token, or None."""
        return self._access_token

    @property
    def headers(self) -> dict:
        """Return an Authorization header dict for authenticated requests."""
        if self._access_token:
            return {"Authorization": f"Bearer {self._access_token}"}
        return {}

    def set_access_token(self, token: str) -> None:
        """Manually set the access token (e.g. from external storage)."""
        self._access_token = token

    async def close(self) -> None:
        """Close the underlying HTTP client."""
        await self._client.aclose()

    async def __aenter__(self) -> "IamClient":
        return self

    async def __aexit__(self, *args: object) -> None:
        await self.close()
