using System.Diagnostics;
using System.Globalization;
using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;
using UltimateRDP.Client.Settings;
using UltimateRDP.Core.Agent;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// The files that the session sends to this computer, since 0.9.0. The agent offers them with
/// a sendOffer through the channel of this tab; the client checks the offer
/// (<see cref="ReceivePolicy.Check"/>), answers with a sendAck, then receives the files through
/// the direct connection with a <see cref="DirectDownload"/>, in a progress window of its own.
/// The client takes nothing that it did not acknowledge, writes only into the folder for
/// received files, never runs anything it received, and marks every file with the Mark of
/// the Web.
/// </summary>
internal sealed partial class SessionView
{
    private const int ReceiveTickMilliseconds = 250;

    private readonly System.Windows.Forms.Timer _receiveTimer = new() { Interval = ReceiveTickMilliseconds };
    private bool _receivesFiles;
    private string _receiveFolder = string.Empty;
    private DirectDownload? _download;
    private DropProgressForm? _receiveWindow;

    /// <summary>True while files of the session are being received in this tab.</summary>
    public bool IsReceiving => _download is not null;

    /// <summary>Reads the settings of the reception and subscribes to the offers. Called by the constructor.</summary>
    private void InitializeReceive(ClientSettings settings)
    {
        _receivesFiles = settings.ReceivesFiles;
        _receiveFolder = settings.ReceiveFolderPath;
        _agent.AcceptsFiles = _receivesFiles;
        _agent.SendOfferReceived += (_, offer) => OnSendOffer(offer);
        _agent.SendCancelReceived += (_, cancel) => OnSendCancel(cancel);
        _receiveTimer.Tick += (_, _) => ShowReceiveProgress();
    }

    private void DisposeReceive()
    {
        _receiveTimer.Dispose();
        StopReceive(cancel: true, "the tab closed");
        if (_receiveWindow is { IsDisposed: false } window)
        {
            window.Close();
        }
    }

    /// <summary>The session offers files: they are received, or refused with the reason. UI thread.</summary>
    private void OnSendOffer(SendOfferMessage offer)
    {
        var folderProblem = PrepareReceiveFolder(out var free);
        var refusal = folderProblem is null ? ReceivePolicy.Check(_receivesFiles, _download is not null, offer, free) : ReceiveRefusal.NoFolder;
        if (refusal != ReceiveRefusal.None)
        {
            var text = refusal switch
            {
                ReceiveRefusal.Off => LocalText.Of(nameof(Strings.ReceiveRefusedOff)),
                ReceiveRefusal.Busy => LocalText.Of(nameof(Strings.ReceiveRefusedBusy)),
                ReceiveRefusal.NotDirect => LocalText.Of(nameof(Strings.ReceiveRefusedNotDirect)),
                ReceiveRefusal.NoSpace => LocalText.Of(nameof(Strings.ReceiveRefusedNoSpace), _receiveFolder, FastTransferPolicy.RequiredFreeSpace(offer.Bytes) / 1_000_000.0, (free ?? 0) / 1_000_000.0),
                _ => LocalText.Of(nameof(Strings.ReceiveRefusedNoFolder), _receiveFolder, folderProblem ?? string.Empty),
            };
            ClientLog.Info($"Send offer {offer.Id} refused: {refusal}, {text.ToEnglish()}");
            _agent.SendReceiveAnswer(new SendAckMessage(offer.Id, false, text.ToString()), offer.Id);
            ShowDropStatus(LocalText.Of(nameof(Strings.ReceiveRefused), text));
            return;
        }

        DirectDownload download;
        try
        {
            download = new DirectDownload(this, offer, Host, _receiveFolder, _directConnections);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            var text = LocalText.Of(nameof(Strings.ReceiveRefusedNoFolder), _receiveFolder, ex.Message);
            ClientLog.Warn($"Send offer {offer.Id}: the part folder cannot be created in {_receiveFolder}. {ex.Message}");
            _agent.SendReceiveAnswer(new SendAckMessage(offer.Id, false, text.ToString()), offer.Id);
            ShowDropStatus(LocalText.Of(nameof(Strings.ReceiveRefused), text));
            return;
        }

        if (!_agent.SendReceiveAnswer(new SendAckMessage(offer.Id, true), offer.Id))
        {
            ClientLog.Warn($"Send offer {offer.Id}: the answer could not be sent; nothing is received.");
            download.Stop("the answer could not be sent");
            return;
        }

        ClientLog.Info(string.Create(CultureInfo.InvariantCulture, $"Receive {offer.Id}: accepted, {offer.Items.Count} item(s), {offer.Files} file(s), {offer.Bytes} bytes into {_receiveFolder}, part folder {download.PartFolder}, port {offer.Direct.Port} of {Host}{(offer.Direct.Host is { } hint ? ", address hint " + hint : string.Empty)}, certificate pinned to SHA-256 {offer.Direct.CertSha256}."));
        _download = download;
        download.NotAvailable += (d, reason) => OnReceiveNotAvailable(d, reason);
        download.Failed += (d, text) => OnReceiveFailed(d, text);
        download.Completed += (d, paths) => OnReceiveCompleted(d, paths);
        OpenReceiveWindow(offer);
        ShowDropStatus(LocalText.Joined(LocalText.Of(nameof(Strings.ReceivePreparing), offer.Items.Count), LocalText.Of(nameof(Strings.TransferDirect))));
        _receiveTimer.Start();
        download.Start();
        RaiseStateChanged();
    }

    /// <summary>The agent gave up the send.</summary>
    private void OnSendCancel(SendCancelMessage cancel)
    {
        if (_download is not { } download || download.SendId != cancel.Id)
        {
            return;
        }

        StopReceive(cancel: false, "the agent cancelled the send through the channel");
        var text = LocalText.Of(nameof(Strings.ReceiveCancelledBySession), cancel.Reason ?? string.Empty);
        _receiveWindow?.ShowFailure(text);
        ShowDropStatus(text);
    }

    /// <summary>The user cancels the reception, from its window. UI thread.</summary>
    private void CancelReceive()
    {
        if (_download is not { } download)
        {
            return;
        }

        StopReceive(cancel: true, "The user cancelled the transfer.");
        _agent.SendReceiveAnswer(new SendCancelMessage(download.SendId, "The user cancelled the transfer."), download.SendId);
        var text = LocalText.Of(nameof(Strings.TransferCancelledByUser));
        _receiveWindow?.ShowFailure(text);
        ShowDropStatus(text);
    }

    /// <summary>Stops the reception in progress; with <paramref name="cancel"/> the session gets the cancel of the client.</summary>
    private void StopReceive(bool cancel, string reason)
    {
        if (_download is not { } download)
        {
            return;
        }

        _download = null;
        _receiveTimer.Stop();
        if (cancel)
        {
            download.Cancel(reason);
        }
        else
        {
            download.Stop(reason);
        }

        RaiseStateChanged();
    }

    /// <summary>The tab closes: the session is told while the channel still works.</summary>
    private void CancelReceiveForClose()
    {
        if (_download is { } download)
        {
            StopReceive(cancel: true, "the tab closed");
            _agent.SendReceiveAnswer(new SendCancelMessage(download.SendId, "The tab of the client closed."), download.SendId);
        }
    }

    /// <summary>The session was disconnected: a reception in progress ends.</summary>
    private void OnReceiveDisconnected()
    {
        if (_download is null)
        {
            return;
        }

        StopReceive(cancel: true, "the session was disconnected");
        var text = LocalText.Of(nameof(Strings.TransferInterrupted));
        _receiveWindow?.ShowFailure(text);
    }

    private void OnReceiveNotAvailable(DirectDownload download, string reason)
    {
        if (_download != download)
        {
            return;
        }

        _download = null;
        _receiveTimer.Stop();
        _agent.SendReceiveAnswer(new SendCancelMessage(download.SendId, reason, NotAvailable: true), download.SendId);
        var text = LocalText.Of(nameof(Strings.ReceiveNotAvailable), reason);
        ClientLog.Warn($"Receive {download.SendId}: the direct connection cannot be used, {reason}; there is no other path for files of the session.");
        _receiveWindow?.ShowFailure(text);
        ShowDropStatus(text);
        RaiseStateChanged();
    }

    private void OnReceiveFailed(DirectDownload download, LocalText text)
    {
        if (_download != download)
        {
            return;
        }

        _download = null;
        _receiveTimer.Stop();
        _receiveWindow?.ShowFailure(text);
        ShowDropStatus(LocalText.Of(nameof(Strings.ReceiveFailed), text));
        RaiseStateChanged();
    }

    private void OnReceiveCompleted(DirectDownload download, IReadOnlyList<string> paths)
    {
        if (_download != download)
        {
            return;
        }

        _download = null;
        _receiveTimer.Stop();
        var seconds = (Environment.TickCount64 - download.StartTicks) / 1000.0;
        var rate = TransferProgressText.MegabytesPerSecond(download.Offer.Bytes, seconds);
        var files = download.Offer.Files;
        var folder = _receiveFolder;
        ClientLog.Info(string.Create(CultureInfo.InvariantCulture, $"Receive {download.SendId}: Ok through the direct connection to {download.Endpoint}, {download.Tls}, {files} file(s), {download.Offer.Bytes} bytes in {seconds:0.0} s, {rate:0.0} MB/s from the answer; in {folder}."));
        var where = paths.Count == 1
            ? LocalText.Of(nameof(Strings.ReceiveWhereOne), Path.GetFileName(paths[0]), folder)
            : LocalText.Of(nameof(Strings.ReceiveWhereMany), paths.Count, folder);
        _receiveWindow?.ShowReceived(files, TimeSpan.FromSeconds(seconds), rate, where, () => OpenReceived(paths));
        ShowDropStatus(LocalText.Of(nameof(Strings.ReceiveDoneStatus), files, folder));
        RaiseStateChanged();
    }

    /// <summary>"Receiving 3 file(s) from the session: 42 % (85.0 MB/s, 5 s left)", in the window and the status bar.</summary>
    private void ShowReceiveProgress()
    {
        if (_download is not { } download)
        {
            _receiveTimer.Stop();
            return;
        }

        var received = download.ReceivedBytes;
        var progress = download.Progress;
        progress.Report(Environment.TickCount64, received);
        var files = download.Files;
        if (files.Count == 0 || (received == 0 && download.Offer.Bytes > 0))
        {
            return;
        }

        var sizes = files.Select(file => file.Size).ToList();
        var index = TransferProgress.CurrentFile(sizes, progress.DoneBytes);
        if (index >= 0 && _receiveWindow is { IsDisposed: false } window)
        {
            window.ShowProgress(files[index].RelativePath, index + 1, files.Count, progress);
            window.ShowDirect(true);
        }

        var text = progress.BytesPerSecond is { } rate && progress.Remaining is { } remaining
            ? LocalText.Of(nameof(Strings.ReceiveProgressRate), download.Offer.Files, progress.Percent, rate / TransferProgressText.BytesPerMegabyte, TimeLeftText(remaining))
            : LocalText.Of(nameof(Strings.ReceiveProgress), download.Offer.Files, progress.Percent);
        if (_dropTextBeforeDrag is not null)
        {
            _dropTextBeforeDrag = text;
            return;
        }

        ShowDropStatus(text);
    }

    private void OpenReceiveWindow(SendOfferMessage offer)
    {
        if (_receiveWindow is { IsDisposed: false } previous)
        {
            previous.Close();
        }

        var window = DropProgressForm.ForReceive(offer.Id, _receiveFolder);
        window.CancelRequested += (_, _) =>
        {
            if (_download?.SendId == window.DragId)
            {
                CancelReceive();
            }
        };
        window.FormClosed += (_, _) =>
        {
            if (_receiveWindow == window)
            {
                _receiveWindow = null;
            }
        };
        _receiveWindow = window;
        window.ShowPreparing(offer.FirstItem, offer.Items.Count);
        window.ShowDirect(true);
        // Over the session when the main window is on a monitor; otherwise, as for a RemoteApp
        // program or a window kept out of sight, on the monitor of the mouse, on top.
        if (FindForm() is { Visible: true } form && !IsRemoteApp && Screen.AllScreens.Any(screen => screen.Bounds.IntersectsWith(form.Bounds)))
        {
            window.ShowOver(form, SessionScreenArea());
        }
        else
        {
            window.ShowOver(null, Screen.FromPoint(Cursor.Position).WorkingArea, topMost: true);
        }
    }

    /// <summary>
    /// Creates the folder for received files when it is missing. Returns null when it can
    /// hold files, with its free space when known; otherwise why not.
    /// </summary>
    private string? PrepareReceiveFolder(out long? free)
    {
        free = null;
        try
        {
            Directory.CreateDirectory(_receiveFolder);
            var root = Path.GetPathRoot(Path.GetFullPath(_receiveFolder));
            if (!string.IsNullOrEmpty(root) && !root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                free = new DriveInfo(root).AvailableFreeSpace;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ClientLog.Warn($"The folder for received files {_receiveFolder} cannot be used. {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>Opens Explorer on the folder, with the first received item selected.</summary>
    private static void OpenReceived(IReadOnlyList<string> paths)
    {
        try
        {
            if (paths.Count > 0)
            {
                // A path cannot hold a quote, so this form of Explorer takes any path.
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + paths[0] + "\"") { UseShellExecute = false })?.Dispose();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ClientLog.Error("Cannot open the folder of the received files", ex);
        }
    }
}
