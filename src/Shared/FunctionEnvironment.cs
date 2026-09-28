public static class FunctionEnvironment
{
    public static string EnvironmentName { get {
        string environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";

        environment =  environment.ToLowerInvariant() switch {
            "development" => "dev",
            "production" => "prod",
            string s => s
        };
        return environment;
    }}

    private static IConfiguration Configuration { get; set; } = new ConfigurationBuilder()
            .AddInMemoryCollection()
            .AddEnvironmentVariables()
            .AddJsonFile($"appsettings.{EnvironmentName}.json", optional: true)
            .Build();

    public static string GetConfiguration(string key)
    {
        return Configuration[key];
    }
}