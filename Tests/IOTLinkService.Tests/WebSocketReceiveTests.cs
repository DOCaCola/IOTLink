using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IOTLinkService.Service.WebSockets.Server;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IOTLinkService.Tests
{
    [TestClass]
    public class WebSocketReceiveTests
    {
        [TestMethod]
        public async Task FragmentedScreenshotAndFollowingMessageUseLinearReceiveStorage()
        {
            // Split a UTF-8 character between receives and finish on a partial buffer.
            string screenshot = new string('A', 2047) + "📷" + new string('B', 256 * 1024);
            using (var socket = new TextMessageSocket(screenshot, "ping", string.Empty))
            {
                var server = new WebSocketServer();
                var received = new List<string>();
                server.OnMessageHandler += (sender, e) => received.Add(e.Message);
                await server.ReceiveMessagesAsync(socket, "agent");

                CollectionAssert.AreEqual(new[] { screenshot, "ping", string.Empty }, received);
                Assert.AreEqual(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
                Assert.IsTrue(socket.ReceiveBufferBytes < 4L * Encoding.UTF8.GetByteCount(screenshot),
                    "Reassembling a screenshot repeatedly allocated and copied the accumulated payload.");
            }
        }

        private sealed class TextMessageSocket : WebSocket
        {
            private readonly Queue<byte[]> messages = new Queue<byte[]>();
            private int offset;
            private byte[] lastBuffer;
            private WebSocketCloseStatus? closeStatus;

            public TextMessageSocket(params string[] texts)
            {
                foreach (var text in texts)
                    messages.Enqueue(Encoding.UTF8.GetBytes(text));
            }

            public long ReceiveBufferBytes { get; private set; }
            public override WebSocketCloseStatus? CloseStatus => closeStatus;
            public override WebSocketState State => closeStatus.HasValue ? WebSocketState.Closed : WebSocketState.Open;
            public override string CloseStatusDescription => null;
            public override string SubProtocol => null;

            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
            {
                if (lastBuffer != buffer.Array)
                {
                    lastBuffer = buffer.Array;
                    ReceiveBufferBytes += buffer.Array.Length;
                }
                if (messages.Count == 0)
                    return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

                var message = messages.Peek();
                int count = Math.Min(2048, Math.Min(buffer.Count, message.Length - offset));
                Array.Copy(message, offset, buffer.Array, buffer.Offset, count);
                offset += count;
                bool end = offset == message.Length;
                if (end)
                {
                    messages.Dequeue();
                    offset = 0;
                }
                return Task.FromResult(new WebSocketReceiveResult(count, WebSocketMessageType.Text, end));
            }

            public override Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken token)
            {
                closeStatus = status;
                return Task.CompletedTask;
            }

            public override void Dispose() { }
            public override void Abort() => throw new NotSupportedException();
            public override Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken token) => throw new NotSupportedException();
            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token) => throw new NotSupportedException();
        }
    }
}
