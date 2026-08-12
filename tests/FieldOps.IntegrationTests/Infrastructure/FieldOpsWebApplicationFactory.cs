using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FieldOps.IntegrationTests.Infrastructure;

public sealed class FieldOpsWebApplicationFactory(
    string connectionString,
    Action<IServiceCollection>? configureServices = null,
    Action<ILoggingBuilder>? configureLogging = null,
    IReadOnlyDictionary<string, string?>? configuration = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:FieldOps", connectionString);
        builder.ConfigureAppConfiguration(configurationBuilder =>
        {
            Dictionary<string, string?> configurationValues = new(StringComparer.Ordinal)
            {
                ["ConnectionStrings:FieldOps"] = connectionString,
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