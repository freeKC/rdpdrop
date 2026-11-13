using UltimateRDP.Client.Loader;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;
using UltimateRDP.Client.Sessions;
using UltimateRDP.Core.RdpFiles;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Client;

/// <summary>
/// One connection per server and account, since 0.10.0. Windows gives a user one session per
/// server: a second connection with the same account takes the session of the first, which
/// ends with reason 3 and extended reason 5, and the 0.9.3 log showed both sessions gone at a
/// second click on the same RemoteApp launcher. As mstsc does, a RemoteApp launch to the target
/// of a RemoteApp tab that runs starts its program on that connection
/// (<see cref="SessionView.JoinRemoteApp"/>), with a loader of its own that says "Starting
/// {program}..." until a new window of a program appears; a desktop opened again to the target
/// of a desktop tab that runs selects that tab.
/// </summary>
internal sealed partial class MainForm
{
    private readonly Dictionary<JoinedLaunch, (SessionView Host, LoaderForm Loader)> _joinedLoaders = [];

    /// <summary>
    /// True when <paramref name="source"/>, read as <paramref name="file"/>, goes to a
    /// connection that runs instead of a new tab.
    /// </summary>
    private bool JoinsExistingConnection(SessionSource source, RdpFile file, bool askCredentials)
    {
        var target = ConnectionTarget.FromRdpFile(file);
        var launch = RemoteAppLaunch.FromFile(file, out _);
        if (launch is null)
        {
            return SelectsOpenDesktop(target);
        }

        var host = Sessions.FirstOrDefault(session => session.TakesRemoteAppLaunches && ConnectionTarget.Matches(session.Target, target));
        if (host is null)
        {
            var otherAccount = Sessions.Any(session => session.TakesRemoteAppLaunches
                && ConnectionTarget.Matches(session.Target with { User = string.Empty, Domain = string.Empty }, target with { User = string.Empty, Domain = string.Empty }));
            if (otherAccount)
            {
                ClientLog.Info($"RemoteApp launch of {launch.DisplayName} opens its own connection: different account ({target}).");
            }

            return false;
        }

        var joined = new JoinedLaunch(launch, source, askCredentials);
        ClientLog.Info($"RemoteApp launch of {launch.DisplayName} joins the connection of {host.Title}: same target ({target}){(askCredentials ? "; the password prompt asked by the command line does not apply to a connection that runs" : string.Empty)}.");
        AttachJoinedLoader(host, joined);
        if (host.JoinRemoteApp(joined) == JoinDecision.OpenOwn)
        {
            FinishJoinedLoader(joined, "the connection it joined ended");
            return false;
        }

        _startupLoader?.Finish("the launch joined a connection that runs");
        _startupLoader = null;
        _tray?.UpdateText();
        return true;
    }

    /// <summary>A desktop opened again while a desktop tab to the same target runs: that tab is selected, with a notice.</summary>
    private bool SelectsOpenDesktop(ConnectionTarget target)
    {
        var open = Sessions.FirstOrDefault(session => !session.IsRemoteApp && !session.IsClosing && session.State != SessionState.Disconnected
            && ConnectionTarget.Matches(session.Target, target));
        if (open?.Parent is not TabPage page)
        {
            return false;
        }

        ClientLog.Info($"The desktop of {target} is open already in a tab: that tab is selected instead of a second connection, which would take its session.");
        _startupLoader?.Finish("the desktop is open already");
        _startupLoader = null;
        _tabs.SelectedTab = page;
        open.ShowNotice(LocalText.Of(nameof(Strings.DesktopAlreadyOpen)));
        ShowMainWindow("the desktop asked for is open already");
        return true;
    }

    /// <summary>Follows the launches that join <paramref name="session"/>, and opens a connection for those its end leaves.</summary>
    private void FollowJoinedLaunches(SessionView session)
    {
        session.JoinedLaunchStarted += (_, joined) => OnJoinedLoaderEvent(joined, LoaderEvent.LoggedOn);
        session.JoinedLaunchEnded += (_, joined) => FinishJoinedLoader(joined, joined.Start.End switch
        {
            JoinedStartEnd.NewWindow => "a new window of a program appeared",
            JoinedStartEnd.AnsweredOk => "the server started the program without a new window",
            JoinedStartEnd.Failed => "the server could not start the program",
            _ => "no window came in time",
        });
        session.ConnectedToServer += (_, _) =>
        {
            foreach (var joined in _joinedLoaders.Where(pair => pair.Value.Host == session).Select(pair => pair.Key).ToList())
            {
                OnJoinedLoaderEvent(joined, LoaderEvent.Connected);
            }
        };
        session.JoinedLaunchesOrphaned += (_, orphans) =>
        {
            foreach (var orphan in orphans)
            {
                FinishJoinedLoader(orphan, "the connection it joined ended; it opens its own");
                OpenSession(orphan.Source, orphan.AskCredentials);
            }
        };
        session.CloseCompleted += (_, _) =>
        {
            foreach (var joined in _joinedLoaders.Where(pair => pair.Value.Host == session).Select(pair => pair.Key).ToList())
            {
                FinishJoinedLoader(joined, "the tab of the connection closed");
            }
        };
    }

    /// <summary>The loader of a launch that joins <paramref name="host"/>: it starts at the step of the connection.</summary>
    private void AttachJoinedLoader(SessionView host, JoinedLaunch joined)
    {
        if (!_settings.LoaderOn)
        {
            return;
        }

        var loader = new LoaderForm(joined.Name);
        loader.FileRead(host.Host, joined.Name);
        loader.On(LoaderEvent.Connecting);
        if (host.State == SessionState.Connected)
        {
            loader.On(LoaderEvent.Connected);
        }

        // Cancel would close the connection of the other programs: the loader of a launch that
        // joins has none.
        loader.CanCancel = false;
        loader.ShowNow(LoaderOwner);
        _joinedLoaders[joined] = (host, loader);
    }

    private void OnJoinedLoaderEvent(JoinedLaunch joined, LoaderEvent loaderEvent)
    {
        if (_joinedLoaders.TryGetValue(joined, out var entry))
        {
            entry.Loader.On(loaderEvent);
        }
    }

    private void FinishJoinedLoader(JoinedLaunch joined, string reason)
    {
        if (_joinedLoaders.Remove(joined, out var entry))
        {
            entry.Loader.Finish(reason);
        }
    }
}
