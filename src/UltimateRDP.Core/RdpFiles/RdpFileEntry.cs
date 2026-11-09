namespace UltimateRDP.Core.RdpFiles;

/// <summary>
/// One <c>key:type:value</c> line of a <c>.rdp</c> file.
/// </summary>
/// <param name="Key">Setting name, as written in the file.</param>
/// <param name="Type">Type letter in lower case: <c>s</c> (string), <c>i</c> (integer) or <c>b</c> (binary as hex).</param>
/// <param name="Value">Everything after the second colon.</param>
public sealed record RdpFileEntry(string Key, char Type, string Value);
