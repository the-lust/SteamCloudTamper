using SteamKit2;

namespace SteamCloudTamper.Engines;

public enum AuthMode { Anonymous, Credentials }

public sealed class SteamSession : IAsyncDisposable
{
    private readonly SteamClient _client;
    private readonly CallbackManager _callbacks;
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _logon = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;

    public SteamSession()
    {
        var config = SteamConfiguration.Create(b => b.WithProtocolTypes(ProtocolTypes.Tcp | ProtocolTypes.Udp));
        _client = new SteamClient(config);
        _callbacks = new CallbackManager(_client);
    }

    public SteamID? SteamId { get; private set; }

    public bool IsConnected => _client.IsConnected;

    public bool IsLoggedOn => _logon.Task.IsCompletedSuccessfully;

    public CloudRpcClient Cloud { get; private set; } = null!;

    public event Action<string>? Event;

    public async Task<bool> ConnectAsync(AuthMode mode = AuthMode.Anonymous, string? username = null, string? password = null)
    {
        _cts = new CancellationTokenSource();
        _callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => _connected.TrySetResult());
        _callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ => _logon.TrySetResult(false));

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await _callbacks.RunWaitCallbackAsync(_cts.Token); }
                catch (OperationCanceledException) { break; }
                catch { /* transient */ }
            }
        });

        Event?.Invoke("Connecting to Steam3...");
        _client.Connect();
        await _connected.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var user = _client.GetHandler<SteamUser>();
        _callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        if (mode == AuthMode.Anonymous)
        {
            Event?.Invoke("Logging on anonymously...");
            user.LogOnAnonymous();
        }
        else
        {
            Event?.Invoke("Logging on with credentials...");
            user.LogOn(new SteamUser.LogOnDetails
            {
                Username = username,
                Password = password,
            });
        }

        await _logon.Task.WaitAsync(TimeSpan.FromSeconds(60));
        SteamId = IsLoggedOn ? _client.SteamID : null;
        Cloud = new CloudRpcClient(this);
        return IsLoggedOn;
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback cb)
    {
        Event?.Invoke($"Logon result: {cb.Result} (ext: {cb.ExtendedResult})");
        _logon.TrySetResult(cb.Result is EResult.OK or EResult.LogonSessionReplaced);
    }

    internal SteamClient Client => _client;

    internal CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _client.Disconnect();
        await Task.CompletedTask;
    }
}