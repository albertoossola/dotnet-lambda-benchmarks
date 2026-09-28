using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Benchmark.Shared;

namespace Benchmark.CsvProcessing.NativeAot;

public static class Program
{
    public static async Task Main()
    {
        var serializer = new SourceGeneratorLambdaJsonSerializer<LambdaJsonSerializerContext>();
        await LambdaBootstrapBuilder.Create<CsvProcessingRequest, CsvProcessingResult>(
            HandleAsync,
            serializer)
            .Build()
            .RunAsync();
    }

    private static Task<CsvProcessingResult> HandleAsync(
        CsvProcessingRequest input,
        ILambdaContext context)
    {
        return PerformanceMetrics.CaptureAsync(
            "NativeAOT",
            "csv-processing",
            context,
            () => CsvProcessingService.ProcessAsync(input));
    }
}

[JsonSerializable(typeof(CsvProcessingRequest))]
[JsonSerializable(typeof(CsvProcessingResult))]
public partial class LambdaJsonSerializerContext : JsonSerializerContext
{
}