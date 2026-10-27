namespace Recorrencia.Api.Integracoes;

/// <summary>One shared request pace for the integration, including background imports.</summary>
public sealed class HinovaRequestThrottle(TimeProvider clock, IConfiguration configuration)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(
        Math.Clamp(configuration.GetValue("Hinova:MinRequestIntervalMs", 2000), 250, 60000));
    private DateTimeOffset _next;

    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var delay = _next - clock.GetUtcNow();
            if (delay > TimeSpan.Zero) await Task.Delay(delay, clock, ct);
            _next = clock.GetUtcNow() + _interval;
        }
        finally { _gate.Release(); }
    }
}
