using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging;

namespace ZavaFinance.Channel;

/// <summary>Ordinary, tool-neutral progress messages, stopped and joined before final delivery.</summary>
public sealed class TurnProgress : IAsyncDisposable
{
    private static readonly (TimeSpan After, string Text)[] Updates =
    [
        (TimeSpan.FromSeconds(15), "Still working — this can take a minute."),
        (TimeSpan.FromSeconds(90), "Still going. Complex questions can take a few minutes to come back.")
    ];

    private readonly ITurnContext _turnContext;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop;
    private readonly Task _loop;
    private readonly object _gate = new();
    private Task? _disposal;

    private TurnProgress(
        ITurnContext turnContext, ILogger logger, TimeProvider timeProvider, CancellationToken ct)
    {
        _turnContext = turnContext;
        _logger = logger;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = RunAsync(timeProvider, _stop.Token);
    }

    public static TurnProgress Start(
        ITurnContext turnContext, ILogger logger, TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
        => new(turnContext, logger, timeProvider, cancellationToken);

    private async Task RunAsync(TimeProvider timeProvider, CancellationToken ct)
    {
        long started = timeProvider.GetTimestamp();
        try
        {
            foreach ((TimeSpan after, string text) in Updates)
            {
                TimeSpan remaining = after - timeProvider.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, timeProvider, ct);
                }

                ct.ThrowIfCancellationRequested();
                try
                {
                    await _turnContext.SendActivityAsync(MessageFactory.Text(text), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Progress message not delivered.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new ValueTask(_disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await _stop.CancelAsync();
            await _loop;
        }
        finally
        {
            _stop.Dispose();
        }
    }
}
