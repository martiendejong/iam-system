"""Rich display helpers for notebook output: device status tables and
telemetry charts, built on pandas so they render nicely inline."""

from typing import Any, Dict, Iterable, List, Optional

from ..models import Device


def device_status_table(devices: Iterable[Device]) -> "Any":
    """Return a pandas DataFrame summarizing device status, for direct
    display in a notebook cell (Jupyter renders a DataFrame as an HTML
    table automatically when it's the last expression in a cell)."""
    try:
        import pandas as pd
    except ImportError as exc:
        raise ImportError(
            "pandas is required for device_status_table(); install with `pip install iam-sdk[jupyter]`"
        ) from exc

    rows: List[Dict[str, Any]] = [
        {
            "device_id": d.device_id,
            "name": d.name,
            "type": d.device_type,
            "tenant": d.tenant_name,
            "online": d.is_online,
            "active": d.is_active,
            "last_seen": d.last_seen_at,
        }
        for d in devices
    ]
    return pd.DataFrame(rows)


def telemetry_chart(
    records: Iterable[Dict[str, Any]],
    metric_name: Optional[str] = None,
    x: str = "timestamp",
    y: str = "numeric_value",
    title: Optional[str] = None,
) -> "Any":
    """Render a simple line chart of telemetry records (via pandas'
    built-in matplotlib backend) and return the resulting Axes.

    Falls back to returning the filtered DataFrame if matplotlib isn't
    installed, so it still works without a display backend.
    """
    try:
        import pandas as pd
    except ImportError as exc:
        raise ImportError(
            "pandas is required for telemetry_chart(); install with `pip install iam-sdk[jupyter]`"
        ) from exc

    df = pd.DataFrame(list(records))
    if metric_name and "metric_name" in df.columns:
        df = df[df["metric_name"] == metric_name]
    if x in df.columns:
        df = df.sort_values(x)

    try:
        return df.plot(x=x, y=y, marker="o", title=title or metric_name or "Telemetry")
    except ImportError:
        # matplotlib not installed -- return the data itself so the
        # notebook still shows something useful.
        return df
