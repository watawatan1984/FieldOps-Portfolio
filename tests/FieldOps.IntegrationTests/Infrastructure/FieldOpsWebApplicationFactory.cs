using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FieldOps.IntegrationTests.Infrastructure;

public sealed class FieldOpsWebApplicationFactory(
    string connectionString,
    Action<IServiceCollection>? configureServices = null,
    Action<ILoggingBuilder>? configureLogging = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:FieldOps", connectionString);
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FieldOps"] = connectionString
            }));
        builder.ConfigureLogging(logging => configureLogging?.Invoke(logging));
        builder.ConfigureServices(services =>
        {
            services.AddControllersWithViews().AddApplicationPart(typeof(Authorization.AuthorizationProbeController).Assembly);
            configureServices?.Invoke(services);
        });
    }
}