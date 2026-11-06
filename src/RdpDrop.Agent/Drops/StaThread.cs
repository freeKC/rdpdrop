namespace RdpDrop.Agent.Drops;

/// <summary>
/// Starts background work on its own single threaded apartment, which the shell COM
/// objects and the Windows copy dialogs need.
/// </summary>
internal static class StaThread
{
    public static void Start(string name, Action work)
    {
        var thread = new Thread(() => work())
        {
            IsBackground = true,
            Name = name,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
