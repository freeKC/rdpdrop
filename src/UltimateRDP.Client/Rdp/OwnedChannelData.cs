using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using UltimateRDP.Client.Logging;
using UltimateRDP.Core.Protocol;

namespace UltimateRDP.Client.Rdp;

/// <summary>
/// Hands strings to SendOnVirtualChannel of the control with a BSTR allocated here and
/// kept alive after the call, until the agent surely received the data
/// (<see cref="SentDataRetention{T}"/>). UI thread only, like the control.
/// </summary>
/// <remarks>
/// <para>
/// In production a short line sent just before a large one was sometimes lost without
/// any error. The likely cause is that the control sends after the call returned, from a
/// BSTR that the default marshalling frees at once; when the heap reuses that memory
/// first, the control reads a wrong length or wrong content. Keeping the BSTR alive
/// removes that cause, whatever the control does.
/// </para>
/// <para>
/// When <see cref="IMsTscAxOwnedData"/> cannot be obtained from the control, or its slot
/// differs from the slot of the interop assembly, the normal interop call is used, and
/// the log says so once.
/// </para>
/// </remarks>
internal sealed class OwnedChannelData : IDisposable
{
    private const int ReleaseIntervalMilliseconds = 1000;

    /// <summary>Slot of the first method of a dual interface: after the three of IUnknown and the four of IDispatch.</summary>
    private const int FirstMethodSlot = 7;

    private const string SendMethodName = nameof(IMsTscAxOwnedData.SendOnVirtualChannel);

    /// <summary>The sends of closed tabs, freed once their time is over.</summary>
    private static readonly List<(IntPtr Bstr, long Due)> Orphans = [];
    private static readonly object OrphanGate = new();
    private static System.Threading.Timer? _orphanTimer;

    private readonly RdpControl _control;
    private readonly SentDataRetention<IntPtr> _retained;
    private readonly System.Windows.Forms.Timer _releaseTimer = new() { Interval = ReleaseIntervalMilliseconds };
    private IMsTscAxOwnedData? _owned;
    private bool _resolved;
    private bool _disposed;

    /// <param name="control">The Remote Desktop control.</param>
    /// <param name="maxRetainedBytes">Most bytes of strings kept, see <see cref="SentDataRetention{T}.LimitForWindow"/>.</param>
    public OwnedChannelData(RdpControl control, long maxRetainedBytes = SentDataRetention<IntPtr>.DefaultMaxRetainedBytes)
    {
        _control = control;
        _retained = new SentDataRetention<IntPtr>(maxRetainedBytes: maxRetainedBytes);
        _releaseTimer.Tick += (_, _) => ReleaseExpired();
    }

    /// <summary>True when the strings go through the hand written interface, null before the first send.</summary>
    public bool? OwnsData => _resolved ? _owned is not null : null;

    /// <summary>Number of sends whose BSTR is kept.</summary>
    public int RetainedCount => _retained.Count;

    /// <summary>Bytes of the BSTR kept.</summary>
    public long RetainedBytes => _retained.RetainedBytes;

    /// <summary>
    /// Sends <paramref name="text"/> on <paramref name="channelName"/>. <paramref name="firstChunk"/>
    /// is the first chunk line of the text sent for the first time, or null.
    /// <paramref name="holdLong"/> keeps a text of chunks sent again as long as a text of new
    /// chunks, see <see cref="SentDataRetention{T}.Add"/>. Returns the time spent inside the
    /// call of the control, in ticks of <see cref="Stopwatch"/>. Throws what the control throws.
    /// </summary>
    public long Send(string channelName, string text, QueuedLine? firstChunk, bool holdLong = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var owned = Resolve();
        if (owned is null)
        {
            var started = Stopwatch.GetTimestamp();
            _control.SendOnVirtualChannel(channelName, text);
            return Stopwatch.GetTimestamp() - started;
        }

        var bstr = Marshal.StringToBSTR(text);

        // Kept even when the call fails: nothing says that the control did not take it.
        var evicted = _retained.Add(bstr, BstrBytes(text.Length), Environment.TickCount64, firstChunk, holdLong);
        if (evicted.Count > 0)
        {
            ClientLog.Info(Invariant($"More than {_retained.MaxRetainedBytes} bytes of sent strings kept; the {evicted.Count} oldest are freed before their time."));
            Free(evicted);
        }

        _releaseTimer.Start();
        var start = Stopwatch.GetTimestamp();
        owned.SendOnVirtualChannel(channelName, bstr);
        return Stopwatch.GetTimestamp() - start;
    }

    /// <summary>Finds out, once, whether the hand written interface can be used, and logs it.</summary>
    public void Prepare()
    {
        if (!_disposed)
        {
            Resolve();
        }
    }

    /// <summary>The agent acknowledged the first <paramref name="acknowledgedBytes"/> of a transfer.</summary>
    public void OnAcknowledged(string transferId, long acknowledgedBytes)
    {
        if (!_disposed)
        {
            Free(_retained.ReleaseAcknowledged(transferId, acknowledgedBytes));
        }
    }

    /// <summary>
    /// The BSTR still kept are freed once their time is over, on a thread pool timer: the
    /// control may still be sending the last ones when the tab closes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _releaseTimer.Dispose();
        var due = Environment.TickCount64 + (long)_retained.ChunkHoldTime.TotalMilliseconds;
        var left = _retained.ReleaseAll();
        if (left.Count == 0)
        {
            return;
        }

        lock (OrphanGate)
        {
            foreach (var bstr in left)
            {
                Orphans.Add((bstr, due));
            }

            _orphanTimer ??= new System.Threading.Timer(_ => FreeOrphans(), null, ReleaseIntervalMilliseconds, ReleaseIntervalMilliseconds);
        }
    }

    private void ReleaseExpired()
    {
        Free(_retained.ReleaseExpired(Environment.TickCount64));
        if (_retained.Count == 0)
        {
            _releaseTimer.Stop();
        }
    }

    private IMsTscAxOwnedData? Resolve()
    {
        if (_resolved)
        {
            return _owned;
        }

        // Before the control is created there is nothing to send to; the normal call reports it.
        var ocx = _control.GetOcx();
        if (ocx is null)
        {
            return null;
        }

        _resolved = true;
        try
        {
            // The table of a dual interface is IUnknown, IDispatch, then the methods in the
            // order of their metadata, which must be the same in both interfaces.
            var expected = MethodOrder(typeof(MSTSCLib.IMsTscAx));
            var actual = MethodOrder(typeof(IMsTscAxOwnedData));
            var mine = FirstMethodSlot + Array.IndexOf(actual, SendMethodName);
            if (!expected.SequenceEqual(actual, StringComparer.Ordinal) || typeof(MSTSCLib.IMsTscAx).GUID != typeof(IMsTscAxOwnedData).GUID)
            {
                ClientLog.Warn($"The hand written IMsTscAx interface differs from the interop one ({string.Join(", ", expected)}); using the interop call, which frees each string when it returns.");
                return null;
            }

            if (ocx is IMsTscAxOwnedData owned)
            {
                _owned = owned;
                ClientLog.Info(Invariant($"SendOnVirtualChannel goes through the hand written interface (slot {mine}); every string sent stays allocated until the agent received it."));
                return owned;
            }

            ClientLog.Warn("The control does not give the hand written IMsTscAx interface; using the interop call, which frees each string when it returns.");
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException or ArgumentException or NotSupportedException)
        {
            ClientLog.Warn($"The hand written IMsTscAx interface cannot be used; using the interop call, which frees each string when it returns. {ex.Message}");
        }

        return null;
    }

    /// <summary>The methods an interface declares, in the order of their metadata.</summary>
    private static string[] MethodOrder(Type type) =>
        [.. type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(method => method.MetadataToken)
            .Select(method => method.Name)];

    /// <summary>The size of a BSTR of <paramref name="length"/> characters: the length prefix, the characters and the terminator.</summary>
    private static long BstrBytes(int length) => 4 + (2L * length) + 2;

    private static void Free(IReadOnlyList<IntPtr> bstrs)
    {
        foreach (var bstr in bstrs)
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    private static void FreeOrphans()
    {
        lock (OrphanGate)
        {
            var now = Environment.TickCount64;
            for (var i = Orphans.Count - 1; i >= 0; i--)
            {
                if (now >= Orphans[i].Due)
                {
                    Marshal.FreeBSTR(Orphans[i].Bstr);
                    Orphans.RemoveAt(i);
                }
            }

            if (Orphans.Count == 0)
            {
                _orphanTimer?.Dispose();
                _orphanTimer = null;
            }
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
