using ClaimsApproval.Api.Auth;
using ClaimsApproval.Api.Data;
using ClaimsApproval.Api.Domain;
using ClaimsApproval.Api.Workflow;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using Zeebe.Client;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddJwtAuth(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ClaimMetrics>();
builder.Services.AddScoped<ClaimWorkflowHandlers>();

builder.Services.AddDbContext<ClaimsDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Claims")));

builder.Services.AddHealthChecks().AddDbContextCheck<ClaimsDbContext>("database");

// The workflow engine is optional so tests (and `Workflow:Enabled=false`) can run without Camunda.
if (builder.Configuration.GetValue("Workflow:Enabled", true))
{
    builder.Services.AddSingleton<IZeebeClient>(_ => ZeebeClient.Builder()
        .UseGatewayAddress(builder.Configuration["Workflow:GatewayAddress"] ?? "localhost:26500")
        .UsePlainText()
        .Build());
    builder.Services.AddSingleton<IWorkflowEngine, ZeebeWorkflowEngine>();
    builder.Services.AddHostedService<ZeebeJobWorkers>();
}

var app = builder.Build();

// Business rule violations and lost concurrency races become 409 Conflict with a readable message.
app.UseExceptionHandler(errors => errors.Run(async context =>
{
    var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = ex switch
    {
        DomainException => (StatusCodes.Status409Conflict, ex.Message),
        DbUpdateConcurrencyException => (StatusCodes.Status409Conflict,
            "The claim was changed by someone else. Reload it and try again."),
        _ => (StatusCodes.Status500InternalServerError, "Something went wrong."),
    };
    context.Response.StatusCode = status;
    await Results.Problem(title: title, statusCode: status).ExecuteAsync(context);
}));

if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<ClaimsDbContext>().Database.MigrateAsync();
}

app.MapOpenApi();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "Claims approval API"));

app.UseHttpMetrics();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapMetrics();

app.Run();

public partial class Program;
