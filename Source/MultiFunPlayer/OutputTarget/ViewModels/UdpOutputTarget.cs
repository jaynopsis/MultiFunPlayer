using MultiFunPlayer.Common;
using MultiFunPlayer.Input;
using MultiFunPlayer.Input.TCode;
using MultiFunPlayer.Shortcut;
using MultiFunPlayer.UI;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MultiFunPlayer.OutputTarget.ViewModels;

[DisplayName("UDP")]
internal sealed class UdpOutputTarget(int instanceIndex, IEventAggregator eventAggregator, IDeviceAxisValueProvider valueProvider, IInputProcessorManager inputManager)
    : ThreadAbstractOutputTarget(instanceIndex, eventAggregator, valueProvider)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public override ConnectionStatus Status { get; protected set; }

    public EndPoint Endpoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 8080);

    public bool IsConnected => Status == ConnectionStatus.Connected;
    public bool IsConnectBusy => Status == ConnectionStatus.Connecting || Status == ConnectionStatus.Disconnecting;
    public bool CanToggleConnect => !IsConnectBusy;

    protected override IUpdateContext RegisterUpdateContext(DeviceAxisUpdateType updateType) => updateType switch
    {
        DeviceAxisUpdateType.FixedUpdate => new TCodeThreadFixedUpdateContext(),
        DeviceAxisUpdateType.PolledUpdate => new ThreadPolledUpdateContext(),
        _ => null,
    };

    protected override void Run(CancellationToken token)
    {
        using var client = new UdpClient();

        try
        {
            Logger.Info("Connecting to {0} at \"{1}\"", Identifier, $"udp://{Endpoint}");

            const int SIO_UDP_CONNRESET = -1744830452;
            client.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, [0, 0, 0, 0], null);

            client.Connect(Endpoint);
            Status = ConnectionStatus.Connected;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Error when connecting to server");
            _ = DialogHelper.ShowErrorAsync(e, "Error when connecting to server", "RootDialog");
            return;
        }

        try
        {
            EventAggregator.Publish(new SyncRequestMessage());

            using var _ = inputManager.Register<TCodeInputProcessor>(out var tcodeInputProcessor);

            var buffer = new byte[256];
            var receiveBuffer = new SplittingStringBuffer('\n');
            var currentValues = DeviceAxis.All.ToDictionary(a => a, _ => double.NaN);
            var lastSentValues = DeviceAxis.All.ToDictionary(a => a, _ => double.NaN);
            FixedUpdate<TCodeThreadFixedUpdateContext>(() => !token.IsCancellationRequested, (context, elapsed) =>
            {
                Logger.Trace("Begin FixedUpdate [Elapsed: {0}]", elapsed);
                GetValues(currentValues);

                if (client.Available > 0)
                {
                    var endpoint = new IPEndPoint(IPAddress.Any, 0);
                    var message = Encoding.UTF8.GetString(client.Receive(ref endpoint));
                    Logger.Debug("Received \"{0}\" from \"{1}\"", message, $"udp://{endpoint}");

                    receiveBuffer.Push(message);
                    foreach (var command in receiveBuffer.Consume())
                        tcodeInputProcessor.Parse(command);
                }

                var values = context.SendDirtyValuesOnly ? currentValues.Where(x => DeviceAxis.IsValueDirty(x.Value, lastSentValues[x.Key])) : currentValues;
                values = values.Where(x => AxisSettings[x.Key].Enabled);

                var commands = context.OffloadElapsedTime ? DeviceAxis.ToString(values) : DeviceAxis.ToString(values, elapsed * 1000);
                if (!string.IsNullOrWhiteSpace(commands))
                {
                    Logger.Trace("Sending \"{0}\" to \"{1}\"", commands.Trim(), $"udp://{Endpoint}");

                    var encoded = Encoding.UTF8.GetBytes(commands, buffer);
                    client.Send(buffer, encoded);
                    lastSentValues.Merge(values);
                }
            });
        }
        catch (Exception e)
        {
            Logger.Error(e, $"{Identifier} failed with exception");
            _ = DialogHelper.ShowErrorAsync(e, $"{Identifier} failed with exception", "RootDialog");
        }
    }

    public override void HandleSettings(JObject settings, SettingsAction action)
    {
        base.HandleSettings(settings, action);

        if (action == SettingsAction.Saving)
        {
            settings[nameof(Endpoint)] = Endpoint?.ToString();
        }
        else if (action == SettingsAction.Loading)
        {
            if (settings.TryGetValue<EndPoint>(nameof(Endpoint), out var endpoint))
                Endpoint = endpoint;
        }
    }

    public override void RegisterActions(IShortcutManager s)
    {
        base.RegisterActions(s);

        #region Endpoint
        s.RegisterAction<string>($"{Identifier}::Endpoint::Set", s => s.WithLabel("Endpoint").WithDescription("ip/host:port"), endpointString =>
        {
            if (NetUtils.TryParseEndpoint(endpointString, out var endpoint))
                Endpoint = endpoint;
        });
        #endregion
    }

    public override void UnregisterActions(IShortcutManager s)
    {
        base.UnregisterActions(s);
        s.UnregisterAction($"{Identifier}::Endpoint::Set");
    }

    public override async ValueTask<bool> CanConnectAsync(CancellationToken token) => await ValueTask.FromResult(true);
}
