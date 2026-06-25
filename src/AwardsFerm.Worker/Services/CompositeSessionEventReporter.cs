using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;

namespace AwardsFerm.Worker.Services;

public sealed class CompositeSessionEventReporter : ISessionEventReporter
{
    private readonly IReadOnlyList<ISessionEventReporter> _reporters;

    public CompositeSessionEventReporter(IEnumerable<ISessionEventReporter> reporters) =>
        _reporters = reporters.ToList();

    public async Task ReportAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default)
    {
        foreach (var reporter in _reporters)
            await reporter.ReportAsync(sessionEvent, cancellationToken);
    }
}
