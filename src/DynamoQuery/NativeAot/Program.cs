using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Benchmark.Shared;

namespace Benchmark.DynamoQuery.NativeAot;

public static class Program
{
    public static async Task Main()
    {
        var serializer = new SourceGeneratorLambdaJsonSerializer<LambdaJsonSerializerContext>();
        await LambdaBootstrapBuilder.Create<QueryRequest, QueryResultItem>(
            HandleAsync,
            serializer)
            .Build()
            .RunAsync();
    }

    private static Task<QueryResultItem> HandleAsync(QueryRequest input, ILambdaContext context)
    {
        return PerformanceMetrics.CaptureAsync(
            "NativeAOT",
            "dynamo-query",
            context,
            () => DynamoQueryService.QueryAsync(input));
    }
}

[JsonSerializable(typeof(QueryResultItem))]
[JsonSerializable(typeof(QueryRequest))]
public partial class LambdaJsonSerializerContext : JsonSerializerContext
{
}
