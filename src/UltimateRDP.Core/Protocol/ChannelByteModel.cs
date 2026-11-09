namespace UltimateRDP.Core.Protocol;

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

/// <summary>The names of the byte models in the messages, such as <c>clientToAgentModel</c> of the hello of the agent.</summary>
public static class ChannelByteModelNames
{
    /// <summary>Name of <see cref="ChannelByteModel.Utf16"/>.</summary>
    public const string Utf16 = "utf16";

    /// <summary>Name of <see cref="ChannelByteModel.OneBytePerChar"/>.</summary>
    public const string OneBytePerChar = "oneBytePerChar";

    /// <summary>The name of a known model, or null for <see cref="ChannelByteModel.Unknown"/>.</summary>
    public static string? ToName(ChannelByteModel model) => model switch
    {
        ChannelByteModel.Utf16 => Utf16,
        ChannelByteModel.OneBytePerChar => OneBytePerChar,
        _ => null,
    };

    /// <summary>The model of a name; <see cref="ChannelByteModel.Unknown"/> for null or any other text.</summary>
    public static ChannelByteModel FromName(string? name) => name switch
    {
        Utf16 => ChannelByteModel.Utf16,
        OneBytePerChar => ChannelByteModel.OneBytePerChar,
        _ => ChannelByteModel.Unknown,
    };
}
