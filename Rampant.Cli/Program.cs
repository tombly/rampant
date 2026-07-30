using Rampant.Cli;

var workspaceRoot = Path.Combine(Directory.GetCurrentDirectory(), "local-workspace");
if (!Directory.Exists(workspaceRoot))
{
    Console.Error.WriteLine($"No local-workspace found under {Directory.GetCurrentDirectory()} - run this from ~/rampant.");
    return 1;
}

var command = args.Length > 0 ? args[0] : "log";

return command switch
{
    "log" => LogCommand.Run(workspaceRoot),
    _ => Unknown(command),
};

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command: '{command}'. Available commands: log");
    return 1;
}
