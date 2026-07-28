using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using UnifyEmpi.Application;
using UnifyEmpi.Domain;
using UnifyEmpi.Storage.Abstractions;

namespace UnifyEmpi.Api;

public sealed class RegistryMaintenanceWorker(
    RegistryMaintenanceService maintenance,
    IReadOnlyDictionary<TenantId, TenantMatchingConfiguration> tenants,
    IOptions<RegistryMaintenanceOptions> options,
    TimeProvider timeProvider,
    ILogger<RegistryMaintenanceWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogPollingFailure =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1101, nameof(LogPollingFailure)),
            "Registry maintenance polling failed; the next polling cycle will retry.");

    private readonly string _workerId =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.CreateVersion7():N}";
    private readonly DateTimeOffset _startedAt = timeProvider.GetUtcNow();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configured = options.Value;
        if (!configured.WorkerEnabled)
        {
            return;
        }

        ValidateOptions(configured, tenants);
        var pollInterval = TimeSpan.FromSeconds(configured.PollIntervalSeconds);
        var leaseDuration = TimeSpan.FromSeconds(configured.LeaseSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnqueueDueSchedulesAsync(configured, stoppingToken);
                foreach (var tenant in tenants.Keys)
                {
                    await maintenance.ProcessNextBatchAsync(
                        CreateWorkerContext(tenant),
                        _workerId,
                        leaseDuration,
                        stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogPollingFailure(logger, exception);
            }

            await Task.Delay(pollInterval, timeProvider, stoppingToken);
        }
    }

    private async ValueTask EnqueueDueSchedulesAsync(
        RegistryMaintenanceOptions configured,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var schedule in configured.ReconciliationSchedules)
        {
            var tenantId = new TenantId(schedule.TenantId);
            var context = CreateWorkerContext(tenantId);
            var completed = await maintenance.SearchJobsAsync(
                context,
                new MaintenanceJobSearch(
                    RegistryMaintenanceJobKind.PopulationReconciliation,
                    RegistryMaintenanceJobStatus.Completed,
                    ScheduleKey: schedule.Key,
                    Count: 25),
                cancellationToken);
            var latest = completed.Items
                .Where(job => string.Equals(
                    job.ScheduleKey,
                    schedule.Key,
                    StringComparison.Ordinal))
                .OrderByDescending(static job => job.CompletedAt)
                .FirstOrDefault();
            var interval = TimeSpan.FromMinutes(schedule.IntervalMinutes);
            if (latest is null &&
                !schedule.RunOnStartup &&
                _startedAt.Add(interval) > now ||
                latest?.CompletedAt is { } completedAt && completedAt.Add(interval) > now)
            {
                continue;
            }

            SourceSystemId? sourceSystem = string.IsNullOrWhiteSpace(schedule.SourceSystem)
                ? null
                : new SourceSystemId(schedule.SourceSystem);
            var bucket = now.UtcTicks / interval.Ticks;
            var deterministicId = CreateScheduledJobId(tenantId, schedule.Key, bucket);
            try
            {
                await maintenance.StartPopulationReconciliationAsync(
                    context,
                    new StartPopulationReconciliationCommand(
                        $"Scheduled population reconciliation '{schedule.Key}'.",
                        schedule.BatchSize,
                        sourceSystem,
                        latest?.WindowEnd,
                        RegistryMaintenanceTrigger.Scheduled,
                        schedule.Key,
                        deterministicId),
                    cancellationToken);
            }
            catch (RegistryConcurrencyException)
            {
                // Another replica already owns an active job or created this schedule bucket.
            }
        }
    }

    private ActorContext CreateWorkerContext(TenantId tenant) =>
        new(
            tenant,
            $"maintenance-worker:{_workerId}",
            null,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "mpi.admin",
                "mpi.operations",
                "mpi.audit",
                "mpi.review"
            },
            Guid.CreateVersion7().ToString("N"));

    private static Guid CreateScheduledJobId(TenantId tenant, string key, long bucket)
    {
        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{tenant.Value}\0{key}\0{bucket}"));
        Span<byte> guidBytes = stackalloc byte[16];
        digest.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    private static void ValidateOptions(
        RegistryMaintenanceOptions configured,
        IReadOnlyDictionary<TenantId, TenantMatchingConfiguration> tenants)
    {
        if (configured.PollIntervalSeconds is < 1 or > 60 ||
            configured.LeaseSeconds is < 10 or > 600)
        {
            throw new InvalidOperationException(
                "Maintenance polling must be 1-60 seconds and leases 10-600 seconds.");
        }

        var scheduleKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schedule in configured.ReconciliationSchedules)
        {
            var tenant = new TenantId(schedule.TenantId);
            if (string.IsNullOrWhiteSpace(schedule.Key) ||
                !scheduleKeys.Add($"{tenant.Value}\0{schedule.Key}") ||
                !tenants.ContainsKey(tenant) ||
                schedule.IntervalMinutes is < 1 or > 525600 ||
                schedule.BatchSize is < 1 or > 25)
            {
                throw new InvalidOperationException(
                    "Maintenance reconciliation schedules must have a unique tenant/key, a configured tenant, a 1-525600 minute interval, and a 1-25 batch size.");
            }
        }
    }
}
