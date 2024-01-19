using MultiFunPlayer.Common;
using MultiFunPlayer.Shortcut;
using MultiFunPlayer.UI;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.ComponentModel;
using System.Net.WebSockets;
using System.Text;

namespace MultiFunPlayer.OutputTarget.ViewModels;

[DisplayName("WebSocket")]
internal sealed class WebSocketOutputTarget(int instanceIndex, IEventAggregator eventAggregator, IDeviceAxisValueProvider valueProvider)
    : AsyncAbstractOutputTarget(instanceIndex, eventAggregator, valueProvider)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public override ConnectionStatus Status { get; protected set; }

    public Uri Uri { get; set; } = new Uri("ws://127.0.0.1/ws");

    protected override IUpdateContext RegisterUpdateContext(DeviceAxisUpdateType updateType) => updateType switch
    {
        DeviceAxisUpdateType.FixedUpdate => new TCodeAsyncFixedUpdateContext() { UpdateInterval = 16, MinimumUpdateInterval = 16, MaximumUpdateInterval = 200 },
        DeviceAxisUpdateType.PolledUpdate => new AsyncPolledUpdateContext(),
        _ => null,
    };

    public bool IsConnected => Status == ConnectionStatus.Connected;
    public bool IsConnectBusy => Status == ConnectionStatus.Connecting || Status == ConnectionStatus.Disconnecting;
    public bool CanToggleConnect => !IsConnectBusy;

    protected override async Task RunAsync(CancellationToken token)
    {
        using var client = new ClientWebSocket();

        try
        {
            Logger.Info("Connecting to {0} at \"{1}\"", Identifier, Uri.ToString());
            await client.ConnectAsync(Uri, token)
                        .WithCancellation(1000);
            Status = ConnectionStatus.Connected;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Error when connecting to websocket");
            _ = DialogHelper.ShowErrorAsync(e, "Error when connecting to websocket", "RootDialog");
            return;
        }

        try
        {
            EventAggregator.Publish(new SyncRequestMessage());

            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            var task = await Task.WhenAny(WriteAsync(client, cancellationSource.Token), ReadAsync(client, cancellationSource.Token));
            cancellationSource.Cancel();

            task.ThrowIfFaulted();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Logger.Error(e, $"{Identifier} failed with exception");
            _ = DialogHelper.ShowErrorAsync(e, $"{Identifier} failed with exception", "RootDialog");
        }
    }

    private async Task WriteAsync(ClientWebSocket client, CancellationToken token)
    {
        try
        {
            var currentValues = DeviceAxis.All.ToDictionary(a => a, _ => double.NaN);
            var lastSentValues = DeviceAxis.All.ToDictionary(a => a, _ => double.NaN);
            await FixedUpdateAsync<TCodeAsyncFixedUpdateContext>(() => !token.IsCancellationRequested && client.State == WebSocketState.Open, async (context, elapsed) =>
            {
                Logger.Trace("Begin FixedUpdate [Elapsed: {0}]", elapsed);
                GetValues(currentValues);

                var values = context.SendDirtyValuesOnly ? currentValues.Where(x => DeviceAxis.IsValueDirty(x.Value, lastSentValues[x.Key])) : currentValues;
                values = values.Where(x => AxisSettings[x.Key].Enabled);

                var commands = context.OffloadElapsedTime ? DeviceAxis.ToString(values) : DeviceAxis.ToString(values, elapsed * 1000);
                if (client.State == WebSocketState.Open && !string.IsNullOrWhiteSpace(commands))
                {
                    Logger.Trace("Sending \"{0}\" to \"{1}\"", commands.Trim(), Uri.ToString());
                    await client.SendAsync(Encoding.UTF8.GetBytes(commands), WebSocketMessageType.Text, true, token);
                    lastSentValues.Merge(values);
                }
            }, token);
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReadAsync(ClientWebSocket client, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var message = Encoding.UTF8.GetString(await client.ReceiveAsync(token));
                Logger.Trace("Received \"{0}\" from \"{1}\"", message, Name);
            }
        }
        catch (OperationCanceledException) { }
    }

    public override void HandleSettings(JObject settings, SettingsAction action)
    {
        base.HandleSettings(settings, action);

        if (action == SettingsAction.Saving)
        {
            settings[nameof(Uri)] = Uri?.ToString();
        }
        else if (action == SettingsAction.Loading)
        {
            if (settings.TryGetValue<Uri>(nameof(Uri), out var uri))
                Uri = uri;
        }
    }

    public override void RegisterActions(IShortcutManager s)
    {
        base.RegisterActions(s);

        #region Uri
        s.RegisterAction<string>($"{Identifier}::Uri::Set", s => s.WithLabel("Uri").WithDescription("websocket uri"), uriString =>
        {
            if (Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
                Uri = uri;
        });
        #endregion
    }

    public override void UnregisterActions(IShortcutManager s)
    {
        base.UnregisterActions(s);
        s.UnregisterAction($"{Identifier}::Uri::Set");
    }

    public override async ValueTask<bool> CanConnectAsync(CancellationToken token)
    {
        try
        {
            using var client = new ClientWebSocket();
            await client.ConnectAsync(Uri, token)
                        .WithCancellation(250);

            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
