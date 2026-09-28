using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Benchmark.Shared;

namespace Benchmark.ImageProcessing.NativeAot;

public static class Program
{
    public static async Task Main()
    {
        var serializer = new SourceGeneratorLambdaJsonSerializer<LambdaJsonSerializerContext>();
        await LambdaBootstrapBuilder.Create<ImageProcessingRequest, ImageProcessingResult>(
            HandleAsync,
            serializer)
            .Build()
            .RunAsync();
    }

    private static Task<ImageProcessingResult> HandleAsync(
        ImageProcessingRequest input,
        ILambdaContext context)
    {
        return PerformanceMetrics.CaptureAsync(
            "NativeAOT",
            "image-processing",
            context,
            () => ImageProcessingService.ProcessAsync(input));
    }
}

[JsonSerializable(typeof(ImageProcessingRequest))]
[JsonSerializable(typeof(ImageProcessingResult))]
public partial class LambdaJsonSerializerContext : JsonSerializerContext
{
}
