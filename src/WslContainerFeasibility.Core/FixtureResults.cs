namespace WslContainerFeasibility;

/// <summary>Describes the terminal outcome of a controlled fixture run.</summary>
public enum FixtureOutcome
{
    /// <summary>The fixture completed with its expected result.</summary>
    Completed,

    /// <summary>A prerequisite or required boundary was not verified.</summary>
    Blocked,

    /// <summary>The user cancelled the run.</summary>
    Cancelled,

    /// <summary>The fixture exceeded its execution limit.</summary>
    TimedOut,

    /// <summary>The fixture, infrastructure, or cleanup failed.</summary>
    Failed
}

/// <summary>Reports whether the native container prerequisites are available.</summary>
/// <param name="Ready">Whether the required components are available.</param>
/// <param name="Details">A user-readable prerequisite result.</param>
public sealed record PrerequisiteStatus(bool Ready, string Details);

/// <summary>Reports a fixture outcome and its persisted evidence location.</summary>
/// <param name="Outcome">The terminal run outcome.</param>
/// <param name="Summary">A user-readable explanation of the outcome.</param>
/// <param name="EvidencePath">The path to the saved local evidence file.</param>
public sealed record FixtureRunResult(FixtureOutcome Outcome, string Summary, string EvidencePath);
