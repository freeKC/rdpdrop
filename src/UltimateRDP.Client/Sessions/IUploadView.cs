using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// What the status bar and the progress window show of a transfer, through the channel
/// (<see cref="TransferUpload"/>) or through the direct connection (<see cref="DirectUpload"/>).
/// UI thread only.
/// </summary>
internal interface IUploadView
{
    /// <summary>The drag whose drop was deferred.</summary>
    string DragId { get; }

    /// <summary>The id the agent gave in its dragAck.</summary>
    string TransferId { get; }

    /// <summary>Number of files, folders being counted file by file.</summary>
    int FileCount { get; }

    /// <summary>Total bytes.</summary>
    long TotalBytes { get; }

    /// <summary>The progress, null before the files are listed.</summary>
    TransferProgress? Progress { get; }

    /// <summary>The files in the order they are sent.</summary>
    IReadOnlyList<TransferFile> Files { get; }

    /// <summary>The sizes of <see cref="Files"/>, in the same order.</summary>
    IReadOnlyList<long> FileSizes { get; }

    /// <summary>When the upload started, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
    long StartTicks { get; }

    /// <summary>True when the files go through the direct connection.</summary>
    bool IsDirect { get; }
}
