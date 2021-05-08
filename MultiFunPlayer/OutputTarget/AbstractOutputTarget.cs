﻿using MultiFunPlayer.Common;
using MultiFunPlayer.Common.Messages;
using Newtonsoft.Json.Linq;
using PropertyChanged;
using Stylet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MultiFunPlayer.OutputTarget
{
    public abstract class AbstractOutputTarget : Screen, IHandle<AppSettingsMessage>, IDisposable, IOutputTarget
    {
        private readonly IDeviceAxisValueProvider _valueProvider;

        public abstract string Name { get; }
        [SuppressPropertyChangedWarnings] public abstract OutputTargetStatus Status { get; protected set; }
        public bool ContentVisible { get; set; }

        public ObservableConcurrentDictionary<DeviceAxis, DeviceAxisSettings> AxisSettings { get; protected set; }
        public int UpdateRate { get; set; }
        protected Dictionary<DeviceAxis, float> Values { get; }

        protected AbstractOutputTarget(IEventAggregator eventAggregator, IDeviceAxisValueProvider valueProvider)
        {
            eventAggregator.Subscribe(this);
            _valueProvider = valueProvider;

            Values = EnumUtils.ToDictionary<DeviceAxis, float>(axis => axis.DefaultValue());
            AxisSettings = new ObservableConcurrentDictionary<DeviceAxis, DeviceAxisSettings>(EnumUtils.ToDictionary<DeviceAxis, DeviceAxisSettings>(_ => new DeviceAxisSettings()));
            UpdateRate = 60;
        }

        public async Task ToggleConnectAsync()
        {
            if (Status == OutputTargetStatus.Connected || Status == OutputTargetStatus.Connecting)
                await DisconnectWithStatusAsync().ConfigureAwait(true);
            else
                await ConnectWithStatusAsync().ConfigureAwait(true);
        }

        protected abstract Task ConnectAsync();

        protected async Task ConnectWithStatusAsync()
        {
            if (Status != OutputTargetStatus.Disconnected)
                return;

            Status = OutputTargetStatus.Connecting;
            await ConnectAsync().ConfigureAwait(true);
        }

        protected virtual async Task DisconnectAsync()
        {
            Dispose(disposing: false);
            await Task.Delay(1000);
        }

        protected async Task DisconnectWithStatusAsync()
        {
            if (Status == OutputTargetStatus.Disconnected || Status == OutputTargetStatus.Disconnecting)
                return;

            Status = OutputTargetStatus.Disconnecting;
            await DisconnectAsync().ConfigureAwait(true);
            Status = OutputTargetStatus.Disconnected;
        }

        protected void UpdateValues()
        {
            foreach (var axis in EnumUtils.GetValues<DeviceAxis>())
            {
                var value = _valueProvider?.GetValue(axis) ?? float.NaN;
                if (!float.IsFinite(value))
                    value = axis.DefaultValue();

                var settings = AxisSettings[axis];
                Values[axis] = MathUtils.Lerp(settings.Minimum / 100f, settings.Maximum / 100f, value);
            }
        }

        protected abstract void HandleSettings(JObject settings, AppSettingsMessageType type);
        public void Handle(AppSettingsMessage message)
        {
            if (message.Type == AppSettingsMessageType.Saving)
            {
                if (!message.Settings.EnsureContainsObjects("OutputTarget", Name)
                 || !message.Settings.TryGetObject(out var settings, "OutputTarget", Name))
                    return;

                settings[nameof(UpdateRate)] = new JValue(UpdateRate);
                settings[nameof(AxisSettings)] = JObject.FromObject(AxisSettings);

                HandleSettings(settings, message.Type);
            }
            else if (message.Type == AppSettingsMessageType.Loading)
            {
                if (!message.Settings.TryGetObject(out var settings, "OutputTarget", Name))
                    return;

                if (settings.TryGetValue<int>(nameof(UpdateRate), out var updateRate))
                    UpdateRate = updateRate;
                if (settings.TryGetValue<Dictionary<DeviceAxis, DeviceAxisSettings>>(nameof(AxisSettings), out var axisSettingsMap))
                    foreach (var (axis, axisSettings) in axisSettingsMap)
                        AxisSettings[axis] = axisSettings;

                HandleSettings(settings, message.Type);
            }
        }

        protected virtual void Dispose(bool disposing) { }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }

    public abstract class ThreadAbstractOutputTarget : AbstractOutputTarget
    {
        private CancellationTokenSource _cancellationSource;
        private Thread _thread;

        protected ThreadAbstractOutputTarget(IEventAggregator eventAggregator, IDeviceAxisValueProvider valueProvider)
            : base(eventAggregator, valueProvider) { }

        protected abstract void Run(CancellationToken token);

        protected override async Task ConnectAsync()
        {
            await Task.Delay(1000).ConfigureAwait(true);

            _cancellationSource = new CancellationTokenSource();
            _thread = new Thread(() =>
            {
                Run(_cancellationSource.Token);
                _ = Execute.OnUIThreadAsync(async () => await DisconnectWithStatusAsync().ConfigureAwait(true));
            })
            {
                IsBackground = true
            };
            _thread.Start();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            _cancellationSource?.Cancel();
            _thread?.Join();
            _cancellationSource?.Dispose();

            _cancellationSource = null;
            _thread = null;
        }
    }

    public abstract class AsyncAbstractOutputTarget : AbstractOutputTarget
    {
        private CancellationTokenSource _cancellationSource;
        private Task _task;

        protected AsyncAbstractOutputTarget(IEventAggregator eventAggregator, IDeviceAxisValueProvider valueProvider)
            : base(eventAggregator, valueProvider) { }

        protected abstract Task RunAsync(CancellationToken token);

        protected override async Task ConnectAsync()
        {
            await Task.Delay(1000).ConfigureAwait(true);

            _cancellationSource = new CancellationTokenSource();
            _task = Task.Factory.StartNew(() => RunAsync(_cancellationSource.Token),
                _cancellationSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
                .Unwrap();
            _ = _task.ContinueWith(_ => Execute.OnUIThreadAsync(async () => await DisconnectWithStatusAsync().ConfigureAwait(true))).Unwrap();
        }

        protected override async void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            _cancellationSource?.Cancel();

            if (_task != null)
                await _task;

            _cancellationSource?.Dispose();

            _cancellationSource = null;
            _task = null;
        }
    }
}