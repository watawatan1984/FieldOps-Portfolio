using FieldOps.Features.Abstractions;
using FieldOps.Infrastructure.Auditing;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldOps.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFieldOpsInfrastructure(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContext<FieldOpsDbContext>(options => options.UseNpgsql(connectionString));
        services.AddIdentity<ApplicationUser, IdentityRole>()
            .AddEntityFrameworkStores<FieldOpsDbContext>()
            .AddClaimsPrincipalFactory<DemoUserClaimsPrincipalFactory>()
            .AddDefaultTokenProviders();
        services.AddScoped<DemoIdentitySeeder>();
        services.AddScoped<IFieldOpsDbContext>(provider => provider.GetRequiredService<FieldOpsDbContext>());
        services.AddScoped<IMutationExecutor, MutationExecutor>();
        services.AddScoped<IPartyNameLock, PostgresPartyNameLock>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}