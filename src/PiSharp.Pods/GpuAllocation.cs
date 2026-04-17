namespace PiSharp.Pods;

public sealed record GpuAllocationPlan(
    IReadOnlyList<int> AssignedGpuIndexes,
    int TotalGpuCount,
    IReadOnlyList<int> UnavailableGpuIndexes);

public sealed class GpuAllocator
{
    private readonly int _totalGpus;
    private readonly HashSet<int> _allocated = new();

    public GpuAllocator(int totalGpus)
    {
        if (totalGpus < 0) throw new ArgumentOutOfRangeException(nameof(totalGpus));
        _totalGpus = totalGpus;
    }

    public int TotalGpus => _totalGpus;

    public IReadOnlyCollection<int> AllocatedGpus => _allocated;

    public void MarkAllocated(IEnumerable<int> gpuIndexes)
    {
        foreach (var index in gpuIndexes)
        {
            if (index < 0 || index >= _totalGpus)
            {
                throw new ArgumentOutOfRangeException(nameof(gpuIndexes), $"GPU index {index} is out of range [0, {_totalGpus}).");
            }

            _allocated.Add(index);
        }
    }

    public GpuAllocationPlan? TryAllocate(int requestedCount, IReadOnlyList<int>? preferredIndexes = null)
    {
        if (requestedCount <= 0)
        {
            return new GpuAllocationPlan(Array.Empty<int>(), _totalGpus, _allocated.ToArray());
        }

        var available = Enumerable.Range(0, _totalGpus)
            .Where(i => !_allocated.Contains(i))
            .ToList();

        if (available.Count < requestedCount)
        {
            return null;
        }

        IReadOnlyList<int> assigned;
        if (preferredIndexes is not null && preferredIndexes.All(available.Contains) && preferredIndexes.Count >= requestedCount)
        {
            assigned = preferredIndexes.Take(requestedCount).ToArray();
        }
        else
        {
            assigned = available.Take(requestedCount).ToArray();
        }

        foreach (var index in assigned)
        {
            _allocated.Add(index);
        }

        return new GpuAllocationPlan(assigned, _totalGpus, _allocated.ToArray());
    }

    public void Release(IEnumerable<int> gpuIndexes)
    {
        foreach (var index in gpuIndexes)
        {
            _allocated.Remove(index);
        }
    }
}
