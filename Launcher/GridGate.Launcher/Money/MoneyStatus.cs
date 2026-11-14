using GridGate.Launcher.Core.Money;

namespace GridGate.Launcher.Money;

/// <summary>
/// What the dashboard says about money (money plan 2, task 1): whether the service failed to start, and the last
/// self-check. <see cref="ServiceError"/> is only ever an exception's type name, never its message.
/// </summary>
public sealed class MoneyStatus
{
    private readonly Lock _gate = new();
    private string? _serviceError;
    private (DateTimeOffset At, SelfCheckResult Result)? _lastCheck;

    public string? ServiceError
    {
        get { lock (_gate) return _serviceError; }
        set { lock (_gate) _serviceError = value; }
    }

    public (DateTimeOffset At, SelfCheckResult Result)? LastCheck
    {
        get { lock (_gate) return _lastCheck; }
        set { lock (_gate) _lastCheck = value; }
    }
}
