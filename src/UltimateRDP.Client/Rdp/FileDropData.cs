namespace UltimateRDP.Client.Rdp;

/// <summary>
/// Reads the file list of a drag. Only <see cref="DataFormats.FileDrop"/> (CF_HDROP) is
/// accepted; anything else yields null.
/// </summary>
internal static class FileDropData
{
    public static IReadOnlyList<string>? GetFiles(IDataObject? data)
    {
        if (data is null)
        {
            return null;
        }

        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop, autoConvert: false))
            {
                return null;
            }

            return data.GetData(DataFormats.FileDrop, autoConvert: false) is string[] { Length: > 0 } files
                ? files
                : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Some sources fail to render the format on request.
            return null;
        }
    }
}
