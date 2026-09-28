Run the benchmark with `uv`:

```bash
uv run main.py --seed-dynamo
```

By default, the runner publishes a fresh version for each function before invoking
it, so the first invocation records `ColdStartMs`. Reports are written to
`benchmark-results/metrics.csv` and `benchmark-results/metrics.png`.

Use `--no-cold-start` to invoke the existing live aliases without changing Lambda
versions. This is useful for warm-only measurements.
