using System;
using System.Threading;
using System.Threading.Tasks;
using Broiler.Documents.FormatCodes;

namespace Broiler.Writer.FormatCodes;

/// <summary>Schedules projections that exceed the synchronous projection policy.</summary>
public interface IWriterFormatCodesScheduler
{
    Task<FormatCodeProjection> Schedule(
        Func<FormatCodeProjection> projection,
        CancellationToken cancellationToken);
}

/// <summary>Default scheduler used by desktop and browser Writer hosts.</summary>
public sealed class WriterFormatCodesScheduler : IWriterFormatCodesScheduler
{
    public Task<FormatCodeProjection> Schedule(
        Func<FormatCodeProjection> projection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return Task.Run(projection, cancellationToken);
    }
}
