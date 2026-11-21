using Microsoft.Extensions.Logging;

namespace GridGate.Launcher.Core.Guests;

/// <summary>Whether a resident's profile is on the website's Residents page (spec 2026-11-21-resident-profiles-design).</summary>
public enum PublicProfileState
{
    /// <summary>Not shown: everyone starts here.</summary>
    Off,

    /// <summary>Shown: the resident turned it on from their Account page.</summary>
    On,

    /// <summary>Staff took it off the page; the resident can't turn it on again until staff allow it.</summary>
    Held,
}

/// <summary>
/// Each resident's Residents page choice, by account id (spec 2026-11-21-resident-profiles-design, "Who shows"), kept in
/// <c>&lt;data&gt;/guests/public-profiles.json</c> through <see cref="JsonStateFile{T}"/> as <see cref="ResidentLanguages"/> keeps
/// languages: read once at start, held in memory, written atomically on every change. An account not in the file is
/// <see cref="PublicProfileState.Off"/>, so nobody is published who didn't ask. Safe to call from any thread.
/// </summary>
public sealed class PublicProfileChoices
{
    private readonly JsonStateFile<Dictionary<Guid, PublicProfileState>> _file;
    private readonly Dictionary<Guid, PublicProfileState> _states;
    private readonly Lock _gate = new();

    public PublicProfileChoices(string file, ILogger<PublicProfileChoices>? log = null)
    {
        _file = new JsonStateFile<Dictionary<Guid, PublicProfileState>>(file, () => [], log);
        _states = _file.Load();
        // A value the file doesn't know (a hand edit) reads as off, never as shown.
        foreach (var (id, state) in _states.ToList())
            if (!Enum.IsDefined(state)) _states[id] = PublicProfileState.Off;
    }

    public PublicProfileState StateOf(Guid principal)
    {
        lock (_gate)
            return _states.GetValueOrDefault(principal, PublicProfileState.Off);
    }

    /// <summary>The resident's own switch. False (and nothing changed) when staff hold their profile.</summary>
    public bool Choose(Guid principal, bool shown)
    {
        lock (_gate)
        {
            var now = _states.GetValueOrDefault(principal, PublicProfileState.Off);
            if (now == PublicProfileState.Held) return false;
            var next = shown ? PublicProfileState.On : PublicProfileState.Off;
            if (now != next) Write(principal, next);
            return true;
        }
    }

    /// <summary>Staff take the profile off the page; the resident can't turn it on again.</summary>
    public void Hold(Guid principal)
    {
        lock (_gate)
            Write(principal, PublicProfileState.Held);
    }

    /// <summary>Staff allow it again: back to <see cref="PublicProfileState.Off"/>, so the resident chooses afresh.</summary>
    public void Release(Guid principal)
    {
        lock (_gate)
            if (_states.GetValueOrDefault(principal) == PublicProfileState.Held)
                Write(principal, PublicProfileState.Off);
    }

    /// <summary>Forgets the account (when it is deleted).</summary>
    public void Forget(Guid principal)
    {
        lock (_gate)
            if (_states.Remove(principal))
                _file.Save(_states);
    }

    /// <summary>The accounts that chose to be shown, in no particular order.</summary>
    public IReadOnlyList<Guid> Shown()
    {
        lock (_gate)
            return [.. _states.Where(s => s.Value == PublicProfileState.On).Select(s => s.Key)];
    }

    private void Write(Guid principal, PublicProfileState state)
    {
        if (state == PublicProfileState.Off) _states.Remove(principal);
        else _states[principal] = state;
        _file.Save(_states);
    }
}
