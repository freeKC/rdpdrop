using UltimateRDP.Agent.Localization;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Agent.Resources;
using UltimateRDP.Agent.Setup;
using UltimateRDP.Core.Local;

namespace UltimateRDP.Agent.Send;

/// <summary>
/// <c>UltimateRDP.Agent.exe --send &lt;paths&gt;</c>, since 0.9.0, which the Send To shortcut
/// runs with the selected files: hands the paths to the agent of the same session through a
/// <see cref="SameUserPipe"/>, waits for its answer and exits. Only a refusal shows a message.
/// </summary>
internal static class SendCommand
{
    /// <summary>The purpose of the pipe of the agent, in its name.</summary>
    public const string PipePurpose = "AgentSend";

    /// <summary>Time to reach the agent.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Time for the answer: the agent lists the files and waits for the client.</summary>
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(60);

    public static int Run(FileLogger logger, IReadOnlyList<string> arguments)
    {
        var paths = new List<string>(arguments.Count);
        foreach (var argument in arguments)
        {
            var trimmed = argument.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                paths.Add(Path.GetFullPath(trimmed));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
            {
                CommandLineOutput.Error(LocalText.Of(nameof(Strings.SendMissing), trimmed).ForUser());
                return 2;
            }
        }

        if (paths.Count == 0)
        {
            CommandLineOutput.Error(Strings.SendNothing);
            return 2;
        }

        var request = new SendRequest(paths);
        if (LocalMessages.DecodeSend(LocalMessages.Encode(request)) is null)
        {
            CommandLineOutput.Error(Strings.SendNothing);
            return 2;
        }

        try
        {
            var answer = LocalMessages.DecodeAnswer(SameUserPipe.Call(PipePurpose, LocalMessages.Encode(request), ConnectTimeout, AnswerTimeout));
            if (answer is { Ok: true })
            {
                logger.Info($"--send: {paths.Count} item(s) handed to the agent of the session, which sends them to the client.");
                return 0;
            }

            logger.Info($"--send: the agent refused {paths.Count} item(s): {answer?.Message ?? "(no answer)"}");
            CommandLineOutput.Error(answer?.Message ?? Strings.SendNoAgent);
            return 1;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.Info($"--send: the agent of the session cannot be reached. {ex.Message}");
            CommandLineOutput.Error(Strings.SendNoAgent);
            return 1;
        }
    }
}
