namespace UltimateRDP.Core;

/// <summary>
/// The brand the product shows, since 0.10.0: "UltimateRDP by Aprogsys", the same in every
/// language. It stands where the name of the product is shown alone as a brand (the loader, the
/// About dialogs, the tooltips of the notification icons, the launchers); window titles, files,
/// processes, logs, registry values and folders keep the name <see cref="Product"/>.
/// </summary>
public static class Brand
{
    /// <summary>The name of the product, as files and processes are named.</summary>
    public const string Product = "UltimateRDP";

    /// <summary>The words after the name of the product.</summary>
    public const string Suffix = " by Aprogsys";

    /// <summary>The brand: "UltimateRDP by Aprogsys".</summary>
    public const string Text = Product + Suffix;
}
