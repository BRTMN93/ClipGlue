namespace ClipGlue.Models;

/// <summary>
/// One keep-range of a clip, in seconds from the start of the file. A struct
/// rather than the anonymous <c>(double Start, double End)</c> tuple the
/// preview window used before, because the range list, the overview bar and
/// the scrubber all pass the same thing around now and a named type keeps
/// their signatures readable.
/// </summary>
public readonly record struct TimeRange(double Start, double End)
{
    public double Length => Math.Max(0.0, End - Start);
}
