# .NET Lambda deployment benchmarks

> [!NOTE]
> 🚀 See the [Benchmark Results](https://albertoossola.github.io/dotnet-lambda-benchmarks/)

This AWS CDK project deploys eleven .NET 10 Lambda functions that run four workloads through managed CLR, SnapStart, and Native AOT deployment strategies.

| Workload | Deployment variants | What it does |
| --- | --- | --- |
| `dynamo-query` | Standard CLR, SnapStart, Native AOT | Reads an item from the deployed DynamoDB table |
| `image-processing` | Standard CLR, SnapStart, Native AOT | Loads an image from S3 and encodes it to PNG repeatedly with ImageSharp |
| `csv-processing` | Standard CLR, SnapStart, Native AOT | Loads a 20 MB or larger CSV from S3 into memory, parses it, and serializes the rows to JSON without a data library |
| `geospatial-lookup` | Standard CLR, SnapStart, Native AOT | Downloads Italy's Who's On First Parquet data from S3, loads locations into a grid index, and finds the nearest place |

The CLR and Native AOT handlers share their workload logic through `Shared`. The normal CLR and SnapStart functions reuse the same CLR host asset, with separate SnapStart handler classes that register before-snapshot and after-restore hooks; the Native AOT functions use separate ARM64 bootstrap projects.

Every handler writes CloudWatch Embedded Metric Format records in the `DotnetLambdaBenchmarks` namespace. The records include `DurationMs`, `ColdStartMs` on the first process invocation, `WarmStartMs` on later invocations, `MemoryWorkingSetMb`, and `ManagedHeapMb`. The stack also creates a CloudWatch dashboard.

## Prerequisites

- AWS credentials with permission to deploy CloudFormation, Lambda, CloudWatch Logs, and CloudWatch dashboards
- Node.js 20 or newer
- .NET 10 SDK or newer
- Docker Desktop running; CDK uses AWS .NET 10 build images for managed and ARM64 Native AOT publishing
- AWS CDK is installed locally through `npm install`, so a global CDK install is unnecessary

## Deploy

```bash
npm install
npx cdk bootstrap
npm run synth
npm run deploy
```

The deployed stack is named `besharp-dotnet-lambda`. Its outputs contain thirteen alias ARNs, plus the DynamoDB table name and source buckets. Invoking the aliases keeps the benchmark targets stable while new published versions are created.

The geospatial source bucket is populated automatically during deployment with the Italy administrative-boundary Parquet file from [Geocode Earth's Who's On First downloads](https://geocode.earth/data/whosonfirst/#IT). The data is made available under the [Who's On First License](https://whosonfirst.org/docs/licenses/). Each geospatial Lambda downloads and parses the file into memory during initialization; the SnapStart version captures that initialized index in its snapshot.

Upload a CSV of at least 20 MB to the CSV source bucket before running the benchmark. The parser expects a header and the same number of columns on every row; the runner uses the key `sample.csv`:

```bash
aws s3 cp ./sample.csv s3://<CsvProcessingSourceBucketName>/sample.csv
```

Example invocations:

```bash
aws lambda invoke --function-name <DynamoQueryClrAliasArn> \
	--payload '{"id":"item-001"}' response.json
aws lambda invoke --function-name <DynamoQueryNativeAotAliasArn> \
	--payload '{"id":"item-001"}' response.json
aws lambda invoke --function-name <ImageProcessingSnapStartAliasArn> \
	--payload '{"bucket":"<ImageProcessingSourceBucketName>","key":"sample.jpg","iterations":10,"parallelism":4}' response.json
aws lambda invoke --function-name <CsvProcessingNativeAotAliasArn> \
	--payload '{"bucket":"<CsvProcessingSourceBucketName>","key":"sample.csv"}' response.json
aws lambda invoke --function-name <GeoSpatialLookupSnapStartAliasArn> \
	--payload '{"latitude":41.9028,"longitude":12.4964,"radiusKm":25}' response.json
aws lambda invoke --function-name <GeoSpatialLookupNativeAotAliasArn> \
	--payload '{"latitude":41.9028,"longitude":12.4964,"radiusKm":25}' response.json
```

For the CLI, the alias ARN can be passed as the function name. If your AWS CLI requires base64 input handling, add `--cli-binary-format raw-in-base64-out`.

## Python benchmark runner

Install the optional runner dependencies and invoke every deployed function twice:

```bash
python3 -m venv /tmp/dotnet-lambda-benchmark-venv
/tmp/dotnet-lambda-benchmark-venv/bin/pip install -r requirements-benchmark.txt
/tmp/dotnet-lambda-benchmark-venv/bin/python scripts/benchmark.py \
	--repetitions 2 --seed-dynamo
```

The runner writes `benchmark-results/metrics.csv`, `benchmark-results/metrics.png`, and a self-contained `benchmark-results/metrics.html` report. Open the HTML report in a browser for workload-grouped median results across deployment strategies. Use `--latitude`, `--longitude`, and `--radius-km` to change the geospatial lookup.
Use `--iterations` and `--parallelism` to control image work. Add `--cold-start`
to update a private configuration nonce, publish a fresh version for each function,
and invoke those versions directly. This creates billable published versions and
does not move the live aliases.

## Benchmarking

1. Seed the DynamoDB table and upload a source image to the output resources.
2. Invoke each alias once after deployment and record the first `ColdStartMs` and `DurationMs` log events.
3. Invoke each alias repeatedly, for example 100 times, with the same payload.
4. Compare p50 and p95 `DurationMs`, first-invocation `ColdStartMs`, `WarmStartMs`, `MemoryWorkingSetMb`, and billed duration from the Lambda `REPORT` lines.
5. Use the generated dashboard or query the `DotnetLambdaBenchmarks` namespace in CloudWatch Metrics.
6. Repeat after changing memory size or publishing a new version. SnapStart measurements should be made after the version has completed its snapshot lifecycle.

This is an application-level benchmark, not a substitute for Lambda's `REPORT` line. `ColdStartMs` measures process startup as observed by the handler, while CloudWatch `Init Duration` is the authoritative Lambda initialization measurement.

## Clean up

```bash
npm run destroy
```
