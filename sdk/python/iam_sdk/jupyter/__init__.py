"""Jupyter notebook integration for the IAM System Python SDK.

Requires the `jupyter` extra (`pip install iam-sdk[jupyter]`), which
pulls in pandas for DataFrame output.
"""

from .context import IAMContext
from .display import device_status_table, telemetry_chart
from .query import TelemetryQuery

__all__ = [
    "IAMContext",
    "TelemetryQuery",
    "device_status_table",
    "telemetry_chart",
]
