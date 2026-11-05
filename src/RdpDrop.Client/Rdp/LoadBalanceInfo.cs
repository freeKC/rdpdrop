using System.Runtime.InteropServices;
using System.Text;

namespace RdpDrop.Client.Rdp;

/// <summary>
/// Builds the value of the LoadBalanceInfo property from the <c>loadbalanceinfo</c> of a
/// .rdp file.
/// </summary>
/// <remarks>
/// mstsc sends the value followed by CR LF as the routing token of the connection
/// request. The control reads the BSTR it is given as a byte string, so the ASCII text is
/// packed two bytes per UTF-16 unit. An odd length is padded with a space before CR LF,
/// because a BSTR built from a .NET string always holds an even number of bytes.
/// Against an RD Connection Broker, the plain string made the server refuse the
/// connection at once (disconnect reason 2308, server denied the connection).
/// </remarks>
internal static class LoadBalanceInfo
{
    public static string ToControlValue(string value)
    {
        var text = value.Trim();
        if (text.Length % 2 != 0)
        {
            text += " ";
        }

        var bytes = Encoding.ASCII.GetBytes(text + "\r\n");
        return new string(MemoryMarshal.Cast<byte, char>(bytes));
    }
}
