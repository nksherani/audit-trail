# Local SQL Server performance baseline

Measured 16 October 2026 using .NET 10.0.303 / EF Core 10.0.11 and the local
SQL Server instance. These are single-run development measurements, not deployment
guarantees. Tests warm each insert path once; all compared inserts use the same
Category table/fields/rowversion. Baseline writes deliberately bypass auditing in
the test-only baseline context.

| Workload | Rows | Elapsed |
| --- | ---: | ---: |
| Plain EF insert, same entity schema | 1,000 | 83.5 ms |
| Audited tracked insert | 1,000 | 616.6 ms |
| Audited SQL OUTPUT bulk update | 1,000 | 233.0 ms |
| History first page, 50 entries | 1,000 matching entries | 101.4 ms |

The audited path produced exactly one insert snapshot per business entity, then
one update record per changed property in the bulk operation. Old/new value text
for the measured audit workload occupied approximately 290,040 UTF-16 payload
bytes. This excludes row overhead, indexes, metadata columns, receipts, database
pages and transaction logs; it is not a whole-database size estimate.

A separate 1,000-row bulk update deliberately failed audit insertion and verified
that every affected business row rolled back. Operation replay verified no
duplicate audit rows. Additional tests inject command timeouts and commit-ack loss.

Reproduce with:

```powershell
dotnet test tests/AuditTrail.Tests --filter Category=Performance --logger 'console;verbosity=detailed'
```

The test prints current measurements without asserting arbitrary latency limits.
Establish p95/p99 latency, concurrency, lock-wait, transaction-log growth and
storage budgets from the target environment's representative workload before
selecting a production SLO. Use the framework meter and SQL Server monitoring
to track those budgets; this single-client local sample does not measure peak
throughput or production contention.
