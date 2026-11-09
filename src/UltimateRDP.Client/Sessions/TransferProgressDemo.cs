using UltimateRDP.Client.Localization;
using UltimateRDP.Client.Logging;
using UltimateRDP.Client.Resources;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// Development aid behind a hidden switch: shows the transfer window with a simulated
/// transfer, without any connection, to check its layout and its texts. A first window
/// goes through a transfer that succeeds and closes by itself; a second one fails and
/// stays open.
/// </summary>
/// <remarks>Nothing is sent and no setting is written.</remarks>
internal static class TransferProgressDemo
{
    /// <summary>The command line switch. Not in any help text.</summary>
    public const string Switch = "--show-transfer-window-demo";

    private const int TickMilliseconds = 250;

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
        ClientLog.Info("Transfer window demo: a transfer that succeeds.");
        Run(owner, succeed: true, then: () =>
        {
            ClientLog.Info("Transfer window demo: a transfer that fails.");
            Run(owner, succeed: false, then: () => ClientLog.Info("Transfer window demo: over."));
        });
    }

    private static void Run(Form owner, bool succeed, Action then)
    {
        var window = new TransferProgressForm(Guid.NewGuid().ToString("N"), "demo");
        var timer = new System.Windows.Forms.Timer { Interval = TickMilliseconds };
        var total = Files.Sum(file => file.Size);
        var sizes = Files.Select(file => file.Size).ToArray();
        var progress = new TransferProgress(total, 0);
        long simulated = 0;
        long done = 0;
        var tick = 0;
        window.CancelRequested += (_, _) => window.ShowFailure(LocalText.Of(nameof(Strings.TransferCancelledByUser)));
        window.FormClosed += (_, _) =>
        {
            timer.Dispose();
            owner.BeginInvoke(then);
        };

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
                window.ShowDropping(Files.Length, TimeSpan.FromSeconds(193), TransferProgressText.MegabytesPerSecond(total, 193));
                var drop = new System.Windows.Forms.Timer { Interval = 2000 };
                drop.Tick += (_, _) =>
                {
                    drop.Dispose();
                    window.ShowDropResult(LocalText.Of(nameof(Strings.DroppedOn), LocalText.Raw("Explorer"), LocalText.Of(nameof(Strings.EffectCopy))));
                };
                drop.Start();
            }
        };

        window.ShowPreparing("Photos", 2);
        window.ShowOver(owner, owner.RectangleToScreen(owner.ClientRectangle));
        timer.Start();
    }
}
