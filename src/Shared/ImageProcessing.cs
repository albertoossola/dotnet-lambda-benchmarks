using System.Diagnostics;
using System.Text.Json.Serialization;
using Amazon.S3;
using Amazon.S3.Model;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Benchmark.Shared;

public sealed record ImageProcessingRequest(
    [property: JsonPropertyName("bucket")] string Bucket,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("iterations")] int Iterations = 1,
    [property: JsonPropertyName("parallelism")] int Parallelism = 1);

public sealed record ImageProcessingResult(
    string Bucket,
    string Key,
    int Iterations,
    int Width,
    int Height,
    long PngBytes);

public static class ImageProcessingService
{
    private const int MaxIterations = 100;
    private static readonly AmazonS3Client S3Client = new();

    // SnapStart freezes the client before any socket exists, so the connection made here is
    // dead on restore. Call this from the after-restore hook to pay for a fresh TCP/TLS
    // handshake during the restore phase instead of inside the timed invocation.
    public static async Task PrimeConnectionAsync()
    {
        var bucket = FunctionEnvironment.GetConfiguration("ImageSourceBucketName");
        if (string.IsNullOrEmpty(bucket))
        {
            return;
        }

        try
        {
            await S3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = "sample.jpg",
            });
        }
        catch
        {
            // Best-effort warmup only; a missing object/permission shouldn't fail the snapshot.
        }
    }

    public static async Task PrimeImageProcessingAsync()
    {
        using var dummyImage = new Image<Rgba32>(300, 300);
        await using var output = new MemoryStream();
        await dummyImage.SaveAsPngAsync(output);
    }

    public static async Task<ImageProcessingResult> ProcessAsync(ImageProcessingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Bucket))
        {
            throw new ArgumentException("The S3 bucket is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Key))
        {
            throw new ArgumentException("The S3 object key is required.", nameof(request));
        }

        var iterations = Math.Clamp(request.Iterations, 1, MaxIterations);
        var parallelism = Math.Clamp(request.Parallelism, 1, iterations);
        using var response = await S3Client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = request.Bucket,
            Key = request.Key,
        });

        using var image = await Image.LoadAsync<Rgba32>(response.ResponseStream);

        var processingStartedAt = Stopwatch.GetTimestamp();
        long pngBytes = 0;
        await Parallel.ForAsync(
            0,
            iterations,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            async (_, _) =>
            {
                await using var output = new MemoryStream();
                await image.SaveAsPngAsync(output);
                Interlocked.Exchange(ref pngBytes, output.Length);
            });

        var processingMilliseconds = (Stopwatch.GetTimestamp() - processingStartedAt) * 1000d / Stopwatch.Frequency;
        Console.WriteLine(
            $"ImageSharp PNG processing completed: iterations={iterations}, durationMs={processingMilliseconds:0.###}, " +
            $"workingSetMb={Environment.WorkingSet / 1024d / 1024d:0.###}, managedHeapMb={GC.GetTotalMemory(false) / 1024d / 1024d:0.###}");

        return new ImageProcessingResult(
            request.Bucket,
            request.Key,
            iterations,
            image.Width,
            image.Height,
            pngBytes);
    }
}