namespace TechSolutions.SyntheticMonitor;

/// <summary>Fixed parcel with a known, human-validated result (golden case).</summary>
public sealed class ProbeOptions
{
    public string Address { get; set; } = "";
    public ExpectedResult Expected { get; set; } = new();
    /// <summary>Relative tolerance for areas, e.g. 0.02 = ±2 %. Geometry is never compared byte by byte.</summary>
    public double AreaTolerance { get; set; } = 0.02;
    /// <summary>End-to-end budget: if no final result arrives within it, the probe fails.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);
}

public sealed class ExpectedResult
{
    public double LawnAreaSqft { get; set; }
    public int PolygonCount { get; set; }
    public Dictionary<string, bool> Flags { get; set; } = [];
}

public sealed record ProbeOutcome(bool Success, string? Failure, TimeSpan Elapsed, Guid? JobId)
{
    public static ProbeOutcome Ok(TimeSpan elapsed, Guid jobId) => new(true, null, elapsed, jobId);
    public static ProbeOutcome Fail(string reason, TimeSpan elapsed, Guid? jobId = null) => new(false, reason, elapsed, jobId);
}

public sealed class JiraOptions
{
    public string ProjectKey { get; set; } = "OPS";
    public string IssueType { get; set; } = "Bug";
    /// <summary>Dedup label: while an issue with this label is open, new failures are added as comments.</summary>
    public string DedupLabel { get; set; } = "synthetic-e2e";
    public string Environment { get; set; } = "prod";
}
