using Amazon.Lambda.Core;
using Benchmark.Shared;

namespace Benchmark.ImageProcessing.Clr;

public sealed class ImageProcessingSnapStartHandler
{
    public ImageProcessingSnapStartHandler()
    {
        SnapshotRestore.RegisterBeforeSnapshot(BeforeSnapshotAsync);
        SnapshotRestore.RegisterAfterRestore(AfterRestoreAsync);
    }

    public Task<ImageProcessingResult> HandleAsync(
        ImageProcessingRequest input,
        ILambdaContext context)
    {
        return PerformanceMetrics.CaptureAsync(
            "SnapStart",
            "image-processing",
            context,
            () => ImageProcessingService.ProcessAsync(input));
    }

    private static async ValueTask BeforeSnapshotAsync()
    {
        await ImageProcessingService.PrimeConnectionAsync();
        await ImageProcessingService.PrimeImageProcessingAsync();
        Console.WriteLine("ImageProcessing SnapStart before-snapshot hook invoked.");
    }

    private static async ValueTask AfterRestoreAsync()
    {
        await ImageProcessingService.PrimeConnectionAsync();
        Console.WriteLine("ImageProcessing SnapStart after-restore hook invoked.");
    }
}
