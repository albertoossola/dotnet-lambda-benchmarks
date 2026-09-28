using Amazon.Lambda.Core;
using Benchmark.Shared;

namespace Benchmark.DynamoQuery.Clr;

public sealed class DynamoQuerySnapStartHandler
{
    public DynamoQuerySnapStartHandler()
    {
        SnapshotRestore.RegisterBeforeSnapshot(BeforeSnapshotAsync);
        SnapshotRestore.RegisterAfterRestore(AfterRestoreAsync);
    }

    public Task<QueryResultItem> HandleAsync(QueryRequest input, ILambdaContext context)
    {
        return PerformanceMetrics.CaptureAsync(
            "SnapStart",
            "dynamo-query",
            context,
            () => DynamoQueryService.QueryAsync(input));
    }

    private static async ValueTask BeforeSnapshotAsync()
    {
        await DynamoQueryService.PrimeConnectionAsync();
        Console.WriteLine("DynamoQuery SnapStart before-snapshot hook invoked.");
    }

    private static async ValueTask AfterRestoreAsync()
    {
        await DynamoQueryService.PrimeConnectionAsync();
        Console.WriteLine("DynamoQuery SnapStart after-restore hook invoked.");
    }
}
