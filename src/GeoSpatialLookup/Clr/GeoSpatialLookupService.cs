using System.Text.Json.Serialization;
using Amazon.S3;
using Amazon.S3.Model;
using Benchmark.Shared;
using Parquet;
using Parquet.Data;

namespace Benchmark.GeoSpatialLookup.Clr;

public sealed record GeoSpatialLookupRequest(
    [property: JsonPropertyName("latitude")] double Latitude,
    [property: JsonPropertyName("longitude")] double Longitude,
    [property: JsonPropertyName("radiusKm")] double RadiusKm = 25);

public sealed record GeoSpatialLookupResult(
    string Name,
    double Latitude,
    double Longitude,
    double DistanceKm);

public static class GeoSpatialLookupService
{
    private const string Example = "geospatial-lookup";
    private const double GridSize = 0.1;
    private static readonly AmazonS3Client S3Client = new();
    private static readonly Lazy<LocationIndex> Locations = new(LoadLocations, true);

    public static void Initialize()
    {
        _ = Locations.Value;
    }

    public static Task<GeoSpatialLookupResult> LookupAsync(GeoSpatialLookupRequest request)
    {
        if (request.Latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Latitude));
        }
        if (request.Longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Longitude));
        }
        var radiusKm = Math.Clamp(request.RadiusKm, 1, 500);
        var nearest = Locations.Value.FindNearest(request.Latitude, request.Longitude, radiusKm);
        if (nearest is null)
        {
            throw new KeyNotFoundException("No location was found within the requested radius.");
        }

        return Task.FromResult(new GeoSpatialLookupResult(nearest.Value.Name, nearest.Value.Latitude, nearest.Value.Longitude, nearest.Value.DistanceKm));
    }

    private static LocationIndex LoadLocations()
    {
        var bucket = RequiredConfiguration("LocationBucket");
        var prefix = RequiredConfiguration("LocationPrefix").Trim('/');
        var objects = S3Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
            Prefix = prefix + "/",
        }).GetAwaiter().GetResult().S3Objects
            .Where(item => item.Key.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (objects.Length == 0)
        {
            throw new InvalidOperationException($"No Parquet files found in s3://{bucket}/{prefix}/.");
        }

        var locations = new List<Location>();
        foreach (var item in objects)
        {
            var parquetPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(item.Key));
            using (var response = S3Client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = item.Key }).GetAwaiter().GetResult())
            using (var output = File.Create(parquetPath))
            {
                response.ResponseStream.CopyToAsync(output).GetAwaiter().GetResult();
            }

            locations.AddRange(ReadLocations(parquetPath));
        }

        var index = new LocationIndex(locations);
        Console.WriteLine($"Loaded {Example} index with {locations.Count:N0} locations from s3://{bucket}/{prefix}/.");
        return index;
    }

    private static IEnumerable<Location> ReadLocations(string parquetPath)
    {
        using var stream = File.OpenRead(parquetPath);
        using var reader = ParquetReader.CreateAsync(stream).GetAwaiter().GetResult();
        var fields = reader.Schema.GetDataFields().ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        var locations = new List<Location>();
        for (var groupIndex = 0; groupIndex < reader.RowGroupCount; groupIndex++)
        {
            using var group = reader.OpenRowGroupReader(groupIndex);
            var nameColumn = group.ReadColumnAsync(fields["name"]).GetAwaiter().GetResult();
            var latitudeColumn = group.ReadColumnAsync(fields["lat"]).GetAwaiter().GetResult();
            var longitudeColumn = group.ReadColumnAsync(fields["lon"]).GetAwaiter().GetResult();
            var nameIndex = 0;
            var latitudeIndex = 0;
            var longitudeIndex = 0;
            var rowCount = group.RowCount;
            for (var row = 0; row < rowCount; row++)
            {
                if (!TryReadDefinedValue(nameColumn, row, ref nameIndex, out var rawName)
                    || !TryReadDefinedValue(latitudeColumn, row, ref latitudeIndex, out var rawLatitude)
                    || !TryReadDefinedValue(longitudeColumn, row, ref longitudeIndex, out var rawLongitude))
                {
                    continue;
                }

                var name = Convert.ToString(rawName, System.Globalization.CultureInfo.InvariantCulture)
                    ?? throw new FormatException("Who's On First Parquet name column contains a null value.");
                locations.Add(new Location(
                    name,
                    Convert.ToDouble(rawLatitude, System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToDouble(rawLongitude, System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return locations;
    }

    private static bool TryReadDefinedValue(
        DataColumn column,
        int row,
        ref int definedIndex,
        out object? value)
    {
        var definitionLevels = column.DefinitionLevels;
        if (definitionLevels is not null && definitionLevels[row] < column.Field.MaxDefinitionLevel)
        {
            value = null;
            return false;
        }

        value = column.DefinedData.GetValue(definedIndex++);
        return true;
    }

    private static string RequiredConfiguration(string key)
    {
        return FunctionEnvironment.GetConfiguration(key)
            ?? throw new InvalidOperationException($"{key} environment variable is not set.");
    }

    private readonly record struct Location(string Name, double Latitude, double Longitude);

    private sealed class LocationIndex
    {
        private readonly Dictionary<(int Latitude, int Longitude), Location[]> cells;

        public LocationIndex(IEnumerable<Location> locations)
        {
            cells = locations
                .GroupBy(location => Cell(location.Latitude, location.Longitude))
                .ToDictionary(group => group.Key, group => group.ToArray());
        }

        public (string Name, double Latitude, double Longitude, double DistanceKm)? FindNearest(double latitude, double longitude, double radiusKm)
        {
            var bestDistance = radiusKm;
            Location? best = null;
            var cell = Cell(latitude, longitude);
            var cellRadius = (int)Math.Ceiling(radiusKm / 11.1);
            for (var latitudeOffset = -cellRadius; latitudeOffset <= cellRadius; latitudeOffset++)
            {
                for (var longitudeOffset = -cellRadius; longitudeOffset <= cellRadius; longitudeOffset++)
                {
                    if (!cells.TryGetValue((cell.Latitude + latitudeOffset, cell.Longitude + longitudeOffset), out var candidates))
                    {
                        continue;
                    }
                    foreach (var candidate in candidates)
                    {
                        var distance = DistanceKm(latitude, longitude, candidate.Latitude, candidate.Longitude);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = candidate;
                        }
                    }
                }
            }

            return best is { } location
                ? (location.Name, location.Latitude, location.Longitude, bestDistance)
                : null;
        }

        private static (int Latitude, int Longitude) Cell(double latitude, double longitude) =>
            ((int)Math.Floor(latitude / GridSize), (int)Math.Floor(longitude / GridSize));

        private static double DistanceKm(double latitude1, double longitude1, double latitude2, double longitude2)
        {
            var latitude = DegreesToRadians(latitude2 - latitude1);
            var longitude = DegreesToRadians(longitude2 - longitude1);
            var a = Math.Sin(latitude / 2) * Math.Sin(latitude / 2)
                + Math.Cos(DegreesToRadians(latitude1)) * Math.Cos(DegreesToRadians(latitude2))
                * Math.Sin(longitude / 2) * Math.Sin(longitude / 2);
            return 6371 * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;
    }
}