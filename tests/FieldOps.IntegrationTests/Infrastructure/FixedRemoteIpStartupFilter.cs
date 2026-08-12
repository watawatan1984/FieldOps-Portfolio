using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace FieldOps.IntegrationTests.Infrastructure;

public sealed class FixedRemoteIpStartupFilter(IPAddress remoteIpAddress) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
    {
        application.Use(async (context, continuePipeline) =>
        {
            context.Connection.RemoteIpAddress = remoteIpAddress;
            await continuePipeline();
        });
        next(application);
    };
}