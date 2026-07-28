using Agentwerke.Application.Observability;
using Agentwerke.Application.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentwerke.Infrastructure.Workers;

public sealed class WaitingExternalMonitorOptions
{
    public const string Section = "WaitingExternalMonitor";

    /// <summary>How often the parked-wait population is re-measured.</summary>
    public int SweepIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// A run parked on an external event for longer than this is counted in the
    /// <c>workflow.runs.waiting_external.stale</c> gauge, so ops can alert on waits that are
    /// stuck even when the workflow declares no boundary timer.
    /// </summary>
    public int StaleAfterMinutes { get; set; } = 60;
}

/// <summary>
/// Publishes how many runs are parked in <c>waiting_external</c> and how long the oldest has been
/// waiting (#208). A boundary timer bounds an individual wait; this gauge is what surfaces waits
/// that nobody armed a timer for.
/// </summary>
public sealed class WaitingExternalMonitor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkflowMetrics _metrics;
    private readonly WaitingExternalMonitorOptions _options;
    private readonly ILogger<WaitingExternalMonitor> _logger;

    public WaitingExternalMonitor(
        IServiceScopeFactory scopeFactory,
        IWorkflowMetrics metrics,
        IOptions<WaitingExternalMonitorOptions> options,
        ILogger<WaitingExternalMonitor> logger)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.SweepIntervalSeconds));
        var staleAfter = TimeSpan.FromMinutes(Math.Max(1, _options.StaleAfterMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(staleAfter, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while measuring waiting_external runs");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task SweepAsync(TimeSpan staleAfter, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWaitingExternalCorrelationRepository>();

        var waiting = await repository.ListWaitingAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var ages = waiting
            .Select(w => DateTimeOffset.TryParse(
                w.CreatedAt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var createdAt)
                ? now - createdAt
                : TimeSpan.Zero)
            .ToList();

        var stale = ages.Count(age => age >= staleAfter);
        var oldest = ages.Count == 0 ? TimeSpan.Zero : ages.Max();

        _metrics.RecordWaitingExternalRuns(waiting.Count, stale, oldest.TotalSeconds);

        if (stale > 0)
        {
            _logger.LogWarning(
                "{StaleCount} of {TotalCount} runs have been parked in waiting_external for over {StaleAfter}; oldest {OldestMinutes:F0}m",
                stale, waiting.Count, staleAfter, oldest.TotalMinutes);
        }
    }
}
