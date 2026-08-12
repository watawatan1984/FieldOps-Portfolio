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
    private readonly string _effectiveConnectionString = ResolveConnectionString(connectionString, configuration);
    private int _poolCleared;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (environment is not null)
        {
            builder.UseEnvironment(environment);
        }

        builder.UseSetting("ConnectionStrings:FieldOps", _effectiveConnectionString);
        builder.ConfigureAppConfiguration(configurationBuilder =>
        {
            Dictionary<string, string?> configurationValues = new(StringComparer.Ordinal)
            {
                ["ConnectionStrings:FieldOps"] = _effectiveConnectionString,
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

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing)
            {
                ClearExactPool();
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            ClearExactPool();
        }
    }

    private static string ResolveConnectionString(
        string defaultConnectionString,
        IReadOnlyDictionary<string, string?>? configurationValues)
    {
        if (configurationValues is not null &&
            configurationValues.TryGetValue("ConnectionStrings:FieldOps", out string? configuredConnectionString) &&
            !string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString;
        }

        return defaultConnectionString;
    }

    private void ClearExactPool()
    {
        if (Interlocked.Exchange(ref _poolCleared, 1) != 0)
        {
            return;
        }

        using NpgsqlConnection connection = new(_effectiveConnectionString);
        NpgsqlConnection.ClearPool(connection);
    }
}