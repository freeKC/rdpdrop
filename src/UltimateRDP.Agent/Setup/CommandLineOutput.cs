using UltimateRDP.Agent.Interop;

namespace UltimateRDP.Agent.Setup;

/// <summary>
/// Shows the result of a command line action. The agent is a windowed program, so it
/// writes to the standard output when it has one (redirected, or the console of the
/// parent process) and shows a message box otherwise.
/// </summary>
internal static class CommandLineOutput
{
    private static bool? _hasConsole;
    private static bool _attachedToParent;

    public static void Info(string text, bool showBoxWithoutConsole = true) => Show(text, isError: false, showBoxWithoutConsole);

    public static void Error(string text) => Show(text, isError: true, showBoxWithoutConsole: true);

    /// <summary>
    /// Leaves the console of the parent process, when the output was written there. The
    /// agent that keeps running after --install must not end when that console is closed
    /// or receives Ctrl+C.
    /// </summary>
    public static void DetachFromParentConsole()
    {
        if (_attachedToParent)
        {
            _attachedToParent = false;
            Kernel32.FreeConsole();
        }
    }

    private static void Show(string text, bool isError, bool showBoxWithoutConsole)
    {
        if (HasConsole())
        {
            var writer = isError ? Console.Error : Console.Out;
            writer.WriteLine(text);
            writer.Flush();
            return;
        }

        if (showBoxWithoutConsole)
        {
            MessageBox.Show(text, AgentInfo.ProductName, MessageBoxButtons.OK, isError ? MessageBoxIcon.Error : MessageBoxIcon.Information);
        }
    }

    private static bool HasConsole()
    {
        if (_hasConsole is { } known)
        {
            return known;
        }

        var handle = Kernel32.GetStdHandle(Kernel32.StdOutputHandle);
        var hasOutput = handle != IntPtr.Zero && handle != new IntPtr(-1);
        if (!hasOutput && Kernel32.AttachConsole(Kernel32.AttachParentProcess))
        {
            hasOutput = true;
            _attachedToParent = true;

            // The prompt of the parent shell is already printed; start on a new line.
            Console.WriteLine();
        }

        _hasConsole = hasOutput;
        return hasOutput;
    }
}
