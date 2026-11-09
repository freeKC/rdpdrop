using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Agent;

/// <summary>When the agent asks for the dropped files to be transferred through the channel.</summary>
public enum FastTransferMode
{
    /// <summary>When the dropped items hold at least the threshold in bytes. The default.</summary>
    Auto,

    /// <summary>For every drop.</summary>
    Always,

    /// <summary>Never: the session reads the files through drive redirection, as before.</summary>
    Never,
}

/// <summary>
/// Reads and writes the <c>fastTransfer</c> setting of agent.json, and decides whether a
/// drag is transferred through the channel before the drop.
/// </summary>
public static class FastTransferPolicy
{
    /// <summary>Default of <c>fastTransferMinBytes</c>: 10 MiB.</summary>
    public const long DefaultMinBytes = 10L * 1024 * 1024;

    /// <summary>Free space needed on top of the size of the files, in percent of that size.</summary>
    public const int FreeSpaceMarginPercent = 10;

    /// <summary>
    /// Reads a setting value: "auto", "always" or "never", in any case and with spaces
    /// around. A missing value gives <see cref="FastTransferMode.Auto"/> and counts as
    /// recognized; any other value gives <see cref="FastTransferMode.Auto"/> and
    /// <paramref name="recognized"/> false, so that the caller can log it.
    /// </summary>
    public static FastTransferMode Parse(string? value, out bool recognized)
    {
        recognized = true;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "auto":
                return FastTransferMode.Auto;

            case "always":
                return FastTransferMode.Always;

            case "never":
                return FastTransferMode.Never;

            default:
                recognized = false;
                return FastTransferMode.Auto;
        }
    }

    /// <summary>The value written to agent.json: "auto", "always" or "never".</summary>
    public static string ToSetting(FastTransferMode mode) => mode switch
    {
        FastTransferMode.Always => "always",
        FastTransferMode.Never => "never",
        _ => "auto",
    };

    /// <summary>
    /// Total of the sizes the client gave for <paramref name="items"/>; an item without a
    /// size counts as zero. Never overflows.
    /// </summary>
    public static long TotalSize(IEnumerable<DropItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        long total = 0;
        foreach (var item in items)
        {
            if (item.Size is > 0 and var size)
            {
                total = size > long.MaxValue - total ? long.MaxValue : total + size;
            }
        }

        return total;
    }

    /// <summary>
    /// True when the items must be transferred through the channel: never with
    /// <see cref="FastTransferMode.Never"/>, always with <see cref="FastTransferMode.Always"/>,
    /// and with <see cref="FastTransferMode.Auto"/> when their total size is at least
    /// <paramref name="minBytes"/>.
    /// </summary>
    public static bool ShouldTransfer(FastTransferMode mode, long minBytes, IEnumerable<DropItem> items) => mode switch
    {
        FastTransferMode.Never => false,
        FastTransferMode.Always => true,
        _ => TotalSize(items) >= Math.Max(0, minBytes),
    };

    /// <summary>
    /// Free space the staging volume needs to receive <paramref name="bytes"/>: the size
    /// plus <see cref="FreeSpaceMarginPercent"/> percent, rounded up. Never overflows.
    /// </summary>
    public static long RequiredFreeSpace(long bytes)
    {
        if (bytes <= 0)
        {
            return 0;
        }

        var margin = bytes / 100 * FreeSpaceMarginPercent + ((bytes % 100 * FreeSpaceMarginPercent) + 99) / 100;
        return margin > long.MaxValue - bytes ? long.MaxValue : bytes + margin;
    }
}
