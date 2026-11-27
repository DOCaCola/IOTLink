using IOTLinkAPI.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static IOTLinkService.Service.WebSockets.Server.WebSocketHandlers;

namespace IOTLinkService.Service.WebSockets.Server
{
    internal class WebSocketServer
    {
        private const int BUFFER_LENGTH = 2048;
        private int _count = 0;
        private HttpListener _listener;
        private bool _disconnect;

        private readonly object clientsLock = new object();

        public event WebSocketMessageEventHandler OnMessageHandler;

        private Dictionary<WebSocket, string> _clients = new Dictionary<WebSocket, string>();

        public async void Start(string listenerPrefix)
        {
            // Try to create the HttpListener
            while (_listener == null || !_listener.IsListening)
            {
                try
                {
                    if (_listener != null)
                    {
                        if (_listener.IsListening)
                        {
                            _listener.Stop();
                            Thread.Sleep(2500);
                        }

                        _listener = null;
                    }

                    _disconnect = false;
                    _listener = new HttpListener();
                    _listener.Prefixes.Add(ParseURI(listenerPrefix));
                    _listener.Start();
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error("Error while trying to create a new WebSocketServer: {0}", ex.ToString());
                }
            }

            // Listen for incoming websocket connections
            while (!_disconnect && _listener != null && _listener.IsListening)
            {
                try
                {
                    HttpListenerContext listenerContext = await _listener.GetContextAsync();
                    if (listenerContext.Request.IsWebSocketRequest && !_disconnect)
                    {
                        ProcessRequest(listenerContext);
                    }
                    else
                    {
                        listenerContext.Response.StatusCode = 400;
                        listenerContext.Response.Close();
                    }
                }
                catch (Exception)
                {

                }
            }
        }

        public void Disconnect()
        {
            _disconnect = true;

            while (_listener != null && _listener.IsListening)
            {
                try
                {
                    _listener.Close();
                    Thread.Sleep(1000);

                    if (!_listener.IsListening)
                        _listener = null;
                }
                catch (Exception)
                {

                }
            }
        }

        internal void DisconnectClient(string clientId)
        {
            IEnumerable<KeyValuePair<WebSocket, string>> entries;

            lock (clientsLock)
            {
                entries = _clients.Where(x => x.Value.CompareTo(clientId) == 0);

                foreach (var entry in entries)
                {
                    WebSocket client = entry.Key;

                    try
                    {
                        client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        LoggerHelper.Error("Error while trying to disconnect client: {0}", ex);
                    }
                    finally
                    {
                        client.Dispose();
                    }
                }

            }

            try
            {
                lock (clientsLock)
                {
                    var newClients = _clients.Except(entries).ToDictionary(x => x.Key, x => x.Value);
                    _clients = newClients;
                }
            }
            catch (Exception ex)
            {
                LoggerHelper.Error("Error while trying to disconnect client: {0}", ex);
            }
        }

        internal void Broadcast(string payload)
        {
            lock (clientsLock)
            {
                foreach (WebSocket client in _clients.Keys)
                {
                    SendMessage(client, payload);
                }
            }
        }

        internal void SendMessage(string clientId, string payload)
        {
            lock (clientsLock)
            {
                var entries = _clients.Where(x => x.Value.CompareTo(clientId) == 0);
                foreach (var entry in entries)
                {
                    WebSocket client = entry.Key;
                    SendMessage(client, payload);
                }
            }
        }

        private string ParseURI(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                return null;

            if (uri.StartsWith("wss://"))
                uri = string.Format("https://{0}", uri.Remove(0, 6));

            if (uri.StartsWith("ws://"))
                uri = string.Format("http://{0}", uri.Remove(0, 5));

            if (!uri.EndsWith("/"))
                uri = uri + "/";

            return uri;
        }

        private async void ProcessRequest(HttpListenerContext listenerContext)
        {

            WebSocketContext webSocketContext;
            try
            {
                webSocketContext = await listenerContext.AcceptWebSocketAsync(subProtocol: null);
                Interlocked.Increment(ref _count);
            }
            catch (Exception)
            {
                listenerContext.Response.StatusCode = 500;
                listenerContext.Response.Close();
                return;
            }

            WebSocket webSocket = webSocketContext.WebSocket;
            try
            {
                string currentId = string.Empty;

                if (!_clients.ContainsKey(webSocket))
                {
                    currentId = Guid.NewGuid().ToString("N");
                    _clients.Add(webSocket, currentId);
                }
                else
                {
                    currentId = _clients[webSocket];
                }

                await ReceiveMessagesAsync(webSocket, currentId);
            }
            catch (WebSocketException ex)
            {
                if (ex.InnerException == null || ex.InnerException.GetType() != typeof(HttpListenerException))
                    LoggerHelper.Error("Exception while running WebSocketServer: {0}", ex.ToString());
            }
            catch (Exception ex)
            {
                LoggerHelper.Error("Exception while running WebSocketServer: {0}", ex.ToString());
            }
            finally
            {
                // Clean up by disposing the WebSocket once it is closed/aborted.
                if (webSocket != null)
                {
                    if (_clients.ContainsKey(webSocket))
                        _clients.Remove(webSocket);

                    webSocket.Dispose();
                }
            }
        }

        internal async Task ReceiveMessagesAsync(WebSocket webSocket, string clientId)
        {
            var buffer = new ArraySegment<byte>(new byte[BUFFER_LENGTH]);
            while (!_disconnect && webSocket.State == WebSocketState.Open)
            {
                string text;
                using (var message = new MemoryStream())
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await webSocket.ReceiveAsync(buffer, CancellationToken.None);
                        if (result.MessageType != WebSocketMessageType.Text)
                        {
                            bool binary = result.MessageType == WebSocketMessageType.Binary;
                            var status = binary ? WebSocketCloseStatus.InvalidMessageType : WebSocketCloseStatus.NormalClosure;
                            await webSocket.CloseAsync(status, binary ? "This server only accepts text frames" : string.Empty, CancellationToken.None);
                            return;
                        }

                        if (!result.EndOfMessage)
                        {
                            message.Write(buffer.Array, 0, result.Count);
                            if (_disconnect || webSocket.State != WebSocketState.Open)
                                return;
                        }
                    } while (!result.EndOfMessage);

                    if (message.Length == 0)
                        text = Encoding.UTF8.GetString(buffer.Array, 0, result.Count);
                    else
                    {
                        message.Write(buffer.Array, 0, result.Count);
                        text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    }
                }

                OnMessageHandler?.Invoke(this, new WebSocketMessageEventArgs { ID = clientId, Message = text.Replace("\0", string.Empty) });
            }
        }

        private async void SendMessage(WebSocket webSocket, string payload)
        {
            if (webSocket == null || string.IsNullOrWhiteSpace(payload))
                return;

            bool shouldRemoveClient = false;
            try
            {
                var encoded = System.Text.Encoding.UTF8.GetBytes(payload);
                var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
                await webSocket.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LoggerHelper.Error("Error while sending message to client: {0}", ex.ToString());
                shouldRemoveClient = true;
            }

            if (shouldRemoveClient)
            {
                try
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
                finally
                {
                    _clients.Remove(webSocket);
                    webSocket.Dispose();
                }
            }
        }
    }
}
