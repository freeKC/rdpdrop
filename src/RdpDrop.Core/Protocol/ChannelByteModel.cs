namespace RdpDrop.Core.Protocol;

/// <summary>
/// How the bytes of the virtual channel relate to the characters of the string that the
/// RDP ActiveX control exposes on the client.
/// </summary>
public enum ChannelByteModel
{
    /// <summary>Not detected yet.</summary>
    Unknown,

    /// <summary>Each character carries one byte, in its low eight bits.</summary>
    OneBytePerChar,

    /// <summary>Each character carries two bytes, little endian (raw UTF-16LE).</summary>
    Utf16,
}
