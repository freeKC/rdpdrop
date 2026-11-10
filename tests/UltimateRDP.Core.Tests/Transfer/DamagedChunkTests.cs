using System.Globalization;
using System.Text.RegularExpressions;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Tests.Transfer;

/// <summary>How the channel damages a chunk line in a test.</summary>
public enum LineDamage
{
    /// <summary>A unit in the middle of the data becomes 0x0041, outside the range of u15.</summary>
    UnitReplaced,

    /// <summary>A character in the middle of the data becomes a star, which base64 does not use.</summary>
    BadCharacter,

    /// <summary>A character in the middle of the data is lost.</summary>
    UnitRemoved,

    /// <summary>A valid character is added in the middle of the data.</summary>
    UnitAdded,

    /// <summary>The length field of a u15 line says 100 bytes more.</summary>
    LengthWrong,

    /// <summary>The offset field says one byte more.</summary>
    OffsetWrong,

    /// <summary>800 characters of the data are lost, a block of 1600 bytes of the channel in the UTF-16 model.</summary>
    BlockMissing,
}

/// <summary>
/// Whole transfers whose channel damages a chunk line, as the agent saw it in production
/// with 0.5.1: the ASCII fields of the line read well, its data did not. Since 0.5.2 the
/// agent asks for the line again, as for a lost one, through the whole codec path of
/// <see cref="SimulatedTransfer"/>.
/// </summary>
public class DamagedChunkTests
{
    /// <summary>A file of 5 MB in chunks of 32 KiB: lines 0 and 1, chunks 2 to 161, fileEnd 162.</summary>
    private const int FiveMegabytes = 5 * 1024 * 1024;

    /// <summary>A chunk line in the middle of the file.</summary>
    private const long MiddleSeq = 82;

    private static readonly TransferItem[] OneFile = [new TransferItem("big.bin", false)];

    /// <summary>The damages of each encoding, in both byte models for base64 and in the UTF-16 model for u15, the only one where u15 travels.</summary>
    public static TheoryData<ChannelByteModel, string, LineDamage> Damages
    {
        get
        {
            var data = new TheoryData<ChannelByteModel, string, LineDamage>();
            foreach (var model in new[] { ChannelByteModel.OneBytePerChar, ChannelByteModel.Utf16 })
            {
                foreach (var damage in new[] { LineDamage.BadCharacter, LineDamage.UnitRemoved, LineDamage.UnitAdded, LineDamage.OffsetWrong })
                {
                    data.Add(model, ChunkEncodings.Base64, damage);
                }
            }

            foreach (var damage in new[] { LineDamage.UnitReplaced, LineDamage.UnitRemoved, LineDamage.UnitAdded, LineDamage.LengthWrong, LineDamage.OffsetWrong })
            {
                data.Add(ChannelByteModel.Utf16, ChunkEncodings.U15, damage);
            }

            return data;
        }
    }

    /// <summary>base64 in both byte models, and u15 in the UTF-16 model.</summary>
    public static TheoryData<ChannelByteModel, string> Paths => new()
    {
        { ChannelByteModel.OneBytePerChar, ChunkEncodings.Base64 },
        { ChannelByteModel.Utf16, ChunkEncodings.Base64 },
        { ChannelByteModel.Utf16, ChunkEncodings.U15 },
    };

    private static SimulatedTransfer FiveMegabyteTransfer(ChannelByteModel model, string encoding, MemoryTransferStorage storage, int chunkSize = TransferSender.DefaultChunkSize) =>
        new(model, OneFile, [SimulatedTransfer.MakeFile("big.bin", FiveMegabytes, 8)], storage, chunkSize, chunkEncoding: encoding, reliable: true) { Batching = new SendQueue() };

    private static void AssertIntact(SimulatedTransfer transfer, MemoryTransferStorage storage)
    {
        Assert.Equal(DropStatus.Ok, transfer.Result?.Status);
        Assert.Equal(TransferState.Succeeded, transfer.Receiver.State);
        var (file, data) = Assert.Single(transfer.Files);
        Assert.Equal(data, storage.Content(file.RelativePath));
        Assert.Equal(0, transfer.Outbox!.Count);
        Assert.Equal(InboxFailure.None, transfer.Inbox!.Failure);
        Assert.False(transfer.Inbox.HasGap);
    }

    /// <summary>
    /// Damages the chunk line <paramref name="seq"/> in a call, when the call holds it.
    /// <paramref name="damagedLength"/> receives the length of the damaged line.
    /// </summary>
    internal static string DamageLine(string call, long seq, LineDamage damage, string encoding, Action<int>? damagedLength = null)
    {
        var at = call.IndexOf(string.Create(CultureInfo.InvariantCulture, $"\"seq\":{seq},\"data\":\""), StringComparison.Ordinal);
        if (at < 0)
        {
            return call;
        }

        var start = call.LastIndexOf("{\"type\":\"fileChunk\"", at, StringComparison.Ordinal);
        var end = call.IndexOf('\n', at);
        var line = Damage(call[start..end], damage, encoding);
        damagedLength?.Invoke(line.Length);
        return string.Concat(call.AsSpan(0, start), line, call.AsSpan(end));
    }

    /// <summary>One damage on one chunk line, without its line break.</summary>
    internal static string Damage(string line, LineDamage damage, string encoding)
    {
        var dataStart = line.IndexOf(",\"data\":\"", StringComparison.Ordinal) + ",\"data\":\"".Length;
        var middle = dataStart + ((line.Length - 2 - dataStart) / 2);
        return damage switch
        {
            LineDamage.UnitReplaced => string.Concat(line.AsSpan(0, middle), "A", line.AsSpan(middle + 1)),
            LineDamage.BadCharacter => string.Concat(line.AsSpan(0, middle), "*", line.AsSpan(middle + 1)),
            LineDamage.UnitRemoved => line.Remove(middle, 1),
            LineDamage.UnitAdded => line.Insert(middle, encoding == ChunkEncodings.U15 ? ((char)0x1234).ToString() : "A"),
            LineDamage.LengthWrong => Regex.Replace(line, "\"length\":(\\d+)", match => string.Create(CultureInfo.InvariantCulture, $"\"length\":{long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) + 100}")),
            LineDamage.OffsetWrong => Regex.Replace(line, "\"offset\":(\\d+)", match => string.Create(CultureInfo.InvariantCulture, $"\"offset\":{long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) + 1}")),
            LineDamage.BlockMissing => line.Remove(middle, 800),
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };
    }

    /// <summary>
    /// One chunk in the middle of 5 MB arrives damaged once: the agent asks for that line
    /// alone, once, says why, and the file arrives intact.
    /// </summary>
    [Theory]
    [MemberData(nameof(Damages))]
    public void A_damaged_chunk_in_the_middle_is_sent_again_once(ChannelByteModel model, string encoding, LineDamage damage)
    {
        var storage = new MemoryTransferStorage();
        var transfer = FiveMegabyteTransfer(model, encoding, storage);
        var damagedLength = 0;
        transfer.Channel.Tamper = (_, call) => transfer.SendingAgain ? call : DamageLine(call, MiddleSeq, damage, encoding, length => damagedLength = length);

        transfer.Run();

        AssertIntact(transfer, storage);
        Assert.Equal([new TransferResendMessage("t1", MiddleSeq, MiddleSeq, Damaged: true)], transfer.Requests);
        Assert.Equal(1, transfer.Outbox!.ResentLines);
        Assert.Equal(1, transfer.Outbox.DamagedRequests);
        Assert.Equal(1, transfer.Inbox!.DamagedLines);
        Assert.Equal(1, transfer.Inbox.DamagedRequests);

        var found = Assert.Single(transfer.Damages);
        var units = encoding == ChunkEncodings.U15 ? U15Codec.EncodedLength(TransferSender.DefaultChunkSize) : (TransferSender.DefaultChunkSize + 2) / 3 * 4;
        Assert.Equal(MiddleSeq, found.Seq);
        Assert.Equal(encoding, found.Encoding);
        Assert.Equal(damagedLength, found.LineLength);
        switch (damage)
        {
            case LineDamage.UnitReplaced or LineDamage.BadCharacter:
                Assert.Equal(units / 2, found.BadUnitIndex);
                Assert.Equal(damage == LineDamage.UnitReplaced ? 'A' : '*', found.BadUnit);
                Assert.Equal(units, found.Units);
                Assert.Equal(found.ExpectedLineLength, found.LineLength);
                break;
            case LineDamage.UnitRemoved or LineDamage.UnitAdded:
                var change = damage == LineDamage.UnitAdded ? 1 : -1;
                Assert.Null(found.BadUnitIndex);
                Assert.Equal(units, found.ExpectedUnits);
                Assert.Equal(units + change, found.Units);
                Assert.Equal(found.ExpectedLineLength + change, found.LineLength);
                break;
            case LineDamage.LengthWrong:
                Assert.Equal(TransferSender.DefaultChunkSize + 100, found.ExpectedBytes);
                Assert.Equal(U15Codec.EncodedLength(TransferSender.DefaultChunkSize + 100), found.ExpectedUnits);
                Assert.Equal(units, found.Units);
                Assert.NotEqual(found.ExpectedLineLength, found.LineLength);
                break;
            case LineDamage.OffsetWrong:
                Assert.Equal(TransferFailure.DataLost, found.Failure);
                Assert.Null(found.BadUnitIndex);
                Assert.Equal(units, found.Units);
                Assert.Equal(found.ExpectedLineLength, found.LineLength);
                break;
        }
    }

    /// <summary>
    /// The incident of 0.5.1: the first call held transferBegin, fileBegin and the first
    /// chunk of 128 KiB, whose data lost a block on the way. The agent handles the first two
    /// lines, asks for line 2, and the transfer completes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Paths))]
    public void A_damaged_first_chunk_in_the_call_of_transferBegin_is_sent_again(ChannelByteModel model, string encoding)
    {
        var storage = new MemoryTransferStorage();
        var transfer = FiveMegabyteTransfer(model, encoding, storage, chunkSize: 128 * 1024);
        transfer.Channel.Tamper = (position, call) =>
        {
            if (position != 0 || transfer.SendingAgain)
            {
                return call;
            }

            Assert.Contains("\"type\":\"transferBegin\"", call, StringComparison.Ordinal);
            Assert.Contains("\"type\":\"fileBegin\"", call, StringComparison.Ordinal);
            return DamageLine(call, 2, LineDamage.BlockMissing, encoding);
        };

        transfer.Run();

        AssertIntact(transfer, storage);
        Assert.Equal([new TransferResendMessage("t1", 2, 2, Damaged: true)], transfer.Requests);
        // In base64 what is left is valid base64 too: the length field tells that it is short.
        var found = Assert.Single(transfer.Damages);
        var units = encoding == ChunkEncodings.U15 ? U15Codec.EncodedLength(128 * 1024) : (128 * 1024 + 2) / 3 * 4;
        Assert.Equal(0, found.Offset);
        Assert.Null(found.BadUnitIndex);
        Assert.Equal(units, found.ExpectedUnits);
        Assert.Equal(units - 800, found.Units);
        Assert.Equal(found.ExpectedLineLength - 800, found.LineLength);
    }

    /// <summary>The line sent again arrives damaged too: a second request after the silence, then success.</summary>
    [Theory]
    [MemberData(nameof(Paths))]
    public void A_damaged_resend_is_asked_for_again(ChannelByteModel model, string encoding)
    {
        var storage = new MemoryTransferStorage();
        var transfer = FiveMegabyteTransfer(model, encoding, storage);
        var damage = encoding == ChunkEncodings.U15 ? LineDamage.UnitReplaced : LineDamage.BadCharacter;
        var resends = 0;
        transfer.Channel.Tamper = (_, call) => !transfer.SendingAgain || resends++ == 0 ? DamageLine(call, MiddleSeq, damage, encoding) : call;

        transfer.Run();

        AssertIntact(transfer, storage);
        Assert.Equal([new TransferResendMessage("t1", MiddleSeq, MiddleSeq, Damaged: true), new TransferResendMessage("t1", MiddleSeq, MiddleSeq, Damaged: true)], transfer.Requests);
        Assert.Equal(2, transfer.Damages.Count);
        Assert.Equal(2, transfer.Outbox!.ResentLines);
        Assert.InRange(transfer.Clock, ReliableInbox.RequestInterval.TotalMilliseconds, 2000);
    }

    /// <summary>Every copy of the line arrives damaged: ten requests, then the transfer fails and nothing written stays.</summary>
    [Theory]
    [MemberData(nameof(Paths))]
    public void A_line_always_damaged_fails_after_ten_requests(ChannelByteModel model, string encoding)
    {
        var storage = new MemoryTransferStorage();
        var transfer = FiveMegabyteTransfer(model, encoding, storage);
        transfer.Channel.Tamper = (_, call) => DamageLine(call, MiddleSeq, LineDamage.UnitRemoved, encoding);

        transfer.Run();

        Assert.Equal(DropStatus.Error, transfer.Result?.Status);
        Assert.Equal(InboxFailure.Unrecovered, transfer.Inbox!.Failure);
        Assert.Equal(ReliableInbox.MaxRequests, transfer.Requests.Count);
        Assert.All(transfer.Requests, request => Assert.Equal(new TransferResendMessage("t1", MiddleSeq, MiddleSeq, Damaged: true), request));
        Assert.Equal(ReliableInbox.MaxRequests + 1, transfer.Damages.Count);
        Assert.Equal("lines 82 to 82 of the transfer never arrived intact after 10 requests to the client; line 82 arrived damaged.", transfer.Result?.Message);
        Assert.Empty(storage.Files);
    }

    /// <summary>
    /// The number itself is damaged: the line reads without one, the agent ignores it, and
    /// the next line shows the gap, which the agent asks for as for a lost line.
    /// </summary>
    [Theory]
    [MemberData(nameof(Paths))]
    public void A_chunk_whose_number_is_damaged_is_asked_for_as_a_gap(ChannelByteModel model, string encoding)
    {
        var storage = new MemoryTransferStorage();
        var transfer = FiveMegabyteTransfer(model, encoding, storage);
        var marker = string.Create(CultureInfo.InvariantCulture, $"\"seq\":{MiddleSeq},");
        transfer.Channel.Tamper = (_, call) => transfer.SendingAgain ? call : call.Replace(marker, string.Create(CultureInfo.InvariantCulture, $"\"seX\":{MiddleSeq},"), StringComparison.Ordinal);

        transfer.Run();

        AssertIntact(transfer, storage);
        Assert.Equal(1, transfer.UnnumberedIgnored);
        Assert.Empty(transfer.Damages);
        Assert.Equal([new TransferResendMessage("t1", MiddleSeq, MiddleSeq)], transfer.Requests);
    }

    /// <summary>A client before 0.5.1 does not number its lines: a damaged chunk ends the transfer, as before.</summary>
    [Fact]
    public void A_damaged_chunk_without_a_number_still_fails_the_transfer()
    {
        var storage = new MemoryTransferStorage();
        var transfer = new SimulatedTransfer(ChannelByteModel.Utf16, OneFile, [SimulatedTransfer.MakeFile("big.bin", FiveMegabytes, 8)], storage, chunkEncoding: ChunkEncodings.U15) { Batching = new SendQueue() };
        var damaged = false;
        transfer.Channel.Tamper = (position, call) =>
        {
            if (position != 3)
            {
                return call;
            }

            damaged = true;
            var start = call.IndexOf("{\"type\":\"fileChunk\"", StringComparison.Ordinal);
            var end = call.IndexOf('\n', start);
            return string.Concat(call.AsSpan(0, start), Damage(call[start..end], LineDamage.UnitReplaced, ChunkEncodings.U15), call.AsSpan(end));
        };

        transfer.Run();

        Assert.True(damaged);
        Assert.Equal(DropStatus.Error, transfer.Result?.Status);
        Assert.Equal(TransferFailure.BadData, transfer.Receiver.Failure);
        Assert.Empty(transfer.Requests);
    }
}
