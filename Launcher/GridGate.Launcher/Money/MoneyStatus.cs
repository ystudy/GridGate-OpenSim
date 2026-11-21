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
    private bool _serviceRunning;
    private (DateTimeOffset At, SelfCheckResult Result)? _lastCheck;

    public string? ServiceError
    {
        get { lock (_gate) return _serviceError; }
        set { lock (_gate) _serviceError = value; }
    }

    /// <summary>
    /// Whether the money service started in this run. Money turned on after GridGate started isn't running until a restart
    /// (plan 5 starts and stops it with the mode); the dashboard says so instead of reporting a database fault.
    /// </summary>
    public bool ServiceRunning
    {
        get { lock (_gate) return _serviceRunning; }
        set { lock (_gate) _serviceRunning = value; }
    }

    /// <summary>
    /// Money is on but waits for a restart: the service didn't start in this run and nothing failed, so its database may
    /// not exist yet. Pages say <see cref="MoneyPageRules.StartsOnRestart"/> and don't ask the database.
    /// </summary>
    public bool WaitingForRestart
    {
        get { lock (_gate) return !_serviceRunning && _serviceError is null; }
    }

    public (DateTimeOffset At, SelfCheckResult Result)? LastCheck
    {
        get { lock (_gate) return _lastCheck; }
        set { lock (_gate) _lastCheck = value; }
    }
}
