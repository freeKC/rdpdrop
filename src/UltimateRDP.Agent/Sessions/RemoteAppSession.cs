namespace UltimateRDP.Agent.Sessions;

/// <summary>
/// Whether the agent runs in a RemoteApp session: from its own look at the processes of the
/// session, and from the hello of the client. Read by the drag threads, set by the UI thread
/// and the channel thread.
/// </summary>
internal sealed class RemoteAppSession
{
    private volatile bool _detected;
    private volatile bool _clientSays;

    /// <summary>Raised on the thread that changed a value, when one changed.</summary>
    public event Action? Changed;

    /// <summary>True once the agent found the RemoteApp shell and no Explorer in its session.</summary>
    public bool Detected => _detected;

    /// <summary>True when the hello of the client said that it runs a RemoteApp program.</summary>
    public bool ClientSays => _clientSays;

    /// <summary>
    /// True when the drag is replayed at the drop with a small proxy window: in a RemoteApp
    /// session, whoever says so first.
    /// </summary>
    public bool ReplayAtDrop => _detected || _clientSays;

    /// <summary>The agent found the RemoteApp shell and no Explorer in its session.</summary>
    public void SetDetected()
    {
        if (!_detected)
        {
            _detected = true;
            Changed?.Invoke();
        }
    }

    /// <summary>The hello of the client says whether it runs a RemoteApp program.</summary>
    public void SetClientSays(bool value)
    {
        if (_clientSays != value)
        {
            _clientSays = value;
            Changed?.Invoke();
        }
    }
}
