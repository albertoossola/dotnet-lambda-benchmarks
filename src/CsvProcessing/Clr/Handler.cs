using Amazon.Lambda.Core;
using Benchmark.Shared;

namespace Benchmark.CsvProcessing.Clr;

public sealed class CsvProcessingHandler
{
    public Task<CsvProcessingResult> HandleAsync(
        CsvProcessingRequest input,
        ILambdaContext context)
    {
        return PerformanceMetrics.CaptureAsync(
            "StandardCLR",
            "csv-processing",
            context,
            () => CsvProcessingService.ProcessAsync(input));
    }
}