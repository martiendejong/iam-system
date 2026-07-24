"""Shared authentication helpers: token caching with proactive refresh,
and retry-with-backoff for transient HTTP failures.
"""

import logging
import time
from typing import Any, Optional

import httpx
from tenacity import (
    AsyncRetrying,
    retry,
    retry_if_exception,
    stop_after_attempt,
    wait_random_exponential,
)

from .exceptions import IamAuthError

logger = logging.getLogger("iam_sdk")

# Refresh this many seconds before the token's actual expiry, so a request
# in flight never races a token that dies mid-call.
DEFAULT_REFRESH_MARGIN_SECONDS = 30


class TokenCache:
    """Caches an access token and its expiry, refreshing proactively.

    Usage:
        cache = TokenCache()
        cache.set(access_token, expires_in=900)
        if cache.is_expiring_soon():
            # refresh before making the next call
            ...
    """

    def __init__(self, refresh_margin_seconds: int = DEFAULT_REFRESH_MARGIN_SECONDS):
        self.refresh_margin_seconds = refresh_margin_seconds
        self._token: Optional[str] = None
        self._expires_at: Optional[float] = None

    def set(self, token: str, expires_in: int) -> None:
        """Store a new token and compute its absolute expiry time."""
        self._token = token
        self._expires_at = time.monotonic() + expires_in if expires_in else None

    def clear(self) -> None:
        self._token = None
        self._expires_at = None

    @property
    def token(self) -> Optional[str]:
        return self._token

    def is_expiring_soon(self) -> bool:
        """True if there's no token, or it expires within the refresh margin."""
        if self._token is None:
            return True
        if self._expires_at is None:
            return False
        return time.monotonic() >= (self._expires_at - self.refresh_margin_seconds)


def _is_retryable(exc: BaseException) -> bool:
    """Retry on connection-level failures and 5xx server errors.

    4xx client errors (bad credentials, not found, etc.) are not
    retryable -- retrying them just repeats the same failure.
    """
    if isinstance(exc, (httpx.ConnectError, httpx.ReadTimeout, httpx.WriteTimeout, httpx.PoolTimeout)):
        return True
    if isinstance(exc, httpx.HTTPStatusError):
        return exc.response.status_code >= 500
    return False


def with_retry(max_attempts: int = 3) -> Any:
    """Decorator applying exponential backoff with jitter to transient failures.

    Wraps `tenacity.retry` with IAM SDK's chosen policy: retry connection
    errors and 5xx responses, leave 4xx alone, cap attempts, log each retry.
    Most SDK clients get retry coverage for free via `RetryTransport`
    instead -- this decorator is exposed for callers wrapping their own
    functions with the same policy.
    """

    def before_sleep(retry_state: Any) -> None:
        exc = retry_state.outcome.exception() if retry_state.outcome else None
        logger.warning(
            "iam_sdk: retrying %s after error %r (attempt %d/%d)",
            getattr(retry_state.fn, "__name__", "call"),
            exc,
            retry_state.attempt_number,
            max_attempts,
        )

    return retry(
        reraise=True,
        stop=stop_after_attempt(max_attempts),
        wait=wait_random_exponential(multiplier=0.5, max=8),
        retry=retry_if_exception(_is_retryable),
        before_sleep=before_sleep,
    )


class RetryTransport(httpx.AsyncBaseTransport):
    """An httpx transport that retries transient failures with backoff+jitter.

    Wraps the default `httpx.AsyncHTTPTransport`, retrying connection-level
    errors and 5xx responses (never 4xx, since retrying a bad request or
    bad credentials just repeats the same failure). Applying retry at the
    transport layer means every request made through an `httpx.AsyncClient`
    configured with this transport gets the same policy, without having to
    decorate each SDK method individually.
    """

    def __init__(self, verify: bool = False, max_attempts: int = 3):
        self._transport = httpx.AsyncHTTPTransport(verify=verify)
        self._max_attempts = max_attempts

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        attempt = 0
        async for attempt_state in AsyncRetrying(
            reraise=True,
            stop=stop_after_attempt(self._max_attempts),
            wait=wait_random_exponential(multiplier=0.5, max=8),
            retry=retry_if_exception(_is_retryable),
        ):
            with attempt_state:
                attempt += 1
                response = await self._transport.handle_async_request(request)
                if response.status_code >= 500:
                    await response.aread()
                    logger.warning(
                        "iam_sdk: %s %s -> %d, retrying (attempt %d/%d)",
                        request.method,
                        request.url,
                        response.status_code,
                        attempt,
                        self._max_attempts,
                    )
                    response.raise_for_status()
                return response
        raise IamAuthError("unreachable")  # pragma: no cover - loop always returns or raises

    async def aclose(self) -> None:
        await self._transport.aclose()
