using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rampant.Supervisor;

var builder = Host.CreateApplicationBuilder(args);

// Read once, at startup, from this process's environment - which compose populates from .env on
// the host, outside the volume the agent can write. See SupervisorConfig for why that matters.
var config = SupervisorConfig.FromEnvironment();
builder.Services.AddSingleton(config);

builder.Services.AddSingleton<ISeedBootstrap, SeedBootstrap>();
builder.Services.AddSingleton<IBuildRunner, BuildRunner>();
builder.Services.AddSingleton<IAgentProcess, AgentProcess>();
builder.Services.AddSingleton<IClaudeCodeRunner, ClaudeCodeRunner>();
builder.Services.AddSingleton<SpendLedger>();
builder.Services.AddSingleton<ApprovalQueue>();
builder.Services.AddSingleton<Deployer>();
builder.Services.AddSingleton<RequestPipeline>();
builder.Services.AddSingleton<StatusWriter>();
builder.Services.AddSingleton<WakeTicker>();

builder.Services.AddSingleton(sp => new SignalClient(
    host: "signal-cli",
    port: 7583,
    logger: sp.GetRequiredService<ILogger<SignalClient>>()));

// Registered as both a singleton and a hosted service: the supervisor calls NotifyOwnerAsync on
// the same instance whose background loop owns the socket.
builder.Services.AddSingleton<SignalGateway>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SignalGateway>());
builder.Services.AddHostedService<ProcessSupervisor>();

var host = builder.Build();

var startupLogger = host.Services.GetRequiredService<ILogger<Program>>();
startupLogger.LogInformation(
    "Rampant supervisor starting. Daily budget ${Budget:0.00}, per-build cap ${Cap:0.00}, cooldown {Cooldown}, wake every {Wake} between {Start:00}:00 and {End:00}:00 {Zone}.",
    config.DailyBudgetUsd, config.MaxBudgetPerInvocationUsd, config.BuildCooldown, config.WakeInterval,
    config.WakeStartHour, config.WakeEndHour, config.WakeTimeZone?.Id ?? "(no window - waking around the clock)");

if (config.AnthropicApiKey is null)
    startupLogger.LogWarning("ANTHROPIC_API_KEY is not set - no capability request can be built.");

if (config.WakeTimeZone is null)
    startupLogger.LogWarning("RAMPANT_WAKE_TIMEZONE could not be resolved - quiet hours are disabled and the agent will be woken at any hour.");

await host.RunAsync();

// Named so ILogger<Program> resolves against a real type in a top-level-statements file.
public partial class Program;
