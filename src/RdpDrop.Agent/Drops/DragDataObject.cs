namespace RdpDrop.Agent.Drops;

/// <summary>
/// Builds the data object offered by the drag inside the session.
/// </summary>
internal static class DragDataObject
{
    /// <summary>Shell format that tells a target which effect the source prefers.</summary>
    public const string PreferredDropEffectFormat = "Preferred DropEffect";

    /// <summary>
    /// Offers <paramref name="paths"/> as a file drop (CF_HDROP, with FileNameW and
    /// FileName for the first path) and asks for a copy. Targets that read the files at
    /// drop time open them through these paths.
    /// </summary>
    public static DataObject Create(IReadOnlyList<string> paths)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, autoConvert: true, paths.ToArray());

        // A stream is copied as is into the global memory block: four bytes, DROPEFFECT_COPY.
        // Without it Explorer may choose a move on the same volume.
        data.SetData(PreferredDropEffectFormat, new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
        return data;
    }
}
