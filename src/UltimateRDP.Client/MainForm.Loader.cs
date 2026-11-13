using UltimateRDP.Client.Loader;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Sessions;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Client;

/// <summary>
/// The loader of each session that opens, since 0.9.3: the loader of the start, shown by
/// <see cref="Program"/> before this window existed, goes to the first session, and every
/// session opened later gets one of its own, a second start handed to this one included. The
/// loader follows the events of its session, and closes at the logon of a desktop, at the first
/// window of a RemoteApp program, or at any end before. While the main window is on a monitor,
/// the loader sits over it, owned by it, so that the tab of a desktop that connects does not show
/// its black surface alone; out of sight, it sits in the middle of the monitor of the mouse.
/// </summary>
internal sealed partial class MainForm
{
    private readonly Dictionary<SessionView, LoaderForm> _loaders = [];
    private LoaderForm? _startupLoader;

    /// <summary>The main window, when it is on a monitor; null while it is out of sight.</summary>
    private Form? LoaderOwner => _offScreen || !Visible ? null : this;

    /// <summary>Gives <paramref name="session"/> its loader: the one of the start for the first session, a new one otherwise.</summary>
    private void AttachLoader(SessionView session)
    {
        var loader = _startupLoader;
        _startupLoader = null;
        if (loader is null || loader.IsDisposed || loader.Progress.IsClosed)
        {
            if (!_settings.LoaderOn)
            {
                return;
            }

            loader = new LoaderForm(session.Title);
            loader.FileRead(session.Host, session.RemoteAppName);
            loader.ShowNow(LoaderOwner);
        }
        else
        {
            loader.FileRead(session.Host, session.RemoteAppName);
            loader.PlaceOver(LoaderOwner);
        }

        loader.CanCancel = true;
        loader.CancelRequested += (_, _) =>
        {
            FinishLoader(session, "Cancel");
            session.BeginClose();
        };
        _loaders[session] = loader;
        session.ConnectedToServer += (_, _) => OnLoaderEvent(session, LoaderEvent.Connected);
        session.LoggedOn += (_, _) => OnLoaderEvent(session, LoaderEvent.LoggedOn);
        session.RemoteProgramShown += (_, _) => OnLoaderEvent(session, LoaderEvent.FirstProgramWindow);
        session.CloseCompleted += (_, _) => FinishLoader(session, "the tab closed");
    }

    /// <summary>The loader follows the state of its session: Connecting, and any end before the start is over.</summary>
    private void OnSessionStateForLoader(SessionView session)
    {
        if (!_loaders.TryGetValue(session, out var loader))
        {
            return;
        }

        if (session.State == SessionState.Connecting)
        {
            loader.On(LoaderEvent.Connecting);
        }
        else if (session.State == SessionState.Disconnected && (session.ConnectFailed || loader.Progress.Step != LoaderStep.Starting))
        {
            FinishLoader(session, session.ConnectFailed ? "the connection could not start" : "the session ended before the start was over");
        }

        Forget(session, loader);
    }

    private void OnLoaderEvent(SessionView session, LoaderEvent loaderEvent)
    {
        if (_loaders.TryGetValue(session, out var loader))
        {
            loader.On(loaderEvent);
            Forget(session, loader);
        }
    }

    private void FinishLoader(SessionView session, string reason)
    {
        if (_loaders.Remove(session, out var loader))
        {
            loader.Finish(reason);
        }
    }

    /// <summary>A loader that closed is no longer followed.</summary>
    private void Forget(SessionView session, LoaderForm loader)
    {
        if (loader.Progress.IsClosed || loader.IsDisposed)
        {
            _loaders.Remove(session);
        }
    }

    /// <summary>The main window came on a monitor, or went out of sight: the loaders go over it, or back to the middle of the monitor.</summary>
    private void PlaceLoaders()
    {
        foreach (var loader in _loaders.Values.Append(_startupLoader).OfType<LoaderForm>().Where(loader => !loader.IsDisposed))
        {
            loader.PlaceOver(LoaderOwner);
        }
    }

    /// <summary>Closes every loader, when the window closes.</summary>
    private void FinishLoaders(string reason)
    {
        _startupLoader?.Finish(reason);
        _startupLoader = null;
        foreach (var session in _loaders.Keys.ToList())
        {
            FinishLoader(session, reason);
        }

        ClientLog.Info($"Loaders closed: {reason}.");
    }
}
