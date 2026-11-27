using IOTLinkAPI.Configs;
using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform.Events.MQTT;
using IOTLinkAPI.Platform.HomeAssistant;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using System;
using System.Timers;
using static IOTLinkAPI.Platform.Events.MQTT.MQTTHandlers;

namespace IOTLinkService.Service.MQTT
{
    internal class MQTTClientManager : IDisposable
    {
        enum EventType
        {
            INITIAL = 0,
            DISCONNECTED = 1,
            CONNECTED = 2
        }

        private const int InitialVerificationInterval = 60000;
        private const int VerificationInterval = 10000;
        private const int RefreshInterval = 60000;

        private static readonly MQTTClientManager Instance = new MQTTClientManager(
            () => MqttConfig.FromConfiguration(ApplicationConfigHelper.GetEngineConfig()?.GetValue("mqtt")),
            () => new MqttFactory().CreateManagedMqttClient());

        private readonly Func<MqttConfig> _loadConfig;
        private readonly Func<IManagedMqttClient> _createClient;
        private readonly Timer _verifyTimer = new Timer(InitialVerificationInterval) { AutoReset = false };
        private readonly Timer _refreshTimer = new Timer(RefreshInterval);
        private readonly object lifecycleLock = new object();
        private readonly object publishLock = new object();
        private MQTTClient _mqttClient;
        private bool _running;
        private bool _disposed;
        private bool _publishFailed;

        private EventType lastFiredEvent = EventType.INITIAL;

        public event MQTTEventHandler OnMQTTConnected;
        public event MQTTEventHandler OnMQTTDisconnected;
        public event MQTTMessageEventHandler OnMQTTMessageReceived;
        public event MQTTRefreshMessageEventHandler OnMQTTRefreshMessageReceived;

        public static MQTTClientManager GetInstance()
        {
            return Instance;
        }

        internal MQTTClientManager(Func<MqttConfig> loadConfig, Func<IManagedMqttClient> createClient)
        {
            _loadConfig = loadConfig;
            _createClient = createClient;
            _verifyTimer.Elapsed += OnVerifyTimerElapsed;
            _refreshTimer.Elapsed += OnRefreshTimerElapsed;
        }

        public void Start()
        {
            lock (lifecycleLock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(MQTTClientManager));
                if (_running)
                    return;

                Connect();
                _running = true;
                _verifyTimer.Interval = InitialVerificationInterval;
                _verifyTimer.Start();
                _refreshTimer.Start();
            }
        }

        public void Stop()
        {
            lock (lifecycleLock)
            {
                if (!_running)
                    return;

                _running = false;
                _verifyTimer.Stop();
                _refreshTimer.Stop();
                Disconnect();
            }
        }

        internal void ReloadConfiguration()
        {
            lock (lifecycleLock)
            {
                if (_running)
                {
                    Disconnect();
                    Connect();
                }
            }
        }

        public void Dispose()
        {
            lock (lifecycleLock)
            {
                if (_disposed)
                    return;

                try
                {
                    Stop();
                }
                finally
                {
                    _disposed = true;
                    _verifyTimer.Dispose();
                    _refreshTimer.Dispose();
                }
            }
        }

        internal void PublishMessage(string addonTopic, string message, string addonId = null)
        {
            lock (publishLock)
            {
                LoggerHelper.Verbose("MQTTClientManager::PublishMessage('{0}', '{1}')", addonTopic, message);

                try
                {
                    _mqttClient?.PublishMessage(addonTopic, message, addonId);
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error("MQTTClientManager::PublishMessage('{0}', '{1}') -> {2}", addonTopic, message, ex.Message);
                    _publishFailed = true;
                }
            }
        }

        internal void PublishMessage(string topic, byte[] message, string addonId = null)
        {
            lock (publishLock)
            {
                LoggerHelper.Verbose("MQTTClientManager::PublishMessage('{0}', '{1}')", topic, message);

                try
                {
                    _mqttClient?.PublishMessage(topic, message, addonId);
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error("MQTTClientManager::PublishMessage('{0}', '{1}') -> {2}", topic, message, ex.Message);
                    _publishFailed = true;
                }
            }
        }

        internal void PublishDiscoveryMessage(string addonTopic, string preffixName, HassDiscoveryOptions discoveryOptions, string addonId = null)
        {
            lock (publishLock)
            {
                LoggerHelper.Verbose("MQTTClientManager::PublishDiscoveryMessage('{0}', '{1}', '{2}')", addonTopic, preffixName, discoveryOptions);

                try
                {
                    _mqttClient?.PublishDiscoveryMessage(addonTopic, preffixName, discoveryOptions, addonId);
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error("MQTTClientManager::PublishDiscoveryMessage('{0}', '{1}', '{2}') -> {3}", addonTopic, preffixName, discoveryOptions, ex.Message);
                    _publishFailed = true;
                }
            }
        }

        private void Connect()
        {
            var client = new MQTTClient();
            if (!client.Init(_loadConfig()))
                return;

            client.OnMQTTConnected += OnMQTTConnectedHandler;
            client.OnMQTTDisconnected += OnMQTTDisconnectedHandler;
            client.OnMQTTMessageReceived += OnMQTTMessageReceivedHandler;
            client.OnMQTTRefreshMessageReceived += OnMQTTRefreshMessageReceivedHandler;
            try
            {
                lock (publishLock)
                {
                    _mqttClient = client;
                    _publishFailed = false;
                    client.Connect(_createClient());
                }
            }
            catch
            {
                Disconnect(true);
                throw;
            }
        }

        private void Disconnect(bool skipLastWill = false)
        {
            MQTTClient client;
            lock (publishLock)
            {
                client = _mqttClient;
                _mqttClient = null;
            }

            // A callback can publish while MQTTnet waits for it to finish in StopAsync.
            if (client != null)
            {
                client.CleanEvents();
                client.Disconnect(skipLastWill);
            }
            lastFiredEvent = EventType.INITIAL;
        }

        private void OnVerifyTimerElapsed(object sender, ElapsedEventArgs e)
        {
            lock (lifecycleLock)
            {
                if (!_running)
                    return;

                try
                {
                    lock (publishLock)
                    {
                        if (!_publishFailed && _mqttClient != null && _mqttClient.IsConnected())
                            return;
                    }
                    Disconnect(true);
                    Connect();
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error("MQTT connection verification failed: {0}", ex);
                }
                finally
                {
                    _verifyTimer.Interval = VerificationInterval;
                    _verifyTimer.Start();
                }
            }
        }

        private void OnRefreshTimerElapsed(object sender, ElapsedEventArgs e)
        {
            lock (publishLock)
            {
                try
                {
                    _mqttClient?.SendLWTConnect();
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error("MQTT LWT refresh failed: {0}", ex);
                }
            }
        }

        private void OnMQTTConnectedHandler(object sender, MQTTEventEventArgs e)
        {
            try
            {
                if (EventType.CONNECTED.Equals(lastFiredEvent))
                {
                    LoggerHelper.Error("MQTTClientManager::OnMQTTConnectedHandler() - DUPLICATED Connected Event");
                    return;
                }

                lastFiredEvent = EventType.CONNECTED;
                OnMQTTConnected?.Invoke(sender, e);
            }
            catch (Exception ex)
            {
                LoggerHelper.Error("MQTTClientManager::OnMQTTConnectedHandler() - Error: {0}", ex.ToString());
            }
        }

        private void OnMQTTDisconnectedHandler(object sender, MQTTEventEventArgs e)
        {
            try
            {
                if (EventType.DISCONNECTED.Equals(lastFiredEvent))
                {
                    LoggerHelper.Error("MQTTClientManager::OnMQTTDisconnectedHandler() - DUPLICATED Disconnected Event");
                    return;
                }

                lastFiredEvent = EventType.DISCONNECTED;
                OnMQTTDisconnected?.Invoke(sender, e);
            }
            catch (Exception ex)
            {
                LoggerHelper.Error("MQTTClientManager::OnMQTTDisconnectedHandler() - Error: {0}", ex.ToString());
            }
        }

        private void OnMQTTMessageReceivedHandler(object sender, MQTTMessageEventEventArgs e)
        {
            try
            {
                OnMQTTMessageReceived?.Invoke(sender, e);
            }
            catch (Exception ex)
            {
                LoggerHelper.Error("MQTTClientManager::OnMQTTMessageReceivedHandler() - Error: {0}", ex.ToString());
            }
        }

        private void OnMQTTRefreshMessageReceivedHandler(object sender, EventArgs e)
        {
            try
            {
                OnMQTTRefreshMessageReceived?.Invoke(sender, e);
            }
            catch (Exception ex)
            {
                LoggerHelper.Error("MQTTClientManager::OnMQTTRefreshMessageReceivedHandler() - Error: {0}", ex.ToString());
            }
        }
    }
}
