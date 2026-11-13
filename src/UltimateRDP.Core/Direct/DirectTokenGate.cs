using System.Security.Cryptography;

namespace UltimateRDP.Core.Direct;

/// <summary>
/// The check of the token frames of one offer, on the side that accepts the connections:
/// the agent, in both directions. A frame passes when it is of version 2, of the role of
/// the offer, its index is below the connections of the offer, its proof is the one of the
/// token for that index, compared in constant time before anything that depends on the
/// index, connection 0 came first, and the index was not used yet. The token is cleared once
/// every index was used, or by <see cref="Clear"/>.
/// </summary>
/// <remarks>Thread safe.</remarks>
public sealed class DirectTokenGate
{
    private readonly Lock _gate = new();
    private readonly byte[] _token;
    private readonly bool[] _used;
    private bool _cleared;

    /// <param name="token">The token of the offer, <see cref="DirectFrames.TokenLength"/> bytes.</param>
    /// <param name="maxConnections">The connections the offer allows, 1 to <see cref="DirectFrames.MaxConnections"/>.</param>
    /// <param name="role">The direction of the offer: the role every token frame must say.</param>
    public DirectTokenGate(ReadOnlySpan<byte> token, int maxConnections, DirectRole role)
    {
        if (token.Length != DirectFrames.TokenLength)
        {
            throw new ArgumentException("A token has 32 bytes.", nameof(token));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxConnections, DirectFrames.MaxConnections);
        _token = token.ToArray();
        _used = new bool[maxConnections];
        Role = role;
    }

    /// <summary>The direction of the offer.</summary>
    public DirectRole Role { get; }

    /// <summary>The connections the offer allows.</summary>
    public int MaxConnections => _used.Length;

    /// <summary>True once connection 0 passed.</summary>
    public bool HasControl
    {
        get
        {
            lock (_gate)
            {
                return _used[0];
            }
        }
    }

    /// <summary>
    /// Checks the payload of a token frame and marks its index as used. Returns null when it
    /// passes, with the index; otherwise why not, in English, for the log.
    /// </summary>
    public string? Check(ReadOnlySpan<byte> payload, out int index)
    {
        index = -1;
        if (!DirectFrames.TryParseToken(payload, out var version, out var parsedIndex, out var role, out var proof))
        {
            return payload.Length > 0 && version != DirectFrames.ProtocolVersion
                ? $"The client speaks version {version} of the direct connection; this agent speaks {DirectFrames.ProtocolVersion}."
                : "The token frame is malformed.";
        }

        lock (_gate)
        {
            if (_cleared)
            {
                return "Every connection of the offer was used.";
            }

            if (parsedIndex >= _used.Length)
            {
                return $"Connection {parsedIndex} is beyond the {_used.Length} connection(s) of the offer.";
            }

            // Compared in constant time, before anything that depends on the index or the
            // role, so that the answer tells nothing to a client without the token. A proof of
            // the other direction is not this one.
            if (role != Role || !CryptographicOperations.FixedTimeEquals(proof, DirectFrames.ConnectionProof(_token, parsedIndex, Role)))
            {
                return "The token is not the one of the offer.";
            }

            if (parsedIndex != 0 && !_used[0])
            {
                return $"Connection {parsedIndex} came before connection 0.";
            }

            if (_used[parsedIndex])
            {
                return $"Connection {parsedIndex} was already used.";
            }

            _used[parsedIndex] = true;
            if (_used.All(used => used))
            {
                ClearUnderLock();
            }
        }

        index = parsedIndex;
        return null;
    }

    /// <summary>The offer is over: the token cannot open anything more.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            ClearUnderLock();
        }
    }

    private void ClearUnderLock()
    {
        if (!_cleared)
        {
            CryptographicOperations.ZeroMemory(_token);
            _cleared = true;
        }
    }
}
