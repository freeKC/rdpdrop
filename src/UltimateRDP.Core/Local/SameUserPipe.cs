using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace UltimateRDP.Core.Local;

/// <summary>
/// A named pipe between two processes of the same user in the same Windows session, since
/// 0.9.0: the copy of the agent started with <c>--send</c> hands its paths to the agent of
/// its session, and a second start of the client hands its .rdp file to the first one. One
/// request and one answer per connection, each a length of 4 bytes, little endian, then at
/// most <see cref="MaxMessageBytes"/> bytes.
/// </summary>
/// <remarks>
/// <para>
/// The name holds the purpose, the SID of the user and the session id, so that each user and
/// each session has its own. The server is created with <see cref="PipeOptions.CurrentUserOnly"/>,
/// which gives the pipe an access list with the current user only, and as the first and only
/// instance of its name, so that a process that took the name first is never mistaken for
/// the server. It refuses remote clients, and a client whose session is not its own. The
/// client also uses <see cref="PipeOptions.CurrentUserOnly"/>, which checks that the server
/// belongs to the same user, and checks that the server runs in its session.
/// </para>
/// <para>
/// The server waits with asynchronous calls: no thread of its own waits for a connection.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SameUserPipe : IDisposable
{
    /// <summary>Longest request or answer, in bytes.</summary>
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>Time a client of the server has to send its request and read the answer.</summary>
    public static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(10);

    private readonly NamedPipeServerStream _server;
    private readonly Func<byte[], byte[]> _handle;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SameUserPipe(string name, NamedPipeServerStream server, Func<byte[], byte[]> handle, Action<string> log)
    {
        Name = name;
        _server = server;
        _handle = handle;
        _log = log;
    }

    /// <summary>The name of the pipe, without <c>\\.\pipe\</c>.</summary>
    public string Name { get; }

    /// <summary>The name of the pipe of <paramref name="purpose"/> for the current user and session.</summary>
    public static string PipeName(string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        using var identity = WindowsIdentity.GetCurrent();
        return string.Create(CultureInfo.InvariantCulture, $"UltimateRDP.{purpose}.{identity.User?.Value ?? "unknown"}.S{CurrentSession()}");
    }

    /// <summary>
    /// Creates the server of <paramref name="purpose"/> and starts to wait for clients.
    /// <paramref name="handle"/> gets each request and returns its answer, on a thread of the
    /// pool, one request at a time. Returns null when another process of the session already
    /// holds the name, or the pipe cannot be created, with a line for <paramref name="log"/>.
    /// </summary>
    public static SameUserPipe? TryStart(string purpose, Func<byte[], byte[]> handle, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(log);
        var name = PipeName(purpose);
        NamedPipeServerStream server;
        try
        {
            // One instance: Windows then creates it as the first instance of the name, and
            // fails when a pipe of that name exists.
            server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A pipe of that name exists: Windows refuses a first instance, or a second one.
            log($"The pipe {name} is held by another process: {ex.Message}");
            return null;
        }

        var pipe = new SameUserPipe(name, server, handle, log);
        _ = pipe.ServeAsync();
        return pipe;
    }

    /// <summary>
    /// Sends <paramref name="request"/> to the server of <paramref name="purpose"/> and returns
    /// its answer. Throws a <see cref="TimeoutException"/> when no server answers within
    /// <paramref name="timeout"/>, and an <see cref="IOException"/> or an
    /// <see cref="UnauthorizedAccessException"/> when the server is not of this user and
    /// session, or the exchange fails.
    /// </summary>
    public static byte[] Call(string purpose, byte[] request, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Length > MaxMessageBytes)
        {
            throw new ArgumentException("The request is too long.", nameof(request));
        }

        using var client = new NamedPipeClientStream(".", PipeName(purpose), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        client.Connect((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue));
        if (!GetNamedPipeServerSessionId(client.SafePipeHandle, out var session) || session != CurrentSession())
        {
            throw new IOException("The server of the pipe does not run in this session.");
        }

        using var deadline = new CancellationTokenSource(ExchangeTimeout);
        using var registration = deadline.Token.Register(client.Dispose);
        Write(client, request);
        return Read(client) ?? throw new IOException("The server closed the pipe without an answer.");
    }

    /// <summary>Stops the server; a request being handled finishes first. Waits at most two seconds.</summary>
    public void Dispose()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        _stop.Cancel();
        try
        {
            _server.Dispose();
        }
        catch (IOException)
        {
            // Closing a pipe in use may fail; the handle is released all the same.
        }

        _stopped.Task.Wait(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }

    /// <summary>The session of this process.</summary>
    private static uint CurrentSession() => (uint)Process.GetCurrentProcess().SessionId;

    private static void Write(Stream stream, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, data.Length);
        stream.Write(length);
        stream.Write(data);
        stream.Flush();
    }

    /// <summary>One message, or null when the other side closed before it.</summary>
    private static byte[]? Read(Stream stream)
    {
        Span<byte> length = stackalloc byte[4];
        if (!ReadExactly(stream, length))
        {
            return null;
        }

        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size is < 0 or > MaxMessageBytes)
        {
            throw new IOException($"A message of {size} bytes is beyond the limit of {MaxMessageBytes}.");
        }

        var data = new byte[size];
        return ReadExactly(stream, data) ? data : throw new IOException("The pipe closed in the middle of a message.");
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var done = 0;
        while (done < buffer.Length)
        {
            var read = stream.Read(buffer[done..]);
            if (read == 0)
            {
                return false;
            }

            done += read;
        }

        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint clientSessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint serverSessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await _server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    ServeOne();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    _log($"A request on the pipe {Name} failed: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        if (_server.IsConnected)
                        {
                            _server.Disconnect();
                        }
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                    {
                        // The next wait fails and ends the loop when the pipe is gone.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _log($"The pipe {Name} stopped: {ex.Message}");
        }
        finally
        {
            _stopped.TrySetResult();
        }
    }

    /// <summary>One connected client: its session checked, its request read, handled and answered.</summary>
    private void ServeOne()
    {
        var handle = _server.SafePipeHandle;
        if (!GetNamedPipeClientSessionId(handle, out var session) || session != CurrentSession())
        {
            GetNamedPipeClientProcessId(handle, out var process);
            _log(string.Create(CultureInfo.InvariantCulture, $"A client of the pipe {Name} from process {process} in session {session} is not of this session; refused."));
            return;
        }

        using var deadline = new CancellationTokenSource(ExchangeTimeout);
        using var registration = deadline.Token.Register(() =>
        {
            try
            {
                _server.Disconnect();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Closed already.
            }
        });
        var request = Read(_server);
        if (request is null)
        {
            return;
        }

        byte[] answer;
        try
        {
            answer = _handle(request);
        }
        catch (Exception ex)
        {
            _log($"The handler of the pipe {Name} failed: {ex}");
            return;
        }

        Write(_server, answer.Length <= MaxMessageBytes ? answer : []);
    }
}
