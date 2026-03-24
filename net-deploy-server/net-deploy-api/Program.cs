using NET.Deploy.Api.Data;
using NET.Deploy.Api.Logic.Deploy;
using NET.Deploy.Api.Logic.DeployLogs;
using NET.Deploy.Api.Logic.Git;
using NET.Deploy.Api.Logic.IIS;
using NET.Deploy.Api.Logic.Services;
using NET.Deploy.Api.Logic.Settings;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

// OpenAPI/Swagger
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();

// CORS for Angular UI
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:4200"];
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // needed for SSE stream if cookies are ever used
    });
});

// Configure MongoDB
builder.Services.AddSingleton<MongoDbContext>();

// Register Domain Logic
builder.Services.AddSingleton<SettingsLogic>();
builder.Services.AddSingleton<ServicesLogic>();
builder.Services.AddSingleton<DeployLogsLogic>();
builder.Services.AddSingleton<GitLogic>();
builder.Services.AddSingleton<EnvConfigsLogic>();
builder.Services.AddSingleton<NET.Deploy.Api.Logic.Maintenance.MaintenanceLogic>();
builder.Services.AddSingleton<NET.Deploy.Api.Logic.DeployHistory.DeployHistoryLogic>();

// Deploy dependencies
builder.Services.AddSingleton<ProcessRunner>();
builder.Services.AddSingleton<BuildManager>();
builder.Services.AddSingleton<TransferManager>();
builder.Services.AddSingleton<DeployLogic>();

builder.Services.AddSingleton<IISLogic>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// app.UseHttpsRedirection();
app.UseCors();
app.UseAuthorization();
app.MapControllers();

app.Run();
