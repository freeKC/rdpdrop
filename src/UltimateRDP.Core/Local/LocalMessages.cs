using System.Text.Json;
using System.Text.Json.Serialization;
using UltimateRDP.Core.Launchers;
using UltimateRDP.Core.Protocol;
using UltimateRDP.Core.RemoteApp;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Core.Local;

/// <summary>
/// What <c>UltimateRDP.Agent.exe --send</c> hands to the agent of its session through the
/// <see cref="SameUserPipe"/>: the paths to send to the client computer.
/// </summary>
/// <param name="Paths">Full paths of files and folders, 1 to <see cref="SendOfferMessage.MaxItems"/>.</param>
public sealed record SendRequest(IReadOnlyList<string> Paths);

/// <summary>What a second start of the client hands to the first one: the file to open and its options.</summary>
/// <param name="Path">The .rdp file, or null.</param>
/// <param name="Window">What <c>--show</c> or <c>--hidden</c> asked for.</param>
/// <param name="AskCredentials">True with <c>--ask-credentials</c>.</param>
/// <param name="Connection">
/// The options of a connection, since 0.10.0: on top of <paramref name="Path"/>, or alone with
/// <c>--server</c>. Null or empty without any; with neither a file nor a server the window of
/// the client shows.
/// </param>
public sealed record OpenRequest(string? Path, LaunchWindow Window, bool AskCredentials, ConnectionOptions? Connection = null);

/// <summary>The answer to a request.</summary>
/// <param name="Ok">True when the request was taken.</param>
/// <param name="Message">Why not, for the user, in their language; null when taken.</param>
public sealed record LocalAnswer(bool Ok, string? Message = null);

/// <summary>The JSON of the messages of the <see cref="SameUserPipe"/>, and their checks.</summary>
public static class LocalMessages
{
    /// <summary>Longest path accepted, in characters.</summary>
    public const int MaxPathLength = 32767;

    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>The bytes of a message.</summary>
    public static byte[] Encode<T>(T message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);

    /// <summary>A send request, or null when the bytes are not one, or a path is not a full local path.</summary>
    public static SendRequest? DecodeSend(byte[] bytes)
    {
        var request = Decode<SendRequest>(bytes);
        return request?.Paths is { Count: > 0 and <= SendOfferMessage.MaxItems } paths && paths.All(IsAcceptablePath) ? request : null;
    }

    /// <summary>
    /// An open request, or null when the bytes are not one, its path is not a full local path,
    /// or its connection does not follow the rules of the command line (<see cref="ConnectionOptions.Check"/>).
    /// </summary>
    public static OpenRequest? DecodeOpen(byte[] bytes)
    {
        var request = Decode<OpenRequest>(bytes);
        return request is not null
            && Enum.IsDefined(request.Window)
            && (request.Path is null || IsAcceptablePath(request.Path))
            && request.Connection?.Check(hasFile: request.Path is not null) is null
                ? request
                : null;
    }

    /// <summary>An answer, or null when the bytes are not one.</summary>
    public static LocalAnswer? DecodeAnswer(byte[] bytes) => Decode<LocalAnswer>(bytes);

    /// <summary>
    /// True for a full path of a local drive or a share, without control characters and at
    /// most <see cref="MaxPathLength"/> long. Device paths such as <c>\\.\</c> and <c>\\?\</c>
    /// are refused.
    /// </summary>
    public static bool IsAcceptablePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.Length <= MaxPathLength
        && !path.Any(char.IsControl)
        && !path.StartsWith(@"\\.\", StringComparison.Ordinal)
        && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
        && Path.IsPathFullyQualified(path);

    /// <summary>
    /// The items of a send: each path with its name in the transfer, as a drop names them
    /// (<see cref="TransferPaths.ItemName"/>). <paramref name="kind"/> says whether a path is a
    /// folder, a file, or null when it does not exist. Returns null with the path that
    /// <paramref name="problem"/> names when a path does not exist, has no valid name, or has
    /// the name of another one.
    /// </summary>
    public static (IReadOnlyList<DropItem> Drops, IReadOnlyList<TransferItem> Items)? PlanSend(IReadOnlyList<string> paths, Func<string, bool?> kind, out SendPlanProblem problem, out string? problemPath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(kind);
        var drops = new List<DropItem>(paths.Count);
        var items = new List<TransferItem>(paths.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            problemPath = path;
            if (kind(path) is not { } isDirectory)
            {
                problem = SendPlanProblem.Missing;
                return null;
            }

            var name = TransferPaths.ItemName(path);
            if (!TransferPaths.IsValidName(name))
            {
                problem = SendPlanProblem.BadName;
                return null;
            }

            if (!names.Add(name))
            {
                problem = SendPlanProblem.SameName;
                return null;
            }

            drops.Add(new DropItem(path, isDirectory));
            items.Add(new TransferItem(name, isDirectory));
        }

        problem = paths.Count == 0 ? SendPlanProblem.Empty : SendPlanProblem.None;
        problemPath = null;
        return paths.Count == 0 ? null : (drops, items);
    }

    private static T? Decode<T>(byte[] bytes)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

/// <summary>Why <see cref="LocalMessages.PlanSend"/> refused the paths.</summary>
public enum SendPlanProblem
{
    /// <summary>The paths are fine.</summary>
    None,

    /// <summary>No path.</summary>
    Empty,

    /// <summary>A path does not exist.</summary>
    Missing,

    /// <summary>A path has no name that a file can have, such as the root of a share.</summary>
    BadName,

    /// <summary>Two paths have the same name.</summary>
    SameName,
}
