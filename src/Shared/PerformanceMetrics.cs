#pragma warning disable CA2255

using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.Core;

namespace Benchmark.Shared;

public static class PerformanceMetrics
{
    private static long processStartedAt;
    private static int isColdStart = 1;

    [ModuleInitializer]
    internal static void Initialize()
    {
        processStartedAt = Stopwatch.GetTimestamp();
        SnapshotRestore.RegisterAfterRestore(OnAfterRestore);
    }

    // A SnapStart restore resumes a process frozen before this handler ever ran, so the
    // module-init timestamp reflects when the snapshot was taken, not this restore. Reset
    // it here or ColdStartMs keeps growing by however long the snapshot sat cached.
    private static ValueTask OnAfterRestore()
    {
        processStartedAt = Stopwatch.GetTimestamp();
        Volatile.Write(ref isColdStart, 1);
        return ValueTask.CompletedTask;
    }

    public static async Task<T> CaptureAsync<T>(
        string deploymentType,
        string example,
        ILambdaContext context,
        Func<Task<T>> action)
    {
        var invocationStartedAt = Stopwatch.GetTimestamp();
        var coldStart = Interlocked.Exchange(ref isColdStart, 0) == 1;
        var result = await action();
        var durationMs = ElapsedMilliseconds(invocationStartedAt);
        var metrics = new Dictionary<string, double>
        {
            ["DurationMs"] = durationMs,
            ["MemoryWorkingSetMb"] = Process.GetCurrentProcess().WorkingSet64 / 1024d / 1024d,
            ["ManagedHeapMb"] = GC.GetTotalMemory(false) / 1024d / 1024d,
        };

        if (coldStart)
        {
            metrics["ColdStartMs"] = ElapsedMilliseconds(processStartedAt);
        }
        else
        {
            metrics["WarmStartMs"] = durationMs;
        }

        WriteEmbeddedMetrics(context, deploymentType, example, metrics);
        return result;
    }

    private static double ElapsedMilliseconds(long startedAt)
    {
        return (Stopwatch.GetTimestamp() - startedAt) * 1000d / Stopwatch.Frequency;
    }

    private static void WriteEmbeddedMetrics(
        ILambdaContext context,
        string deploymentType,
        string example,
        IReadOnlyDictionary<string, double> metrics)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteStartObject("_aws");
        writer.WriteNumber("Timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        writer.WriteStartArray("CloudWatchMetrics");
        writer.WriteStartObject();
        writer.WriteString("Namespace", "DotnetLambdaBenchmarks");
        writer.WriteStartArray("Dimensions");
        writer.WriteStartArray();
        writer.WriteStringValue("FunctionName");
        writer.WriteStringValue("DeploymentType");
        writer.WriteStringValue("Example");
        writer.WriteEndArray();
        writer.WriteEndArray();
        writer.WriteStartArray("Metrics");
        foreach (var metric in metrics.Keys)
        {
            writer.WriteStartObject();
            writer.WriteString("Name", metric);
            writer.WriteString("Unit", metric.EndsWith("Mb", StringComparison.Ordinal) ? "Megabytes" : "Milliseconds");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteString("FunctionName", context.FunctionName);
        writer.WriteString("DeploymentType", deploymentType);
        writer.WriteString("Example", example);
        foreach (var metric in metrics)
        {
            writer.WriteNumber(metric.Key, metric.Value);
        }
        writer.WriteEndObject();
        writer.Flush();
        Console.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }
}

#pragma warning restore CA2255
