using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace FieldOps.IntegrationTests.Infrastructure;

public sealed class FieldOpsWebApplicationFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:FieldOps", connectionString);
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FieldOps"] = connectionString
                }));
        builder.ConfigureServices(services =>
            services.AddControllersWithViews().AddApplicationPart(typeof(Authorization.AuthorizationProbeController).Assembly));
    }
}
