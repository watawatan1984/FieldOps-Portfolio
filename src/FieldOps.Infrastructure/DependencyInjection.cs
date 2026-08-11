using FieldOps.Features.Abstractions;
using FieldOps.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldOps.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFieldOpsInfrastructure(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContext<FieldOpsDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IFieldOpsDbContext>(provider => provider.GetRequiredService<FieldOpsDbContext>());
        return services;
    }
}