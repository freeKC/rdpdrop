using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// Development aid behind a hidden switch: shows the drop window with simulated drops,
/// without any connection, to check its layout and its texts. A first window shows a
/// drop without transfer, a second one a transfer that succeeds, both closing by
/// themselves; a third one a transfer that fails, which stays open.
/// </summary>
/// <remarks>Nothing is sent and no setting is written.</remarks>
internal static class DropProgressDemo
{
    /// <summary>The command line switch. Not in any help text.</summary>
    public const string Switch = "--show-transfer-window-demo";

    private const int TickMilliseconds = 250;
    private const int DropMilliseconds = 2000;

    // In simulated time each tick is four seconds at 2.6 MB/s, so 496 MB take 12 seconds.
    private const long SimulatedTickMilliseconds = 4000;
    private const long BytesPerTick = 10_400_000;

    private static readonly TransferFile[] Files =
    [
        new(@"Photos\2026\a.jpg", 120_000_000),
        new(@"Photos\2026\b.mov", 376_000_000),
    ];

    /// <summary>Starts the demonstration over <paramref name="owner"/>.</summary>
    public static void Start(Form owner)
    {
        ClientLog.Info("Drop window demo: a drop without transfer.");
        RunDrop(owner, then: () =>
        {
            ClientLog.Info("Drop window demo: a transfer that succeeds.");
            RunTransfer(owner, succeed: true, then: () =>
            {
                ClientLog.Info("Drop window demo: a transfer that fails.");
                RunTransfer(owner, succeed: false, then: () => ClientLog.Info("Drop window demo: over."));
            });
        });
    }

    private static DropProgressForm NewWindow(Form owner, Action then, System.Windows.Forms.Timer? timer)
    {
        var window = new DropProgressForm(Guid.NewGuid().ToString("N"), "demo");
        window.FormClosed += (_, _) =>
        {
            timer?.Dispose();
            owner.BeginInvoke(then);
        };
        return window;
    }

    private static void RunDrop(Form owner, Action then)
    {
        var window = NewWindow(owner, then, timer: null);
        window.ShowDropping("report.docx", 3);
        window.ShowOver(owner, owner.RectangleToScreen(owner.ClientRectangle));
        After(DropMilliseconds, () => window.ShowDropResult(DroppedOnExplorer(), failed: false));
    }

    private static void RunTransfer(Form owner, bool succeed, Action then)
    {
        var timer = new System.Windows.Forms.Timer { Interval = TickMilliseconds };
        var window = NewWindow(owner, then, timer);
        var total = Files.Sum(file => file.Size);
        var sizes = Files.Select(file => file.Size).ToArray();
        var progress = new TransferProgress(total, 0);
        long simulated = 0;
        long done = 0;
        var tick = 0;
        window.CancelRequested += (_, _) => window.ShowFailure(LocalText.Of(nameof(Strings.TransferCancelledByUser)));
        timer.Tick += (_, _) =>
        {
            tick++;
            if (tick < 8 || !window.IsRunning)
            {
                // Preparing for two seconds; after the end, nothing more to simulate.
                return;
            }

            if (tick == 20)
            {
                window.ShowBusyNotice();
            }

            if (!succeed && tick == 24)
            {
                timer.Stop();
                window.ShowFailure(LocalText.Of(nameof(Strings.TransferAgentSilent)));
                return;
            }

            simulated += SimulatedTickMilliseconds;
            done = Math.Min(total, done + BytesPerTick);
            progress.Report(simulated, done);
            var index = TransferProgress.CurrentFile(sizes, done);
            window.ShowProgress(Files[index].RelativePath, index + 1, Files.Length, progress);
            if (done == total)
            {
                timer.Stop();
                window.ShowTransferred(Files.Length, TimeSpan.FromSeconds(193), TransferProgressText.MegabytesPerSecond(total, 193));
                After(DropMilliseconds, () => window.ShowDropResult(DroppedOnExplorer(), failed: false));
            }
        };

        window.ShowPreparing("Photos", 2);
        window.ShowOver(owner, owner.RectangleToScreen(owner.ClientRectangle));
        timer.Start();
    }

    private static LocalText DroppedOnExplorer() =>
        LocalText.Of(nameof(Strings.DroppedOn), LocalText.Raw("Explorer"), LocalText.Of(nameof(Strings.EffectCopy)));

    private static void After(int milliseconds, Action action)
    {
        var timer = new System.Windows.Forms.Timer { Interval = milliseconds };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            action();
        };
        timer.Start();
    }
}
