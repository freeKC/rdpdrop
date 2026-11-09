using RdpDrop.Client.Resources;
using RdpDrop.Core.Paths;

namespace RdpDrop.Client.Localization;

/// <summary>
/// Texts for the user that explain why a dragged path cannot be dropped. The log keeps
/// the English text of <see cref="ClientPathMapper.Describe"/>.
/// </summary>
internal static class PathTexts
{
    public static LocalText Describe(PathRejection rejection, string path) => rejection switch
    {
        PathRejection.None => LocalText.Empty,
        PathRejection.Empty => LocalText.Of(nameof(Strings.PathEmpty)),
        PathRejection.DevicePath => LocalText.Of(nameof(Strings.PathDevice), path),
        PathRejection.NetworkPath => LocalText.Of(nameof(Strings.PathNetwork), path),
        PathRejection.RelativePath => LocalText.Of(nameof(Strings.PathRelative), path),
        PathRejection.DriveRelativePath => LocalText.Of(nameof(Strings.PathDriveRelative), path),
        PathRejection.DotSegment => LocalText.Of(nameof(Strings.PathDotSegment), path),
        _ => LocalText.Of(nameof(Strings.PathInvalidCharacters), path),
    };
}
