using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Benchmark.GeoSpatialLookup.Clr;
using Benchmark.Shared;

namespace Benchmark.GeoSpatialLookup.NativeAot;

public static class Program
{
    public static async Task Main()
    {
        GeoSpatialLookupService.Initialize();
        var serializer = new SourceGeneratorLambdaJsonSerializer<LambdaJsonSerializerContext>();
        await LambdaBootstrapBuilder.Create<GeoSpatialLookupRequest, GeoSpatialLookupResult>(
            HandleAsync,
            serializer)
            .Build()
            .RunAsync();
    }

    private static Task<GeoSpatialLookupResult> HandleAsync(
        GeoSpatialLookupRequest input,
        ILambdaContext context)
    {
        return PerformanceMetrics.CaptureAsync(
            "NativeAOT",
            "geospatial-lookup",
            context,
            () => GeoSpatialLookupService.LookupAsync(input));
    }
}

[JsonSerializable(typeof(GeoSpatialLookupRequest))]
[JsonSerializable(typeof(GeoSpatialLookupResult))]
public partial class LambdaJsonSerializerContext : JsonSerializerContext
{
}
