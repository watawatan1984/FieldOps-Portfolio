using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace FieldOps.IntegrationTests.Infrastructure;

public sealed class FieldOpsWebApplicationFactory(
    string connectionString,
    Action<IServiceCollection>? configureServices = null,
    Action<ILoggingBuilder>? configureLogging = null,
    IReadOnlyDictionary<string, string?>? configuration = null,
    string? environment = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        string testConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false
        }.ConnectionString;
        if (environment is not null)
        {
            builder.UseEnvironment(environment);
        }

        builder.UseSetting("ConnectionStrings:FieldOps", testConnectionString);
        builder.ConfigureAppConfiguration(configurationBuilder =>
        {
            Dictionary<string, string?> configurationValues = new(StringComparer.Ordinal)
            {
                ["ConnectionStrings:FieldOps"] = testConnectionString,
                ["DemoMode:Enabled"] = "true",
                ["DemoMode:DatasetIdentifier"] = "fieldops-portal-fictional-demo",
                ["DemoMode:DatasetVersion"] = "1"
            };
            if (configuration is not null)
            {
                foreach ((string key, string? value) in configuration)
                {
                    configurationValues[key] = value;
                }
            }

            configurationBuilder.AddInMemoryCollection(configurationValues);
        });
        builder.ConfigureLogging(logging => configureLogging?.Invoke(logging));
        builder.ConfigureServices(services =>
        {
            services.AddControllersWithViews().AddApplicationPart(typeof(Authorization.AuthorizationProbeController).Assembly);
            configureServices?.Invoke(services);
        });
    }
}