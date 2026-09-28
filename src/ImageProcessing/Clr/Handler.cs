using Amazon.Lambda.Core;
using Benchmark.Shared;

namespace Benchmark.ImageProcessing.Clr;

public sealed class ImageProcessingHandler
{
    public Task<ImageProcessingResult> HandleAsync(
        ImageProcessingRequest input,
        ILambdaContext context)
    {
        return PerformanceMetrics.CaptureAsync(
            "StandardCLR",
            "image-processing",
            context,
            () => ImageProcessingService.ProcessAsync(input));
    }
}
