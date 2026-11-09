namespace UltimateRDP.Agent.Channel;

/// <summary>
/// Decides whether a failure deserves a visible log line: the first occurrence of a
/// failure is reported, identical failures after it are not, until <see cref="Reset"/>
/// is called after a success.
/// </summary>
internal sealed class RepeatedFailureFilter
{
    private string? _lastKey;

    /// <summary>True the first time <paramref name="key"/> is seen in a row.</summary>
    public bool IsNew(string key)
    {
        if (string.Equals(key, _lastKey, StringComparison.Ordinal))
        {
            return false;
        }

        _lastKey = key;
        return true;
    }

    public void Reset() => _lastKey = null;
}
