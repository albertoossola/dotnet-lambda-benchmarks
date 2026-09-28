using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Benchmark.Shared;

public sealed record QueryResultItem(string Id, string Name, int Description);
public sealed record QueryRequest(string id);

public static class DynamoQueryService
{
    private static readonly AmazonDynamoDBClient Client = new();

    // SnapStart freezes the client before any socket exists, so the connection made here is
    // dead on restore. Call this from the after-restore hook to pay for a fresh TCP/TLS
    // handshake during the restore phase instead of inside the timed invocation.
    public static async Task PrimeConnectionAsync()
    {
        var tableName = FunctionEnvironment.GetConfiguration("DynamoDbTableName");
        if (string.IsNullOrEmpty(tableName))
        {
            return;
        }

        try
        {
            await Client.DescribeTableAsync(tableName);
        }
        catch
        {
            // Best-effort warmup only; a missing table/permission shouldn't fail the snapshot.
        }
    }

    public static async Task<QueryResultItem> QueryAsync(QueryRequest request)
    {
        var tableName = FunctionEnvironment.GetConfiguration("DynamoDbTableName");

        if (string.IsNullOrEmpty(tableName))
        {
            throw new InvalidOperationException("DynamoDbTableName environment variable is not set.");
        }

        var getItemRequest = new GetItemRequest(tableName, new Dictionary<string, AttributeValue>
        {
            { "pk", new AttributeValue { S = request.id } },
            { "sk", new AttributeValue { S = request.id } },
        });
        var getItemResponse = await Client.GetItemAsync(getItemRequest);
        var document = getItemResponse.Item;
        return new QueryResultItem(
            document["id"].S,
            document["name"].S,
            int.Parse(document["description"].N));
    }
}