using Rampant.Cli;

var cwd = Directory.GetCurrentDirectory();
var command = args.Length > 0 ? args[0] : "log";

// Before anything else: this binary is installed by hand and nothing else replaces it, so it can
// silently outlive the formats it reads. See StalenessCheck.
StalenessCheck.WarnIfStale(cwd);

return command switch
{
    "log" => LogCommand.Run(cwd),
    "start" => DockerComposeCommand.Run(cwd, "up", "-d"),
    "stop" => DockerComposeCommand.Run(cwd, "stop"),
    _ => Unknown(command),
};

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command: '{command}'. Available commands: log, start, stop");
    return 1;
}
