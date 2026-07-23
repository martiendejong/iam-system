"""pandas DataFrame helpers over the admin telemetry query/aggregate endpoints."""

from typing import Any, Optional

from ..admin import IamAdminClient
from ..models import TelemetryAggregationResult, TelemetryQueryResult


class TelemetryQueryResultFrame:
    """Wraps a `TelemetryQueryResult` with a `.to_dataframe()` convenience method."""

    def __init__(self, result: TelemetryQueryResult):
        self.result = result

    def to_dataframe(self) -> "Any":
        try:
            import pandas as pd
        except ImportError as exc:
            raise ImportError(
                "pandas is required for to_dataframe(); install with `pip install iam-sdk[jupyter]`"
            ) from exc
        return pd.DataFrame(self.result.records)

    def __len__(self) -> int:
        return len(self.result.records)


class TelemetryAggregationResultFrame:
    """Wraps a `TelemetryAggregationResult` with a `.to_dataframe()` convenience method."""

    def __init__(self, result: TelemetryAggregationResult):
        self.result = result

    def to_dataframe(self) -> "Any":
        try:
            import pandas as pd
        except ImportError as exc:
            raise ImportError(
                "pandas is required for to_dataframe(); install with `pip install iam-sdk[jupyter]`"
            ) from exc
        df = pd.DataFrame(self.result.buckets)
        df.attrs["metric_name"] = self.result.metric_name
        df.attrs["aggregation"] = self.result.aggregation
        df.attrs["interval"] = self.result.interval
        return df

    def __len__(self) -> int:
        return len(self.result.buckets)


class TelemetryQuery:
    """Data-science-friendly wrapper around `IamAdminClient` telemetry endpoints.

    Usage:
        query = TelemetryQuery(admin_client)
        frame = await query.query(device_id="sensor-001", metric_name="temperature")
        df = frame.to_dataframe()
    """

    def __init__(self, admin_client: IamAdminClient):
        self._admin = admin_client

    async def query(
        self,
        device_id: Optional[str] = None,
        metric_name: Optional[str] = None,
        tenant_id: Optional[str] = None,
        start_time: Optional[str] = None,
        end_time: Optional[str] = None,
        limit: int = 1000,
        order_by: str = "timestamp_desc",
    ) -> TelemetryQueryResultFrame:
        result = await self._admin.query_telemetry(
            device_id=device_id,
            metric_name=metric_name,
            tenant_id=tenant_id,
            start_time=start_time,
            end_time=end_time,
            limit=limit,
            order_by=order_by,
        )
        return TelemetryQueryResultFrame(result)

    async def aggregate(
        self,
        metric_name: str,
        device_id: Optional[str] = None,
        tenant_id: Optional[str] = None,
        aggregation: str = "avg",
        interval: str = "1h",
        start_time: Optional[str] = None,
        end_time: Optional[str] = None,
        group_by: Optional[str] = None,
    ) -> TelemetryAggregationResultFrame:
        result = await self._admin.aggregate_telemetry(
            metric_name=metric_name,
            device_id=device_id,
            tenant_id=tenant_id,
            aggregation=aggregation,
            interval=interval,
            start_time=start_time,
            end_time=end_time,
            group_by=group_by,
        )
        return TelemetryAggregationResultFrame(result)
