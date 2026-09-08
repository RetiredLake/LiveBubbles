using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace LiveBubbles.Notifications
{
    internal sealed class BrokerConnection : IDisposable
    {
        internal StreamSocket Socket;
        internal WebSocketWire Wire;
        private SocketStream stream;
        internal BrokerConnection(StreamSocket socket)
        {
            Socket = socket; stream = new SocketStream(socket); Wire = new WebSocketWire(stream, stream);
        }

        internal static async Task<BrokerConnection> ConnectAsync(Uri uri, Guid taskId, CancellationToken token)
        {
            var socket = new StreamSocket();
            try
            {
                socket.Control.KeepAlive = true;
                socket.EnableTransferOwnership(taskId, (new Windows.ApplicationModel.Background.SocketActivityTrigger()).IsWakeFromLowPowerSupported ? SocketActivityConnectedStandbyAction.Wake : SocketActivityConnectedStandbyAction.DoNotWake);
                await socket.ConnectAsync(new HostName(uri.DnsSafeHost), uri.Port.ToString(),
                    uri.Scheme == "https" ? SocketProtectionLevel.Tls12 : SocketProtectionLevel.PlainSocket).AsTask(token);
                var connection = new BrokerConnection(socket);
                var keyBytes = new byte[16]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(keyBytes);
                string key = Convert.ToBase64String(keyBytes);
                string host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.DnsSafeHost + "]" : uri.DnsSafeHost;
                if (!uri.IsDefaultPort) host += ":" + uri.Port;
                string request = "GET " + uri.PathAndQuery + " HTTP/1.1\r\nHost: " + host + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: " + key + "\r\nSec-WebSocket-Version: 13\r\n\r\n";
                byte[] bytes = Encoding.UTF8.GetBytes(request);
                await connection.stream.WriteAsync(bytes, 0, bytes.Length, token);
                var header = new StringBuilder(); var one = new byte[1];
                while (header.Length < 16384)
                {
                    if (await connection.stream.ReadAsync(one, 0, 1, token) == 0) throw new EndOfStreamException();
                    header.Append((char)one[0]);
                    if (header.Length >= 4 && header.ToString(header.Length - 4, 4) == "\r\n\r\n") break;
                }
                WebSocketWire.ValidateUpgrade(header.ToString(), key);
                string hello;
                do { hello = await connection.Wire.ReadTextAsync(token); } while (hello == null);
                if (!hello.StartsWith("0", StringComparison.Ordinal)) throw new IOException("Missing Engine.IO handshake.");
                await connection.Wire.SendTextAsync("40", token);
                while (true)
                {
                    string packet = await connection.Wire.ReadTextAsync(token);
                    if (packet == null) continue;
                    if (packet.StartsWith("40", StringComparison.Ordinal)) return connection;
                    if (packet.StartsWith("2", StringComparison.Ordinal)) await connection.Wire.SendTextAsync("3" + packet.Substring(1), token);
                    else throw new IOException("Notification authentication was not accepted.");
                }
            }
            catch { socket.Dispose(); throw; }
        }

        internal async Task TransferAsync(string id)
        {
            await Socket.CancelIOAsync();
            // Return ownership without installing a one-minute broker timer. The
            // server's Engine.IO heartbeat and SocketActivityTrigger wakeups keep
            // this connection alive; an ownership timeout would create a false
            // disconnect every minute.
            Socket.TransferOwnership(id);
            Socket = null; // Ownership belongs to Windows; never dispose the transferred socket.
        }
        public void Dispose() { if (Socket != null) { Socket.Dispose(); Socket = null; } }

        // No read-ahead: all bytes stay in the brokered socket between task invocations.
        private sealed class SocketStream : Stream
        {
            private readonly StreamSocket socket;
            internal SocketStream(StreamSocket socket) { this.socket = socket; }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (count == 0) return 0;
                var data = await socket.InputStream.ReadAsync(buffer.AsBuffer(offset, count), (uint)count, InputStreamOptions.Partial).AsTask(token);
                if (data.Length > 0) data.CopyTo(0, buffer, offset, (int)data.Length);
                return (int)data.Length;
            }
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                int sent = 0;
                while (sent < count)
                {
                    uint written = await socket.OutputStream.WriteAsync(buffer.AsBuffer(offset + sent, count - sent)).AsTask(token);
                    if (written == 0) throw new EndOfStreamException();
                    sent += (int)written;
                }
            }
            public override Task FlushAsync(CancellationToken token) { return Task.CompletedTask; }
            public override bool CanRead { get { return true; } }
            public override bool CanWrite { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override int Read(byte[] b, int o, int c) { throw new NotSupportedException(); }
            public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
            public override long Seek(long o, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
        }
    }
}
