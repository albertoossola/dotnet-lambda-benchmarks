using Amazon.Lambda.Core;
using Benchmark.Shared;

namespace Benchmark.GeoSpatialLookup.Clr;

public sealed class GeoSpatialLookupHandler
{
    public GeoSpatialLookupHandler()
    {
        GeoSpatialLookupService.Initialize();
    }

    public Task<GeoSpatialLookupResult> HandleAsync(
        GeoSpatialLookupRequest input,
        ILambdaContext context)
    {
        return PerformanceMetrics.CaptureAsync(
            "StandardCLR",
            "geospatial-lookup",
            context,
            () => GeoSpatialLookupService.LookupAsync(input));
    }
}