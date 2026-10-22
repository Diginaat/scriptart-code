using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ScriptArt.ProjectSystem.Debugging;

/// <summary>A request the debug adapter answered with <c>success: false</c>.</summary>
public sealed class DebugAdapterException : Exception
{
    public DebugAdapterException()
    {
    }

    public DebugAdapterException(string message)
        : base(message)
    {
    }

    public DebugAdapterException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A Debug Adapter Protocol connection over two streams: messages are JSON bodies after a <c>Content-Length</c> header. Requests
/// return their response's body; events are raised on the reading thread. See https://microsoft.github.io/debug-adapter-protocol/.
/// </summary>
internal sealed class DebugAdapterConnection : IDisposable
{
    private readonly Stream input;
    private readonly Stream output;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> pending = new();
    private int sequence;
    private Task? reading;

    /// <param name="input">The adapter's output, which this side reads.</param>
    /// <param name="output">The adapter's input, which this side writes.</param>
    public DebugAdapterConnection(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
    }

    /// <summary>An event's name and body. Raised on the reading thread, in the order the adapter sent them.</summary>
    public event Action<string, JsonNode?>? EventReceived;

    /// <summary>Completes when the adapter closes its output; pending requests then fail.</summary>
    public Task Completion => reading ?? Task.CompletedTask;

    public void Start() => reading ??= Task.Run(ReadLoopAsync);

    /// <summary>Sends a request and returns the body of its response.</summary>
    /// <exception cref="DebugAdapterException">The adapter reported failure or closed the connection.</exception>
    public async Task<JsonNode?> RequestAsync(string command, JsonObject? arguments, CancellationToken cancellationToken = default)
    {
        int seq = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[seq] = completion;
        var message = new JsonObject { ["seq"] = seq, ["type"] = "request", ["command"] = command };
        if (arguments is not null)
        {
            message["arguments"] = arguments;
        }

        try
        {
            await WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            pending.TryRemove(seq, out _);
            throw new DebugAdapterException($"The debugger is not running ({exception.Message}).", exception);
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task.ConfigureAwait(false);
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteMessageAsync(output, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>Writes one framed message.</summary>
    internal static async Task WriteMessageAsync(Stream stream, JsonNode message, CancellationToken cancellationToken)
    {
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one framed message; null at the end of the stream.</summary>
    /// <exception cref="InvalidDataException">The header has no valid <c>Content-Length</c>.</exception>
    internal static async Task<JsonNode?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        int length = -1;
        while (true)
        {
            string? line = await ReadHeaderLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                break;
            }

            const string prefix = "Content-Length:";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line.AsSpan(prefix.Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                length = value;
            }
        }

        if (length < 0)
        {
            throw new InvalidDataException("A debug adapter message has no Content-Length header.");
        }

        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(body);
    }

    // A header line without its CR LF; null at the end of the stream.
    private static async Task<string?> ReadHeaderLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        byte[] buffer = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) == 0)
            {
                return null;
            }

            if (buffer[0] == '\n')
            {
                return line.ToString().TrimEnd('\r');
            }

            line.Append((char)buffer[0]);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await ReadMessageAsync(input, CancellationToken.None).ConfigureAwait(false) is { } message)
            {
                Dispatch(message);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException or ObjectDisposedException or EndOfStreamException)
        {
            // The adapter exited or wrote something that is not the protocol; pending requests fail below.
        }

        foreach (int seq in pending.Keys)
        {
            if (pending.TryRemove(seq, out TaskCompletionSource<JsonNode?>? completion))
            {
                completion.TrySetException(new DebugAdapterException("The debugger closed the connection."));
            }
        }
    }

    private void Dispatch(JsonNode message)
    {
        switch ((string?)message["type"])
        {
            case "response":
                if (message["request_seq"]?.GetValue<int>() is { } requestSeq && pending.TryRemove(requestSeq, out TaskCompletionSource<JsonNode?>? completion))
                {
                    if (message["success"]?.GetValue<bool>() == true)
                    {
                        completion.TrySetResult(message["body"]?.DeepClone());
                    }
                    else
                    {
                        completion.TrySetException(new DebugAdapterException((string?)message["message"] ?? $"The debugger refused '{(string?)message["command"]}'."));
                    }
                }

                break;
            case "event":
                EventReceived?.Invoke((string?)message["event"] ?? "", message["body"]?.DeepClone());
                break;
            case "request":
                // Reverse requests such as runInTerminal are not supported; answer so the adapter does not wait.
                _ = WriteAsync(new JsonObject
                {
                    ["seq"] = Interlocked.Increment(ref sequence),
                    ["type"] = "response",
                    ["request_seq"] = message["seq"]?.GetValue<int>() ?? 0,
                    ["command"] = (string?)message["command"],
                    ["success"] = false,
                    ["message"] = "Not supported.",
                }, CancellationToken.None);
                break;
        }
    }

    public void Dispose() => writeLock.Dispose();
}
