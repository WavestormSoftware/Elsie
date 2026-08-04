namespace Elsie.Soak.Soak;

/// <summary>Outcome of one stress scenario.</summary>
internal sealed class ScenarioResult
{
    public required string Name { get; init; }
    public bool Skipped { get; init; }
    public string? SkipReason { get; init; }
    public bool Passed { get; set; }
    public string? FailureMessage { get; set; }
    public long Requests { get; set; }
    public long Failures { get; set; }
    public long ExpectedRefusals { get; set; }
    public double P50Ms { get; set; }
    public double P99Ms { get; set; }
    public MemorySnapshot? MemoryBefore { get; set; }
    public MemorySnapshot? MemoryAfter { get; set; }
    public long ServerActiveAfterDrain { get; set; }
    public TimeSpan? ServerStopDuration { get; set; }
    public TimeSpan Duration { get; set; }
    public string? Details { get; set; }

    public static ScenarioResult SkippedResult(string name, string reason) =>
        new() { Name = name, Skipped = true, SkipReason = reason, Passed = true };
}