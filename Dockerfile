FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore src/FieldOps.Web/FieldOps.Web.csproj
RUN dotnet publish src/FieldOps.Web/FieldOps.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    PORT=8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080
COPY --from=build /app/publish .

USER app
HEALTHCHECK --interval=30s --timeout=8s --start-period=120s --retries=3 \
    CMD ["dotnet", "FieldOps.Web.dll", "--health-check"]

ENTRYPOINT ["dotnet", "FieldOps.Web.dll"]
