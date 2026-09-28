using Amazon.Lambda.Core;
using Benchmark.Shared;

namespace Benchmark.GeoSpatialLookup.Clr;

public sealed class GeoSpatialLookupSnapStartHandler
{
    public GeoSpatialLookupSnapStartHandler()
    {
        SnapshotRestore.RegisterBeforeSnapshot(BeforeSnapshotAsync);
        SnapshotRestore.RegisterAfterRestore(AfterRestoreAsync);
    }

    public Task<GeoSpatialLookupResult> HandleAsync(
        GeoSpatialLookupRequest input,
        ILambdaContext context)
    {
        return PerformanceMetrics.CaptureAsync(
            "SnapStart",
            "geospatial-lookup",
            context,
            () => GeoSpatialLookupService.LookupAsync(input));
    }

    private static async ValueTask BeforeSnapshotAsync()
    {
        GeoSpatialLookupService.Initialize();
        var warmupRequest = new GeoSpatialLookupRequest(41.9028, 12.4964, 25);
        await GeoSpatialLookupService.LookupAsync(warmupRequest);
        await GeoSpatialLookupService.LookupAsync(warmupRequest);
        Console.WriteLine("GeoSpatialLookup SnapStart before-snapshot hook invoked.");
    }

    private static ValueTask AfterRestoreAsync()
    {
        Console.WriteLine("GeoSpatialLookup SnapStart after-restore hook invoked.");
        return ValueTask.CompletedTask;
    }
}