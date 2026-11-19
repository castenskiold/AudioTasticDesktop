using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioTastic.Data.Settings;

namespace AudioTastic.App.Integration;

/// <summary>
/// SI-06 / SI-08: a request handed from a second start to the running player: files and player commands. Neither
/// means "come to the front".
/// </summary>
public sealed record InstanceCommand(FileAction Action, IReadOnlyList<string> Files, IReadOnlyList<ControlCommand>? Controls = null)
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public IReadOnlyList<ControlCommand> Controls { get; init; } = Controls ?? [];

    public static InstanceCommand From(StartupOptions options) => new(options.Action, options.Files, options.Controls);

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static InstanceCommand? Deserialize(string text)
    {
        try
        {
            var command = JsonSerializer.Deserialize<InstanceCommand>(text, Json);
            return command is { Files: not null } ? command with { Controls = command.Controls ?? [] } : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// SI-06: one AudioTastic per user and data folder. The first start owns a named mutex and listens on a named pipe
/// (a Unix domain socket on macOS/Linux) that only the same user can open; later starts send their command (one
/// JSON line) and exit. A portable copy has its own data folder and therefore runs on its own.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Thread _owner;
    private readonly ManualResetEventSlim _release;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _server;

    private SingleInstance(Thread owner, ManualResetEventSlim release, string pipeName)
    {
        _owner = owner;
        _release = release;
        _pipeName = pipeName;
    }

    /// <summary>Short per-user, per-data-folder name shared by the mutex and the pipe.</summary>
    public static string NameFor(string dataRoot)
    {
        var root = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            root = root.ToUpperInvariant();
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "|" + root));
        return "AudioTastic-" + Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>Becomes the running instance, or returns null when another one already is.</summary>
    /// <remarks>
    /// A mutex belongs to the thread that took it, so a small thread of its own takes it, holds it until
    /// <see cref="Dispose"/> and releases it; the caller's thread does not matter.
    /// </remarks>
    public static SingleInstance? TryAcquire(string dataRoot)
    {
        var name = NameFor(dataRoot);
        var release = new ManualResetEventSlim();
        var owned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => Hold(@"Local\" + name, owned, release)) { IsBackground = true, Name = "AudioTastic single instance" };
        thread.Start();
        if (owned.Task.GetAwaiter().GetResult())
        {
            return new SingleInstance(thread, release, name);
        }
        thread.Join();
        release.Dispose();
        return null;
    }

    private static void Hold(string name, TaskCompletionSource<bool> owned, ManualResetEventSlim release)
    {
        using var mutex = new Mutex(false, name);
        bool mine;
        try
        {
            mine = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            mine = true; // the previous owner ended without releasing it (a crash): it is ours now
        }
        owned.SetResult(mine);
        if (mine)
        {
            release.Wait();
            mutex.ReleaseMutex();
        }
    }

    /// <summary>Starts listening; <paramref name="received"/> runs on a background thread for each command.</summary>
    public void Listen(Action<InstanceCommand> received) => _server ??= Task.Run(() => ServeAsync(received, _stop.Token));

    private async Task ServeAsync(Action<InstanceCommand> received, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                if (InstanceCommand.Deserialize(text) is { } command)
                {
                    received(command);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A client that went away half-way: wait for the next one.
            }
        }
    }

    /// <summary>
    /// Sends <paramref name="command"/> to the running instance. False when none answered within
    /// <paramref name="timeout"/> (it may still be starting, or just exiting).
    /// </summary>
    public static bool TrySend(string dataRoot, InstanceCommand command, TimeSpan timeout)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", NameFor(dataRoot), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect((int)timeout.TotalMilliseconds);
            var bytes = Encoding.UTF8.GetBytes(command.Serialize());
            pipe.Write(bytes);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _server?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }
        _stop.Dispose();
        _release.Set();
        _owner.Join();
        _release.Dispose();
    }
}

/// <summary>
/// SI-06: commands received before the player window is ready are kept until <see cref="Attach"/>; after that each one
/// goes straight to the handler. Thread-safe.
/// </summary>
public sealed class InstanceInbox
{
    private readonly Lock _gate = new();
    private readonly List<InstanceCommand> _waiting = [];
    private Action<InstanceCommand>? _handler;

    public void Post(InstanceCommand command)
    {
        Action<InstanceCommand>? handler;
        lock (_gate)
        {
            handler = _handler;
            if (handler is null)
            {
                _waiting.Add(command);
                return;
            }
        }
        handler(command);
    }

    /// <summary>Sets the handler and returns the commands that arrived before it.</summary>
    public IReadOnlyList<InstanceCommand> Attach(Action<InstanceCommand> handler)
    {
        lock (_gate)
        {
            _handler = handler;
            var waiting = _waiting.ToArray();
            _waiting.Clear();
            return waiting;
        }
    }
}

/// <summary>
/// SI-06: commands that arrive close together are handled as one. Explorer starts one process per selected file,
/// in no reliable order, so files from several commands are sorted by path; a single command keeps its order.
/// </summary>
/// <remarks>Player commands (SI-08) run after the files, in the order they arrived; they do not bring the player forward.</remarks>
public sealed record InstanceBatch(IReadOnlyList<string> Play, IReadOnlyList<string> Enqueue, bool Activate, IReadOnlyList<ControlCommand>? Controls = null)
{
    public IReadOnlyList<ControlCommand> Controls { get; init; } = Controls ?? [];

    public static InstanceBatch Merge(IReadOnlyList<InstanceCommand> commands, OpenFilesAction defaultAction)
    {
        var play = commands.Where(c => Resolve(c.Action, defaultAction) == FileAction.Open && c.Files.Count > 0).ToArray();
        var enqueue = commands.Where(c => Resolve(c.Action, defaultAction) == FileAction.Enqueue && c.Files.Count > 0).ToArray();
        var activate = commands.Any(c => c.Files.Count == 0 && c.Controls.Count == 0) || play.Length > 0;
        return new InstanceBatch(Files(play), Files(enqueue), activate, [.. commands.SelectMany(c => c.Controls)]);
    }

    private static FileAction Resolve(FileAction action, OpenFilesAction defaultAction) => action switch
    {
        FileAction.Default => defaultAction == OpenFilesAction.Enqueue ? FileAction.Enqueue : FileAction.Open,
        _ => action,
    };

    private static string[] Files(InstanceCommand[] commands)
    {
        var files = commands.SelectMany(c => c.Files);
        if (commands.Length > 1)
        {
            files = files.Order(StringComparer.OrdinalIgnoreCase);
        }
        return [.. files.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)];
    }
}
