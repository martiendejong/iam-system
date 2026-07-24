"""Exception hierarchy for the IAM System Python SDK."""

from typing import Any, Optional


class IamSdkError(Exception):
    """Base class for all errors raised by the IAM SDK."""


class IamAuthError(IamSdkError):
    """Raised when authentication or token handling fails."""


class TokenExpiredError(IamAuthError):
    """Raised when an access token has expired and no refresh is possible."""


class IamApiError(IamSdkError):
    """Raised when the IAM API returns an error response.

    Wraps the HTTP status code and, where available, the parsed error
    body so callers can branch on API-reported error codes without
    depending on httpx directly.
    """

    def __init__(
        self,
        message: str,
        status_code: Optional[int] = None,
        response_body: Optional[Any] = None,
    ):
        super().__init__(message)
        self.status_code = status_code
        self.response_body = response_body


class TelemetryConnectionError(IamSdkError):
    """Raised when the telemetry SignalR connection cannot be established
    or is lost without a successful automatic reconnect."""
