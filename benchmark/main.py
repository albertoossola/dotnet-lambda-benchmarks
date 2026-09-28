#!/usr/bin/env python3
"""Invoke the deployed Lambda benchmarks and graph their EMF performance metrics."""

from __future__ import annotations

import argparse
import base64
import csv
import html
import json
import re
import sys
import time
import uuid
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import boto3

METRICS = (
    "DurationMs",
    "InitDurationMs",
    "RestoreDurationMs",
    "ColdStartMs",
    "WarmStartMs",
    "MemoryWorkingSetMb",
    "ManagedHeapMb",
)


@dataclass(frozen=True)
class Target:
    name: str
    arn: str
    example: str
    deployment_type: str


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--stack", default="besharp-dotnet-lambda", help="CloudFormation stack name"
    )
    parser.add_argument(
        "--region", default=None, help="AWS region; defaults to the SDK configuration"
    )
    parser.add_argument(
        "--repetitions",
        type=int,
        default=3,
        help="Invocations per function (default: 3)",
    )
    parser.add_argument(
        "--iterations", type=int, default=10, help="Image encodes per image invocation"
    )
    parser.add_argument(
        "--parallelism",
        type=int,
        default=1,
        help="Image encode parallelism (default: 1)",
    )
    parser.add_argument(
        "--latitude", type=float, default=41.9028, help="Lookup latitude"
    )
    parser.add_argument(
        "--longitude", type=float, default=12.4964, help="Lookup longitude"
    )
    parser.add_argument(
        "--radius-km", type=float, default=25, help="Lookup radius in kilometers"
    )
    cold_start = parser.add_mutually_exclusive_group()
    cold_start.add_argument(
        "--cold-start",
        dest="cold_start",
        action="store_true",
        help="Publish and invoke a fresh version of each function (default)",
    )
    cold_start.add_argument(
        "--no-cold-start",
        dest="cold_start",
        action="store_false",
        help="Use the existing live aliases instead of publishing fresh versions",
    )
    parser.set_defaults(cold_start=True)
    parser.add_argument(
        "--seed-dynamo",
        action="store_true",
        help="Insert item-001 before invoking DynamoDB functions",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=Path("benchmark-results"),
        help="Output directory",
    )
    return parser.parse_args()


def clients(region: str | None) -> tuple[Any, Any]:
    session = boto3.Session(region_name=region)
    return (
        session.client("cloudformation"),
        session.client("lambda"),
    )


def stack_outputs(cloudformation: Any, stack_name: str) -> dict[str, str]:
    response = cloudformation.describe_stacks(StackName=stack_name)
    stacks = response.get("Stacks", [])
    if not stacks:
        raise RuntimeError(f"Stack not found: {stack_name}")
    return {
        item["OutputKey"]: item["OutputValue"] for item in stacks[0].get("Outputs", [])
    }


def targets(outputs: dict[str, str]) -> list[Target]:
    definitions = (
        ("DynamoQueryClrAliasArn", "dynamo-query", "StandardCLR"),
        ("DynamoQuerySnapStartAliasArn", "dynamo-query", "SnapStart"),
        ("DynamoQueryNativeAotAliasArn", "dynamo-query", "NativeAOT"),
        ("ImageProcessingClrAliasArn", "image-processing", "StandardCLR"),
        ("ImageProcessingSnapStartAliasArn", "image-processing", "SnapStart"),
        ("ImageProcessingNativeAotAliasArn", "image-processing", "NativeAOT"),
        ("CsvProcessingClrAliasArn", "csv-processing", "StandardCLR"),
        ("CsvProcessingSnapStartAliasArn", "csv-processing", "SnapStart"),
        ("CsvProcessingNativeAotAliasArn", "csv-processing", "NativeAOT"),
        ("GeoSpatialLookupClrAliasArn", "geospatial-lookup", "StandardCLR"),
        ("GeoSpatialLookupSnapStartAliasArn", "geospatial-lookup", "SnapStart"),
        ("GeoSpatialLookupNativeAotAliasArn", "geospatial-lookup", "NativeAOT"),
    )
    missing = [key for key, _, _ in definitions if key not in outputs]
    if missing:
        raise RuntimeError(f"Missing stack outputs: {', '.join(missing)}")
    return [
        Target(key.removesuffix("AliasArn"), outputs[key], example, deployment)
        for key, example, deployment in definitions
    ]


def seed_dynamo(dynamodb: Any, table_name: str) -> None:
    dynamodb.put_item(
        TableName=table_name,
        Item={
            "pk": {"S": "item-001"},
            "sk": {"S": "item-001"},
            "id": {"S": "item-001"},
            "name": {"S": "benchmark item"},
            "description": {"N": "1"},
        },
    )


def publish_fresh_version(lambda_client: Any, target: Target) -> str:
    function_arn = target.arn.rsplit(":", 1)[0]
    configuration = lambda_client.get_function_configuration(FunctionName=function_arn)
    environment = dict(configuration.get("Environment", {}).get("Variables", {}))
    environment["BENCHMARK_FORCE_COLD_START"] = uuid.uuid4().hex
    lambda_client.update_function_configuration(
        FunctionName=function_arn,
        Environment={"Variables": environment},
    )
    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        configuration = lambda_client.get_function_configuration(
            FunctionName=function_arn
        )
        if configuration.get("LastUpdateStatus") == "Successful":
            break
        if configuration.get("LastUpdateStatus") == "Failed":
            raise RuntimeError(
                f"Configuration update for {target.name} failed: {configuration.get('LastUpdateStatusReason')}"
            )
        time.sleep(5)
    else:
        raise TimeoutError(f"Timed out waiting for {target.name} configuration update")
    version = lambda_client.publish_version(
        FunctionName=function_arn,
        Description=f"benchmark cold start {datetime.now(timezone.utc).isoformat()}",
    )["Version"]
    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        configuration = lambda_client.get_function_configuration(
            FunctionName=function_arn,
            Qualifier=version,
        )
        if configuration.get("State") == "Active":
            if target.deployment_type != "SnapStart":
                return version
            optimization_status = configuration.get("SnapStart", {}).get(
                "OptimizationStatus"
            )
            if optimization_status == "On":
                return version
            if optimization_status == "Off":
                raise RuntimeError(
                    f"SnapStart optimization for {target.name} version {version} is off"
                )
        if configuration.get("State") == "Failed":
            raise RuntimeError(
                f"Version {version} for {target.name} failed: {configuration.get('StateReason')}"
            )
        time.sleep(5)
    raise TimeoutError(
        f"Timed out waiting for {target.name} version {version} to become active"
    )


def live_version(lambda_client: Any, target: Target) -> str:
    function_arn, alias_name = target.arn.rsplit(":", 1)
    return lambda_client.get_alias(
        FunctionName=function_arn,
        Name=alias_name,
    )["FunctionVersion"]


def function_memory_mb(
    lambda_client: Any, target: Target, qualifier: str | None
) -> int | None:
    function_arn = target.arn.rsplit(":", 1)[0]
    configuration = lambda_client.get_function_configuration(
        FunctionName=function_arn,
        **({"Qualifier": qualifier} if qualifier else {}),
    )
    return configuration.get("MemorySize")


def payload_for(
    target: Target,
    outputs: dict[str, str],
    iterations: int,
    parallelism: int,
    latitude: float,
    longitude: float,
    radius_km: float,
) -> dict[str, Any]:
    if target.example == "dynamo-query":
        return {"id": "item-001"}
    if target.example == "csv-processing":
        return {
            "bucket": outputs["CsvProcessingSourceBucketName"],
            "key": "sample.csv",
        }
    if target.example == "geospatial-lookup":
        return {"latitude": latitude, "longitude": longitude, "radiusKm": radius_km}
    return {
        "bucket": outputs["ImageProcessingSourceBucketName"],
        "key": "sample.jpg",
        "iterations": iterations,
        "parallelism": parallelism,
    }


def emf_records(log_result: str) -> list[dict[str, Any]]:
    records = []
    decoder = json.JSONDecoder()
    for line in log_result.splitlines():
        start = line.find('{"_aws"')
        if start < 0:
            continue
        try:
            value, _ = decoder.raw_decode(line[start:])
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict) and "_aws" in value and "DurationMs" in value:
            records.append(value)
    return records


def platform_metrics(log_result: str) -> dict[str, float]:
    metrics: dict[str, float] = {}
    for metric, label in (
        ("InitDurationMs", "Init Duration"),
        ("RestoreDurationMs", "Restore Duration"),
    ):
        match = re.search(rf"{re.escape(label)}:\s*([0-9.]+) ms", log_result)
        if match:
            metrics[metric] = float(match.group(1))
    return metrics


def invoke(
    lambda_client: Any,
    target: Target,
    qualifier: str | None,
    payload: dict[str, Any],
) -> tuple[list[dict[str, Any]], str | None]:
    request: dict[str, Any] = {
        "FunctionName": target.arn
        if qualifier is None
        else target.arn.rsplit(":", 1)[0],
        "InvocationType": "RequestResponse",
        "LogType": "Tail",
        "Payload": json.dumps(payload).encode(),
    }
    if qualifier:
        request["Qualifier"] = qualifier
    response = lambda_client.invoke(**request)
    logs = base64.b64decode(response.get("LogResult", "")).decode(
        "utf-8", errors="replace"
    )
    error = response.get("FunctionError")
    if error:
        response_payload: Any = response.get("Payload")
        details = (
            response_payload.read().decode("utf-8", errors="replace")
            if response_payload
            else ""
        )
        error = f"{error}: {details}" if details else error
    records = emf_records(logs)
    platform = platform_metrics(logs)
    if platform:
        records.append(platform)
    return records, error


def write_csv(path: Path, rows: list[dict[str, Any]]) -> None:
    fields = [
        "function",
        "deployment_type",
        "example",
        "invocation",
        "phase",
        "memory_mb",
        "metric",
        "value",
    ]
    with path.open("w", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)


def write_html(path: Path, rows: list[dict[str, Any]]) -> None:
    workloads = list(dict.fromkeys(row["example"] for row in rows))
    generated_at = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    chart_rows = list(rows)
    by_invocation: dict[tuple[Any, ...], dict[str, Any]] = {}
    for row in rows:
        key = (
            row["function"],
            row["deployment_type"],
            row["example"],
            row["invocation"],
            row.get("phase"),
            row.get("memory_mb"),
        )
        if row["metric"] in ("DurationMs", "InitDurationMs", "RestoreDurationMs"):
            by_invocation.setdefault(key, {})[row["metric"]] = float(row["value"])
    chart_rows.extend(
        {
            "function": key[0],
            "deployment_type": key[1],
            "example": key[2],
            "invocation": key[3],
            "phase": key[4],
            "memory_mb": key[5],
            "metric": "TotalExecutionMs",
            "value": metrics["DurationMs"]
            + metrics.get("InitDurationMs", 0.0)
            + metrics.get("RestoreDurationMs", 0.0),
        }
        for key, metrics in by_invocation.items()
        if "DurationMs" in metrics
    )

    def text(value: object) -> str:
        return html.escape(str(value))

    metric_options = (
        '<option value="TotalExecutionMs" selected>Total execution time</option>'
        + "".join(
            f'<option value="{text(metric)}">{text(metric)}</option>'
            for metric in METRICS
        )
    )

    def format_value(metric: str, value: float) -> str:
        unit = " MB" if metric.endswith("Mb") else " ms"
        return f"{value:,.1f}{unit}"

    def median(values: list[float]) -> float | None:
        return values[(len(values) - 1) // 2] if values else None

    def memory_text(memory_mb: str | int | float | None) -> str:
        return f" · {int(float(memory_mb))} MB" if memory_mb not in (None, "") else ""

    def phase_values(
        function_rows: list[dict[str, Any]], metric: str, phase: str
    ) -> list[float]:
        # Only invocation 1 of a fresh publish is "cold"; mixing it with warm samples in a
        # single median is what made SnapStart look artificially slow in the raw duration.
        return sorted(
            float(row["value"])
            for row in function_rows
            if row["metric"] == metric and row.get("phase") == phase
        )

    def cold_total_durations(function_rows: list[dict[str, Any]]) -> list[float]:
        # What the caller actually waited for on a cold invocation is Init/Restore
        # (platform overhead paid before the handler runs) plus the handler's own
        # DurationMs — not DurationMs alone, which only covers the handler body.
        by_invocation: dict[Any, dict[str, float]] = {}
        for row in function_rows:
            if row.get("phase") != "cold":
                continue
            if row["metric"] not in (
                "DurationMs",
                "InitDurationMs",
                "RestoreDurationMs",
            ):
                continue
            by_invocation.setdefault(row["invocation"], {})[row["metric"]] = float(
                row["value"]
            )
        totals = [
            metrics["DurationMs"]
            + metrics.get("InitDurationMs", 0.0)
            + metrics.get("RestoreDurationMs", 0.0)
            for metrics in by_invocation.values()
            if "DurationMs" in metrics
        ]
        return sorted(totals)

    sections: list[str] = []
    for workload in workloads:
        workload_rows = [row for row in rows if row["example"] == workload]
        functions = list(dict.fromkeys(row["function"] for row in workload_rows))
        summaries: list[str] = []
        for function in functions:
            function_rows = [
                row for row in workload_rows if row["function"] == function
            ]
            warm_p50 = median(phase_values(function_rows, "DurationMs", "warm"))
            cold_p50 = median(cold_total_durations(function_rows))
            deployment = function_rows[0]["deployment_type"]
            memory_label = memory_text(function_rows[0].get("memory_mb"))
            warm_text = (
                format_value("DurationMs", warm_p50) if warm_p50 is not None else "—"
            )
            cold_text = (
                format_value("DurationMs", cold_p50) if cold_p50 is not None else "—"
            )
            # Always emit one card per function so the badge count below never drifts
            # from what's actually on screen, even when a phase has no samples yet.
            summaries.append(
                f'<article class="summary"><span class="deployment">{text(deployment)}{text(memory_label)}</span>'
                f'<div class="summary-stats">'
                f"<div><strong>{warm_text}</strong><small>warm p50</small></div>"
                f"<div><strong>{cold_text}</strong><small>cold total (launch + run)</small></div>"
                f"</div></article>"
            )

        header_labels = [
            label
            for metric in METRICS
            for label in (
                ("DurationMs (warm)", "DurationMs (cold total)")
                if metric == "DurationMs"
                else (metric,)
            )
        ]
        headers = "".join(f"<th>{text(label)}</th>" for label in header_labels)
        table_rows: list[str] = []
        for function in functions:
            function_rows = [
                row for row in workload_rows if row["function"] == function
            ]
            cells: list[str] = []
            for metric in METRICS:
                if metric == "DurationMs":
                    warm_value = median(phase_values(function_rows, metric, "warm"))
                    cold_value = median(cold_total_durations(function_rows))
                    cells.append(
                        f"<td>{format_value(metric, warm_value) if warm_value is not None else '—'}</td>"
                    )
                    cells.append(
                        f"<td>{format_value(metric, cold_value) if cold_value is not None else '—'}</td>"
                    )
                else:
                    metric_value = median(
                        sorted(
                            float(row["value"])
                            for row in function_rows
                            if row["metric"] == metric
                        )
                    )
                    cells.append(
                        f"<td>{format_value(metric, metric_value) if metric_value is not None else '—'}</td>"
                    )
            memory_cell = function_rows[0].get("memory_mb")
            memory_cell_text = (
                f"{int(float(memory_cell))}" if memory_cell not in (None, "") else "—"
            )
            table_rows.append(
                f'<tr><th scope="row">{text(function)}</th><td>{text(function_rows[0]["deployment_type"])}</td>'
                f"<td>{text(memory_cell_text)}</td>{''.join(cells)}</tr>"
            )

        sections.append(
            f'<section class="workload"><div class="section-heading"><div>'
            f'<p class="eyebrow">Workload</p><h2>{text(workload)}</h2></div>'
            f'<span class="count">{len(table_rows)} deployment targets</span></div>'
            f'<div class="summaries">{"".join(summaries)}</div>'
            f'<div class="chart"><div class="chart-heading"><h3>Metric trend</h3>'
            f'<label>Metric <select class="metric-select" data-workload="{text(workload)}">'
            f"{metric_options}"
            f'</select></label></div><div class="chart-canvas"><canvas data-chart="{text(workload)}"></canvas></div></div>'
            f'<div class="table-wrap"><table><thead><tr><th>Function</th><th>Deployment</th><th>Memory (MB)</th>{headers}</tr></thead>'
            f"<tbody>{''.join(table_rows)}</tbody></table></div></section>"
        )

    data_json = json.dumps(chart_rows, separators=(",", ":")).replace("<", "\\u003c")
    report = f"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>.NET Lambda benchmark results</title>
<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.4/dist/chart.umd.min.js"></script>
<style>
@import url('https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;500;600;700&family=Space+Grotesk:wght@400;500;600;700&display=swap');
:root {{ color-scheme:dark; --ink:#f6f1ff; --muted:#b4a8c9; --line:#3b2d50; --paper:#100c16; --card:#1a1323; --accent:#c084fc; --accent-light:#302047; }}
* {{ box-sizing:border-box; }} body {{ margin:0; background:radial-gradient(circle at 80% -10%,#43205f 0,#100c16 46%); color:var(--ink); font:15px/1.5 'Space Grotesk',ui-sans-serif,sans-serif; }}
main {{ max-width:1180px; margin:auto; padding:48px 24px 72px; }} header {{ display:flex; justify-content:space-between; gap:24px; align-items:end; border-bottom:1px solid var(--line); padding-bottom:28px; margin-bottom:32px; }}
h1,h2,p {{ margin:0; }} h1 {{ font:700 clamp(2rem,5vw,4rem)/.98 'Space Grotesk',ui-sans-serif,sans-serif; letter-spacing:0; }} h2 {{ font:700 1.7rem 'Space Grotesk',ui-sans-serif,sans-serif; }} .dek {{ color:var(--muted); margin-top:12px; }} .stamp {{ color:var(--muted); white-space:nowrap; font-size:.85rem; }}
.workload {{ background:color-mix(in srgb,var(--card) 94%,white 6%); border:1px solid var(--line); margin:24px 0; box-shadow:0 16px 42px #0008; }} .section-heading {{ display:flex; justify-content:space-between; align-items:center; gap:16px; padding:24px 26px 18px; }}
.eyebrow {{ color:var(--accent); font-size:.72rem; font-weight:800; letter-spacing:.12em; text-transform:uppercase; }} .count {{ color:var(--muted); font-size:.85rem; }} .summaries {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); gap:10px; padding:0 26px 24px; }}
.summary {{ background:var(--accent-light); border-left:4px solid var(--accent); padding:14px 16px; }} .deployment {{ display:block; color:var(--muted); font-size:.78rem; }} .summary-stats {{ display:flex; gap:16px; margin-top:6px; }} .summary-stats > div {{ flex:1; min-width:0; }} .summary-stats strong {{ display:block; font:700 1.25rem 'IBM Plex Mono',ui-monospace,monospace; margin:2px 0; }} .summary-stats small {{ display:block; color:var(--muted); font-size:.72rem; }}
.chart {{ border-top:1px solid var(--line); padding:20px 26px 24px; }} .chart-heading {{ display:flex; justify-content:space-between; align-items:center; gap:16px; margin-bottom:12px; }} h3 {{ font-size:1rem; margin:0; }} label {{ color:var(--muted); font-size:.82rem; }} select {{ border:1px solid var(--line); border-radius:4px; background:#100c16; color:var(--ink); padding:7px 28px 7px 9px; font:inherit; }} .chart-canvas {{ position:relative; height:280px; }}
.table-wrap {{ overflow-x:auto; border-top:1px solid var(--line); }} table {{ border-collapse:collapse; width:100%; min-width:760px; }} th,td {{ padding:12px 14px; border-bottom:1px solid var(--line); text-align:right; white-space:nowrap; }} th:first-child,td:first-child {{ text-align:left; }} thead th {{ color:var(--muted); font-size:.72rem; letter-spacing:.06em; text-transform:uppercase; background:#17101f; }} tbody th {{ font-weight:650; }} tbody tr:last-child th,tbody tr:last-child td {{ border-bottom:0; }} tbody tr:hover {{ background:#271936; }}
@media (max-width:650px) {{ main {{ padding:28px 14px 48px; }} header {{ display:block; }} .stamp {{ display:block; margin-top:18px; }} .section-heading {{ padding:20px 16px 14px; }} .summaries,.chart {{ padding-left:16px; padding-right:16px; }} .chart-heading {{ align-items:flex-start; flex-direction:column; }} }}
</style></head><body><main>
<header><div><p class="eyebrow">Benchmark report</p><h1>.NET Lambda results</h1><p class="dek">Median performance grouped by workload and deployment strategy.</p></div><div class="stamp">Generated {text(generated_at)}<br>{len(rows)} metric observations</div></header>
{"".join(sections)}
</main><script>
const rows = {data_json};
const palette = ["#c084fc", "#ffd166", "#ff7b9c", "#70d6ff", "#b8f28b"];
const metricUnits = {{
        TotalExecutionMs: "ms",
    DurationMs: "ms", InitDurationMs: "ms", RestoreDurationMs: "ms",
    ColdStartMs: "ms", WarmStartMs: "ms", MemoryWorkingSetMb: "MB", ManagedHeapMb: "MB",
}};

Chart.defaults.color = "#b4a8c9";
Chart.defaults.borderColor = "#3b2d50";
Chart.defaults.font.family = "'IBM Plex Mono', ui-monospace, monospace";

function datasetsFor(workload, metric) {{
    const workloadRows = rows.filter(row => row.example === workload && row.metric === metric);
    const functions = [...new Set(workloadRows.map(row => row.function))];
    return functions.map((functionName, index) => {{
        const color = palette[index % palette.length];
        const label = functionName.replace(workload.replaceAll("-", " "), "").trim() || functionName;
        return {{
            label,
            data: workloadRows
                .filter(row => row.function === functionName)
                .sort((a, b) => a.invocation - b.invocation)
                .map(row => ({{ x: Number(row.invocation), y: Number(row.value) }})),
            borderColor: color,
            backgroundColor: color,
            pointRadius: 3,
            tension: 0.2,
        }};
    }});
}}

function renderCharts() {{
    document.querySelectorAll(".metric-select").forEach(select => {{
        const canvas = document.querySelector(`[data-chart="${{CSS.escape(select.dataset.workload)}}"]`);
        const chart = new Chart(canvas, {{
            type: "line",
            data: {{ datasets: datasetsFor(select.dataset.workload, select.value) }},
            options: {{
                responsive: true,
                maintainAspectRatio: false,
                interaction: {{ mode: "nearest", intersect: false }},
                scales: {{
                    x: {{ type: "linear", title: {{ display: true, text: "Invocation" }}, ticks: {{ stepSize: 1, precision: 0 }} }},
                    y: {{ beginAtZero: true, title: {{ display: true, text: metricUnits[select.value] }} }},
                }},
                plugins: {{ legend: {{ position: "bottom" }} }},
            }},
        }});
        select.addEventListener("change", () => {{
            chart.data.datasets = datasetsFor(select.dataset.workload, select.value);
            chart.options.scales.y.title.text = metricUnits[select.value];
            chart.update();
        }});
    }});
}}
renderCharts();
</script></body></html>"""
    path.write_text(report, encoding="utf-8")


def plot(path: Path, rows: list[dict[str, Any]]) -> None:
    import matplotlib.pyplot as plt

    functions = list(dict.fromkeys(row["function"] for row in rows))
    colors = {function: index for index, function in enumerate(functions)}
    figure, axes = plt.subplots(
        (len(METRICS) + 1) // 2, 2, figsize=(16, 17), constrained_layout=True
    )
    axes = axes.ravel()
    for axis, metric in zip(axes, METRICS):
        for function in functions:
            values = [
                row["value"]
                for row in rows
                if row["function"] == function and row["metric"] == metric
            ]
            if values:
                axis.plot(
                    range(1, len(values) + 1),
                    values,
                    "o-",
                    label=function,
                    color=f"C{colors[function]}",
                )
        axis.set_title(metric)
        axis.set_xlabel("Recorded invocation")
        axis.set_ylabel("MB" if metric.endswith("Mb") else "milliseconds")
        axis.grid(alpha=0.25)
    axes[-1].axis("off")
    handles, labels = axes[0].get_legend_handles_labels()
    figure.legend(handles, labels, loc="lower center", ncol=3)
    figure.suptitle(".NET Lambda benchmark performance metrics")
    figure.savefig(path, dpi=160, bbox_inches="tight")
    plt.close(figure)


def run_target(
    lambda_client: Any,
    target: Target,
    outputs: dict[str, str],
    repetitions: int,
    iterations: int,
    parallelism: int,
    latitude: float,
    longitude: float,
    radius_km: float,
    cold_start: bool,
) -> list[dict[str, Any]]:
    if cold_start:
        qualifier = publish_fresh_version(lambda_client, target)
    elif target.deployment_type == "SnapStart":
        qualifier = live_version(lambda_client, target)
    else:
        qualifier = None
    memory_mb = function_memory_mb(lambda_client, target, qualifier)
    destination = f"{target.name} ({qualifier or 'live alias'})"
    print(f"Invoking {destination}...", flush=True)

    rows: list[dict[str, Any]] = []
    for invocation_number in range(1, repetitions + 1):
        payload = payload_for(
            target, outputs, iterations, parallelism, latitude, longitude, radius_km
        )
        records, error = invoke(lambda_client, target, qualifier, payload)
        if error:
            print(
                f"  {target.name} invocation {invocation_number}: Lambda error ({error})",
                file=sys.stderr,
            )
        if not records and not error:
            print(
                f"  {target.name} invocation {invocation_number}: no EMF metrics found",
                file=sys.stderr,
            )
        # Only invocation 1 of a freshly published version is a true cold start; later
        # invocations in the loop reuse the same execution environment. Tagging the phase
        # keeps a single unwarmed cold sample from skewing the steady-state comparison.
        phase = "cold" if cold_start and invocation_number == 1 else "warm"
        for record in records:
            for metric in METRICS:
                if metric in record:
                    rows.append(
                        {
                            "function": target.name,
                            "deployment_type": target.deployment_type,
                            "example": target.example,
                            "invocation": invocation_number,
                            "phase": phase,
                            "memory_mb": memory_mb,
                            "metric": metric,
                            "value": record[metric],
                        }
                    )
    return rows


def main() -> int:
    args = parse_args()
    if args.repetitions < 1 or args.iterations < 1 or args.parallelism < 1:
        raise ValueError("repetitions, iterations, and parallelism must be positive")

    cloudformation, lambda_client = clients(args.region)
    outputs = stack_outputs(cloudformation, args.stack)
    target_list = targets(outputs)
    if args.seed_dynamo:
        dynamodb = boto3.client("dynamodb", region_name=args.region)
        seed_dynamo(dynamodb, outputs["DynamoQueryTableName"])

    args.output_dir.mkdir(parents=True, exist_ok=True)
    with ThreadPoolExecutor(max_workers=len(target_list)) as executor:
        target_rows = executor.map(
            lambda target: run_target(
                lambda_client,
                target,
                outputs,
                args.repetitions,
                args.iterations,
                args.parallelism,
                args.latitude,
                args.longitude,
                args.radius_km,
                args.cold_start,
            ),
            target_list,
        )
        rows = [row for rows_for_target in target_rows for row in rows_for_target]

    if not rows:
        raise RuntimeError("No performance metrics were collected")
    csv_path = args.output_dir / "metrics.csv"
    html_path = args.output_dir / "metrics.html"
    plot_path = args.output_dir / "metrics.png"
    write_csv(csv_path, rows)
    write_html(html_path, rows)
    plot(plot_path, rows)
    print(f"Wrote {csv_path}, {html_path}, and {plot_path}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)
