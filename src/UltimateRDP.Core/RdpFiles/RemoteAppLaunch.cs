namespace UltimateRDP.Core.RdpFiles;

/// <summary>Why a .rdp file that asks for RemoteApp cannot open it.</summary>
public enum RemoteAppRejection
{
    /// <summary>Nothing is wrong: the file opens a desktop, or a RemoteApp program it names.</summary>
    None,

    /// <summary>The file asks for RemoteApp but names neither a program nor a file.</summary>
    NoProgram,
}

/// <summary>
/// The program a .rdp file opens in RemoteApp mode, read from its
/// <c>remoteapplication*</c> keys. The keys <c>alternate shell</c> and
/// <c>remoteapplicationappid</c> are not read: the first is what an older server needs
/// instead, the second needs a newer control.
/// </summary>
/// <param name="Program">
/// <c>remoteapplicationprogram</c>: an alias of a published program such as <c>||MyApp</c>,
/// or the path of a program on the server. Null when the file only names a file.
/// </param>
/// <param name="File"><c>remoteapplicationfile</c>: a file on the server for the program to open, or null.</param>
/// <param name="Name"><c>remoteapplicationname</c>: the name shown to the user, or null.</param>
/// <param name="Args"><c>remoteapplicationcmdline</c>: the arguments, or null.</param>
/// <param name="ExpandArgs"><c>remoteapplicationexpandcmdline</c>: the server expands the variables of the arguments.</param>
/// <param name="ExpandWorkingDir"><c>remoteapplicationexpandworkingdir</c>: the server expands the variables of the working folder.</param>
public sealed record RemoteAppLaunch(
    string? Program,
    string? File,
    string? Name,
    string? Args,
    bool ExpandArgs,
    bool ExpandWorkingDir)
{
    /// <summary>The prefix of an alias of a published program.</summary>
    public const string AliasPrefix = "||";

    /// <summary>True when <see cref="Program"/> is an alias of a published program, such as <c>||MyApp</c>.</summary>
    public bool IsAlias => Program?.StartsWith(AliasPrefix, StringComparison.Ordinal) == true && Program.Length > AliasPrefix.Length;

    /// <summary>
    /// The name to show: <see cref="Name"/>, else the alias without its prefix, else the file
    /// name of the program or of the file.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (Name is { } name)
            {
                return name;
            }

            if (IsAlias)
            {
                return Program![AliasPrefix.Length..];
            }

            var path = Program ?? File ?? string.Empty;
            var slash = path.LastIndexOfAny(['\\', '/']);
            return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
        }
    }

    /// <summary>
    /// Reads the RemoteApp keys of <paramref name="file"/>. Returns null when the file opens
    /// a desktop (<c>remoteapplicationmode</c> absent or 0), with
    /// <paramref name="rejection"/> at <see cref="RemoteAppRejection.None"/>; returns null
    /// with a rejection when the file asks for RemoteApp without naming what to open.
    /// </summary>
    public static RemoteAppLaunch? FromFile(RdpFile file, out RemoteAppRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(file);
        rejection = RemoteAppRejection.None;
        if (!file.RemoteApplicationMode)
        {
            return null;
        }

        var program = file.RemoteApplicationProgram;
        var target = file.RemoteApplicationFile;
        if (program is null && target is null)
        {
            rejection = RemoteAppRejection.NoProgram;
            return null;
        }

        return new RemoteAppLaunch(
            program,
            target,
            file.RemoteApplicationName,
            file.RemoteApplicationCmdLine,
            file.RemoteApplicationExpandCmdLine,
            file.RemoteApplicationExpandWorkingDir);
    }

    /// <summary>Readable summary for the log.</summary>
    public override string ToString() =>
        $"program \"{Program}\"{(IsAlias ? " (alias)" : string.Empty)}, file \"{File}\", name \"{Name}\", arguments \"{Args}\", expand arguments {ExpandArgs}, expand working folder {ExpandWorkingDir}";
}
