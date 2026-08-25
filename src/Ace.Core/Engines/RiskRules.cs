using Ace.Core.Models;

namespace Ace.Core.Engines;

/// <summary>
/// All weights and thresholds of the deterministic risk model (FR-008), kept as named
/// constants in one place for readability. Weights sum to 100 so each factor's
/// contribution is directly readable as points.
/// </summary>
public static class RiskRules
{
    // ---- factor weights (sum = 100 points) ---------------------------------

    /// <summary>Points for the number of affected components (saturates at <see cref="AffectedCountSaturation"/>).</summary>
    public const double WeightAffectedComponents = 30.0;

    /// <summary>Points for how deep the impact propagates (scales with <see cref="MaxDepthForScore"/>).</summary>
    public const double WeightDependencyDepth = 15.0;

    /// <summary>Points when the change touches a public API surface (controller / Api project).</summary>
    public const double WeightPublicApiExposure = 20.0;

    /// <summary>Points when the impact crosses project boundaries.</summary>
    public const double WeightCrossProjectImpact = 15.0;

    /// <summary>Points for weak or missing test coverage of the affected components.</summary>
    public const double WeightTestCoverageGap = 10.0;

    /// <summary>Points when configuration or database files are part of the change set.</summary>
    public const double WeightConfigOrDatabaseChange = 10.0;

    // ---- scaling thresholds -------------------------------------------------

    /// <summary>Affected-component count at which the count factor reaches full weight.</summary>
    public const int AffectedCountSaturation = 12;

    /// <summary>Impact depth at which the depth factor reaches full weight.</summary>
    public const int MaxDepthForScore = 3;

    /// <summary>Affected tests below this ratio of affected components score half the coverage gap.</summary>
    public const double PartialCoverageRatio = 1.0;

    // ---- bands ---------------------------------------------------------------

    /// <summary>Scores below this bound are Low risk.</summary>
    public const int LowUpperBoundExclusive = 34;

    /// <summary>Scores at or above this bound are High risk.</summary>
    public const int HighLowerBoundInclusive = 67;

    /// <summary>Maps a 0–100 score onto a risk band: Low (&lt;34), Medium (&lt;67), High.</summary>
    public static RiskLevel Band(int score)
        => score < LowUpperBoundExclusive
            ? RiskLevel.Low
            : score < HighLowerBoundInclusive
                ? RiskLevel.Medium
                : RiskLevel.High;

    /// <summary>Clamps a raw score into the 0–100 range.</summary>
    public static int ClampScore(double score)
        => (int)Math.Clamp(Math.Round(score, MidpointRounding.AwayFromZero), 0, 100);
}
