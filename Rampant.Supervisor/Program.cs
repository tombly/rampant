using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rampant.Supervisor;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<ISeedBootstrap, SeedBootstrap>();
builder.Services.AddSingleton<IBuildRunner, BuildRunner>();
builder.Services.AddSingleton<IAgentProcess, AgentProcess>();
builder.Services.AddSingleton(sp => new SignalNotifier(
    host: "signal-cli",
    port: 7583,
    ownerIdentifier: Environment.GetEnvironmentVariable("RAMPANT_OWNER_SIGNAL_ID"),
    logger: sp.GetRequiredService<ILogger<SignalNotifier>>()));
builder.Services.AddHostedService<ProcessSupervisor>();

var host = builder.Build();
await host.RunAsync();
