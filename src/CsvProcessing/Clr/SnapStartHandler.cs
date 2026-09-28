using Amazon.Lambda.Core;
using Benchmark.Shared;

namespace Benchmark.CsvProcessing.Clr;

public sealed class CsvProcessingSnapStartHandler
{
    public CsvProcessingSnapStartHandler()
    {
        SnapshotRestore.RegisterBeforeSnapshot(BeforeSnapshotAsync);
        SnapshotRestore.RegisterAfterRestore(AfterRestoreAsync);
    }

    public Task<CsvProcessingResult> HandleAsync(
        CsvProcessingRequest input,
        ILambdaContext context)
    {
        return PerformanceMetrics.CaptureAsync(
            "SnapStart",
            "csv-processing",
            context,
            () => CsvProcessingService.ProcessAsync(input));
    }

    private static async ValueTask BeforeSnapshotAsync()
    {
        await CsvProcessingService.PrimeConnectionAsync();
        Console.WriteLine("CsvProcessing SnapStart before-snapshot hook invoked.");
    }

    private static async ValueTask AfterRestoreAsync()
    {
        await CsvProcessingService.PrimeConnectionAsync();
        Console.WriteLine("CsvProcessing SnapStart after-restore hook invoked.");
    }
}