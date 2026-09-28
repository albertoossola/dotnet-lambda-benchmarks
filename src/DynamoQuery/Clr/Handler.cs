using Amazon.Lambda.Core;
using Benchmark.Shared;

namespace Benchmark.DynamoQuery.Clr;

public sealed class DynamoQueryHandler
{
    public Task<QueryResultItem> HandleAsync(QueryRequest input, ILambdaContext context)
    {
        return PerformanceMetrics.CaptureAsync(
            "StandardCLR",
            "dynamo-query",
            context,
            () => DynamoQueryService.QueryAsync(input));
    }
}
