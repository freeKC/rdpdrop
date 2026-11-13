using UltimateRDP.Core.Agent;
using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Core.Transfer;

/// <summary>Why the client does not receive the files that the session offers to send.</summary>
public enum ReceiveRefusal
{
    /// <summary>The client receives them.</summary>
    None,

    /// <summary>The setting <c>receiveFiles</c> is <c>never</c>, or <c>directTransfer</c> is <c>never</c>.</summary>
    Off,

    /// <summary>Another send to this tab runs.</summary>
    Busy,

    /// <summary>The direct connection of the offer is not one this client speaks.</summary>
    NotDirect,

    /// <summary>The folder for received files cannot hold the files.</summary>
    NoSpace,

    /// <summary>The folder for received files cannot be created or written.</summary>
    NoFolder,
}

/// <summary>
/// The rules of the client for the files that the session sends, since 0.9.0: whether it
/// takes an offer, where the files go while they come, and the Mark of the Web they get.
/// </summary>
public static class ReceivePolicy
{
    /// <summary>The zone of the Mark of the Web of every received file: 3, the Internet zone, as for a download of a browser.</summary>
    public const int InternetZone = 3;

    /// <summary>The name of the alternate data stream of the Mark of the Web.</summary>
    public const string ZoneStream = "Zone.Identifier";

    /// <summary>
    /// Checks an offer: the client takes it only when receiving is on, no other send runs in
    /// the tab, its direct connection is of the version of this client, and the folder has
    /// room for the bytes and the margin of <see cref="FastTransferPolicy.RequiredFreeSpace"/>.
    /// <paramref name="freeBytes"/> is null when the free space is not known, which passes.
    /// </summary>
    public static ReceiveRefusal Check(bool enabled, bool busy, SendOfferMessage offer, long? freeBytes)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (!enabled)
        {
            return ReceiveRefusal.Off;
        }

        if (busy)
        {
            return ReceiveRefusal.Busy;
        }

        if (!offer.Direct.IsWellFormed())
        {
            return ReceiveRefusal.NotDirect;
        }

        return freeBytes is { } free && free < FastTransferPolicy.RequiredFreeSpace(offer.Bytes) ? ReceiveRefusal.NoSpace : ReceiveRefusal.None;
    }

    /// <summary>
    /// The content of the Zone.Identifier stream of a received file: the Internet zone, so
    /// that Windows, SmartScreen and Office treat the file as a download.
    /// </summary>
    public static string ZoneIdentifier() => "[ZoneTransfer]\r\nZoneId=" + InternetZone.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n";

    /// <summary>
    /// Moves the items of a transfer that succeeded from <paramref name="partFolder"/> into
    /// <paramref name="receiveFolder"/>, each under a name that no file or folder there has
    /// yet (<see cref="ReceiveNames.Unique"/>). Returns the full path each item got, in the
    /// order of <paramref name="items"/>; an item that is not in the part folder is skipped.
    /// Throws an <see cref="IOException"/> or an <see cref="UnauthorizedAccessException"/> when
    /// a move fails, the items moved before it staying where they went.
    /// </summary>
    public static IReadOnlyList<string> MoveIn(string partFolder, string receiveFolder, IReadOnlyList<TransferItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var moved = new List<string>(items.Count);
        foreach (var item in items)
        {
            var source = Path.Combine(partFolder, item.Name);
            if (item.IsDirectory ? !Directory.Exists(source) : !File.Exists(source))
            {
                continue;
            }

            var name = ReceiveNames.Unique(item.Name, item.IsDirectory, candidate => Path.Exists(Path.Combine(receiveFolder, candidate)));
            var target = Path.Combine(receiveFolder, name);
            if (item.IsDirectory)
            {
                Directory.Move(source, target);
            }
            else
            {
                File.Move(source, target, overwrite: false);
            }

            moved.Add(target);
        }

        return moved;
    }
}
