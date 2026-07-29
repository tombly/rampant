using System.Runtime.InteropServices;
using Rampant.Agent;

using var shutdownCts = new CancellationTokenSource();

// The supervisor gives an in-flight cycle a grace period to finish before hard-killing
// (see Rampant.Supervisor/AgentProcess.cs) - but that's only real if this process actually
// listens for SIGTERM instead of dying on the runtime's default disposition. Without this,
// a self-triggered restart (extend_self commits -> supervisor rebuilds -> restarts) can kill
// the very cycle that requested the change before it replies or marks its message processed,
// leaving the message to be silently reprocessed from scratch by the next process with no
// memory of what its predecessor just did.
void RequestShutdown(PosixSignalContext context)
{
    context.Cancel = true;
    shutdownCts.Cancel();
}

using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestShutdown);
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestShutdown);

var loop = new AgentLoop();
await loop.RunAsync(shutdownCts.Token);
