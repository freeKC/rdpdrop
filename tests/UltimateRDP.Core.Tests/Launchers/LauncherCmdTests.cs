using System.Diagnostics;
using System.Text;
using UltimateRDP.Core.Launchers;
using UltimateRDP.Core.RemoteApp;

namespace UltimateRDP.Core.Tests.Launchers;

/// <summary>
/// The launchers read by the real cmd.exe, without a window: the <c>start</c> line is turned
/// into an <c>echo</c> of the same arguments, which prints them as <c>start</c> would pass
/// them on, after the percent expansion and the carets. This checks <see cref="CmdModel"/>
/// against cmd.exe itself. A value that broke out of its quotes would run the
/// <c>echo INJECTED</c> it carries.
/// </summary>
public sealed class LauncherCmdTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ultimaterdp-launcher-").FullName;

    public static TheoryData<string> Values =>
    [
        "a & echo INJECTED",
        "x\" & echo INJECTED & \"",
        "\" & echo INJECTED",
        "\\\" & echo INJECTED & \\\"",
        "a | echo INJECTED",
        "a > injected.txt",
        "x\" > injected.txt & \"",
        "^& echo INJECTED",
        "%PATH% %~dp0 %1 !PATH! 50%",
        ") & echo INJECTED & (",
        @"C:\folder\",
        @"a\\""b \""c\\",
        "\"\"",
        "é ü 日本語 \U0001F600",
    ];

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [MemberData(nameof(Values))]
    public void Cmd_gives_the_value_as_it_is_and_runs_nothing_else(string value)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var connection = new ConnectionOptions { Server = "server.example.com", RemoteApp = "||Calc", RemoteAppArgs = value };
        var options = new LaunchOptions(null, LaunchWindow.Auto, false, null) { Connection = connection };
        var text = LauncherBatch.Build(new LauncherSpec(LauncherExeKind.FullPath, @"C:\Tools\UltimateRDP.exe", options, null, ["R&D | <x> \"y\" & echo INJECTED", "100% ^"]));
        var echoed = text.Replace("start \"\" \"C:\\Tools\\UltimateRDP.exe\"", "echo ARGS:", StringComparison.Ordinal);

        var output = RunCmd(echoed, utf8: text.Contains("chcp 65001", StringComparison.Ordinal));

        var lines = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(lines);
        Assert.StartsWith("ARGS: ", line, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_folder, "injected*"));
        var parsed = LaunchOptions.Parse(CmdModel.SplitArguments(line["ARGS:".Length..]));
        Assert.Null(parsed.Error);
        Assert.Equal(connection, parsed.Connection);

        // The model of cmd.exe reads the same.
        var model = CmdModel.RemoveCarets(CmdModel.ExpandPercents(echoed.Split("\r\n")[^2], _folder + "\\", new Dictionary<string, string>()));
        Assert.Equal(line, model["echo ".Length..]);
    }

    private string RunCmd(string batch, bool utf8)
    {
        var path = Path.Combine(_folder, "launcher.bat");
        File.WriteAllBytes(path, LauncherBatch.FileEncoding.GetBytes(batch));
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = _folder,
            StandardOutputEncoding = utf8 ? new UTF8Encoding(false) : null,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(string.Empty, error.Result);
        return output;
    }
}
