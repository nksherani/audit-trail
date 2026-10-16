using System.Diagnostics.Metrics;
namespace AuditTrail.Infrastructure.Auditing;
public static class AuditDiagnostics
{
    public static readonly Meter Meter = new("AuditTrail.Framework", "1.0");
    public static readonly Counter<long> Records = Meter.CreateCounter<long>("audit.records");
    public static readonly Counter<long> Failures = Meter.CreateCounter<long>("audit.failures");
    public static readonly Histogram<double> BulkMilliseconds = Meter.CreateHistogram<double>("audit.bulk.duration", "ms");
    public static readonly Histogram<double> QueryMilliseconds = Meter.CreateHistogram<double>("audit.query.duration", "ms");
}
