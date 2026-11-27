using IOTLink.Platform.WebSocket;
using IOTLinkAgent.Agent.Notifications;
using IOTLinkAPI.Helpers;
using IOTLinkAPI.Platform.WebSocket;
using IOTLinkAPI.Platform.Windows;
using Newtonsoft.Json;
using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IOTLinkAgent.Agent.WSClient
{
    internal class WebSocketClient
    {
        private static readonly WebSocketClient Instance = new WebSocketClient();
        private readonly object _sendLock = new object();
        private volatile ClientWebSocket _client;

        public static WebSocketClient GetInstance() => Instance;

        private WebSocketClient() { }

        internal async Task RunAsync(Uri uri, Action setupAgent)
        {
            const int maxAttempts = 5;
            for (int attempt = 1; ; attempt++)
            {
                using var client = new ClientWebSocket();
                try
                {
                    await client.ConnectAsync(uri, CancellationToken.None);
                }
                catch (WebSocketException ex) when (attempt < maxAttempts)
                {
                    LoggerHelper.Warn("WebSocket connection attempt {0} failed: {1}. Retrying in 5 seconds.", attempt, ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    continue;
                }

                try
                {
                    SendMessage(client, MessageType.CLIENT_REQUEST, RequestTypeClient.REQUEST_CONNECTED, new { username = Environment.UserName });
                    _client = client;
                    setupAgent();
                    await ReceiveMessagesAsync(client);
                }
                finally
                {
                    lock (_sendLock)
                        _client = null;
                }
                return;
            }
        }

        internal bool IsConnected() => _client?.State == WebSocketState.Open;

        internal void SendResponse(ResponseTypeClient type, object data = null)
        {
            // Add-ons can respond on other threads while the receive loop is answering a ping.
            lock (_sendLock)
            {
                if (IsConnected())
                    SendMessage(_client, MessageType.CLIENT_RESPONSE, type, data);
            }
        }

        private async Task ReceiveMessagesAsync(ClientWebSocket client)
        {
            byte[] buffer = new byte[8192];
            ArrayBufferWriter<byte> fragments = null;
            while (true)
            {
                var result = await client.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.MessageType != WebSocketMessageType.Text)
                    continue;
                if (result.EndOfMessage && fragments == null)
                {
                    OnMessageReceived(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    continue;
                }

                fragments ??= new ArrayBufferWriter<byte>();
                fragments.Write(buffer.AsSpan(0, result.Count));
                if (result.EndOfMessage)
                {
                    OnMessageReceived(Encoding.UTF8.GetString(fragments.WrittenSpan));
                    fragments = null;
                }
            }

            lock (_sendLock)
            {
                client.CloseOutputAsync(client.CloseStatus ?? WebSocketCloseStatus.Empty, client.CloseStatusDescription,
                    CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        private void OnMessageReceived(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return;

            LoggerHelper.Verbose("Message received from server");
            LoggerHelper.DataDump("Message Payload: {0}", data);
            try
            {
                dynamic json = JsonConvert.DeserializeObject<dynamic>(data);
                if (json == null || json.messageType == null || json.content == null)
                {
                    LoggerHelper.Trace("OnMessageReceived - Invalid message content [2].");
                    return;
                }

                MessageType messageType = json.messageType;
                switch (messageType)
                {
                    case MessageType.SERVER_REQUEST:
                        ParseServerRequest(json.content);
                        break;

                    case MessageType.SERVER_RESPONSE:
                        ParseServerResponse(json.content);
                        break;

                    default:
                        LoggerHelper.Warn("WebSocket server send an invalid message type ({0}) with data: {1}", json.messageType, json.data);
                        break;
                }
            }
            catch (Exception ex)
            {
                LoggerHelper.Error("OnMessageReceived - Exception: {0}", ex.ToString());
            }
        }

        private void ParseServerResponse(dynamic content)
        {
            if (content == null || content.type == null || content.data == null)
            {
                LoggerHelper.Trace("ParseServerResponse - Invalid message content.");
                return;
            }

            LoggerHelper.Trace("ParseServerResponse - Content: {0}", content);
        }

        private void ParseServerRequest(dynamic content)
        {
            if (content == null || content.type == null)
            {
                LoggerHelper.Trace("ParseServerRequest - Invalid message.");
                return;
            }

            RequestTypeServer type = content.type;
            if (content.data == null && type != RequestTypeServer.REQUEST_PING)
            {
                LoggerHelper.Trace("ParseServerRequest - Invalid message content.");
                return;
            }

            dynamic data = content.data;
            switch (type)
            {
                case RequestTypeServer.REQUEST_PING:
                    ParsePingRequest();
                    break;

                case RequestTypeServer.REQUEST_SHOW_MESSAGE:
                    ParseShowMessage(data);
                    break;

                case RequestTypeServer.REQUEST_SHOW_NOTIFICATION:
                    ParseShowNotification(data);
                    break;

                case RequestTypeServer.REQUEST_ADDON:
                    ParseAddonRequest(data);
                    break;

                default:
                    LoggerHelper.Warn("ParseServerRequest - Invalid Request Type: {0}", type);
                    break;
            }
        }

        private void ParsePingRequest()
        {
            LoggerHelper.Verbose("ParsePingRequest - Ping request received.");

            SendResponse(ResponseTypeClient.RESPONSE_PING);
        }

        private void ParseShowMessage(dynamic data)
        {
            string title = data.title;
            string message = data.message;

            LoggerHelper.Verbose("ParseShowMessage - Title: {0} Message: {1}", title, message);
            WindowsAPI.ShowMessage(title, message);
        }

        private void ParseShowNotification(dynamic data)
        {
            LoggerHelper.Trace("ParseShowNotification - Data: {0}", data);

            string title = data.title;
            string message = data.message;
            string iconUrl = data.iconUrl;
            string launchParams = data.launchParams;

            if (string.IsNullOrWhiteSpace(message))
                return;

            NotificationManager.GetInstance().ShowNotification(title, message, iconUrl, launchParams);
        }

        private void ParseAddonRequest(dynamic data)
        {
            string addonId = data.addonId;
            dynamic addonData = data.addonData;

            if (string.IsNullOrWhiteSpace(addonId))
                return;

            AgentAddonManager.GetInstance().Raise_OnAgentRequest(addonId, addonData);
        }

        private static void SendMessage(ClientWebSocket client, MessageType messageType, object contentType, object data)
        {
            var message = new { messageType, content = new { type = contentType, data } };
            string payload = JsonConvert.SerializeObject(message, Formatting.None);
            LoggerHelper.Verbose("Sending message to server (Type: {0})", contentType);
            LoggerHelper.DataDump("Message Payload: {0}", payload);
            var bytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload));
            client.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
