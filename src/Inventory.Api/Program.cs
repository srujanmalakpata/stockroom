using Azure.Monitor.OpenTelemetry.AspNetCore;
using Inventory.Api.Auth;
using Inventory.Api.Endpoints;
using Inventory.Api.Http;
using Inventory.Application;
using Inventory.Application.Services;
using Inventory.Infrastructure;
using Inventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// --- Logging: the console formatter comes from configuration (Logging:Console:FormatterName):
// "json" (structured, one object per line) by default, "simple" in Development. ---
builder.Services.AddHttpLogging(o =>
{
    o.LoggingFields = HttpLoggingFields.RequestMethod | HttpLoggingFields.RequestPath
        | HttpLoggingFields.ResponseStatusCode | HttpLoggingFields.Duration;
    o.CombineLogs = true;
});

// Application Insights via OpenTelemetry, only when a connection string is configured (never in tests).
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor();
}

// --- Layers ---
builder.Services.AddApplication(
    builder.Configuration.GetSection(ConcurrencyOptions.SectionName).Get<ConcurrencyOptions>());
builder.Services.AddInfrastructure(builder.Configuration);

// --- HTTP concerns ---
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ProblemCodes.Apply);
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
builder.Services.AddApiKeyAuth(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddDbContextCheck<InventoryDbContext>("database", tags: ["ready"]);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "stockroom",
        Version = "v1",
        Description = "Products, stock per location, order reservations, fulfilment and low-stock alerts.",
    });
    var apiKey = new OpenApiSecurityScheme
    {
        Name = ApiKeyDefaults.HeaderName,
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Description = "Required for write endpoints.",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = ApiKeyDefaults.Scheme },
    };
    o.AddSecurityDefinition(ApiKeyDefaults.Scheme, apiKey);
    o.OperationFilter<WritePolicySecurityFilter>(); // only write endpoints require the key
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseHttpLogging();

if (app.Configuration.GetValue("OpenApi:Enabled", defaultValue: app.Environment.IsDevelopment()))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.MapGroup("/api")
    .MapProductEndpoints()
    .MapStockEndpoints()
    .MapOrderEndpoints();

await app.Services.MigrateDatabaseAsync();
await app.RunAsync();

/// <summary>Entry point; public so integration tests can host it with WebApplicationFactory.</summary>
public partial class Program;
