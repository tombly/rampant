namespace Rampant.Cli;

public sealed record LogEvent(DateTimeOffset Timestamp, string Category, string Summary);
