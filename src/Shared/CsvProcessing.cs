using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.S3;
using Amazon.S3.Model;

namespace Benchmark.Shared;

public sealed record CsvProcessingRequest(
    [property: JsonPropertyName("bucket")] string Bucket,
    [property: JsonPropertyName("key")] string Key);

public sealed record CsvProcessingResult(
    string Bucket,
    string Key,
    int Rows,
    long CsvBytes,
    long JsonBytes);

public static class CsvProcessingService
{
    private static readonly AmazonS3Client S3Client = new();

    // SnapStart freezes the client before any socket exists, so the connection made here is
    // dead on restore. Call this from the after-restore hook to pay for a fresh TCP/TLS
    // handshake during the restore phase instead of inside the timed invocation.
    public static async Task PrimeConnectionAsync()
    {
        var bucket = FunctionEnvironment.GetConfiguration("CsvSourceBucketName");
        if (string.IsNullOrEmpty(bucket))
        {
            return;
        }

        try
        {
            await S3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = "sample.csv",
            });
        }
        catch
        {
            // Best-effort warmup only; a missing object/permission shouldn't fail the snapshot.
        }
    }

    public static async Task<CsvProcessingResult> ProcessAsync(CsvProcessingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Bucket))
        {
            throw new ArgumentException("The S3 bucket is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Key))
        {
            throw new ArgumentException("The S3 object key is required.", nameof(request));
        }

        using var response = await S3Client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = request.Bucket,
            Key = request.Key,
        });
        await using var csvBuffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(csvBuffer);
        var csv = Encoding.UTF8.GetString(csvBuffer.GetBuffer(), 0, checked((int)csvBuffer.Length));
        var rows = Parse(csv);
        var json = JsonSerializer.Serialize(rows, CsvJsonContext.Default.ListDictionaryStringString);

        return new CsvProcessingResult(request.Bucket, request.Key, rows.Count, csvBuffer.Length, Encoding.UTF8.GetByteCount(json));
    }

    private static List<Dictionary<string, string>> Parse(string csv)
    {
        using var reader = new StringReader(csv);
        var headerLine = reader.ReadLine() ?? throw new FormatException("CSV must contain a header row.");
        var headers = ParseLine(headerLine);
        if (headers.Count == 0 || headers.Any(string.IsNullOrWhiteSpace))
        {
            throw new FormatException("CSV headers must not be empty.");
        }

        var rows = new List<Dictionary<string, string>>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = ParseLine(line);
            if (fields.Count != headers.Count)
            {
                throw new FormatException("CSV rows must contain the same number of fields as the header.");
            }

            var row = new Dictionary<string, string>(headers.Count, StringComparer.Ordinal);
            for (var index = 0; index < headers.Count; index++)
            {
                row[headers[index]] = fields[index];
            }

            rows.Add(row);
        }

        return rows;
    }

    private static List<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        if (quoted)
        {
            throw new FormatException("CSV contains an unterminated quoted field.");
        }

        fields.Add(field.ToString());
        return fields;
    }
}

[JsonSerializable(typeof(List<Dictionary<string, string>>))]
internal partial class CsvJsonContext : JsonSerializerContext
{
}