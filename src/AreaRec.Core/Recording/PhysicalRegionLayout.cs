namespace AreaRec.Core.Recording;

/// <summary>
/// Describes the part of a physical selection supplied by one monitor.
/// Destination offsets are expressed in the selection's physical coordinate
/// space, so logical DPI coordinates never enter the graphics compositor.
/// </summary>
public readonly record struct PhysicalRegionSegment(
    PhysicalRegion TargetRegion,
    PhysicalRegion MonitorRegion,
    PhysicalRegion Intersection)
{
    public int DestinationX => Intersection.X - TargetRegion.X;
    public int DestinationY => Intersection.Y - TargetRegion.Y;
}

public static class PhysicalRegionLayout
{
    public static IReadOnlyList<PhysicalRegionSegment> Partition(
        PhysicalRegion targetRegion,
        IEnumerable<PhysicalRegion> monitorRegions)
    {
        ArgumentNullException.ThrowIfNull(monitorRegions);
        var normalizedTarget = targetRegion.NormalizeForH264();
        return monitorRegions
            .Select(monitor =>
            {
                var intersection = normalizedTarget.Intersection(monitor);
                return intersection.HasValue
                    ? new PhysicalRegionSegment(normalizedTarget, monitor, intersection.Value)
                    : (PhysicalRegionSegment?)null;
            })
            .Where(segment => segment.HasValue)
            .Select(segment => segment!.Value)
            .OrderBy(segment => segment.Intersection.X)
            .ThenBy(segment => segment.Intersection.Y)
            .ToArray();
    }
}
