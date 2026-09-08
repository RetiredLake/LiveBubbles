using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiveBubbles.Notifications
{
    // RFC 6455 client framing. Only notification text is consumed; no media flows here.
    internal sealed class WebSocketWire
    {
        internal const int MaximumPayload = 1024 * 1024;
        private readonly Stream input;
        private readonly Stream output;
        internal WebSocketWire(Stream input, Stream output) { this.input = input; this.output = output; }

        internal async Task<string> ReadTextAsync(CancellationToken token)
        {
            using (var message = new MemoryStream())
            {
                bool fragmented = false;
                while (true)
                {
                    var header = await ExactAsync(2, token);
                    bool final = (header[0] & 128) != 0;
                    int opcode = header[0] & 15;
                    if ((header[0] & 112) != 0 || (header[1] & 128) != 0) throw new IOException("Invalid WebSocket frame.");
                    ulong size = (ulong)(header[1] & 127);
                    if (size == 126) { var b = await ExactAsync(2, token); size = (ulong)(b[0] * 256 + b[1]); if (size < 126) throw new IOException("Invalid length."); }
                    else if (size == 127)
                    {
                        var b = await ExactAsync(8, token); size = 0;
                        if ((b[0] & 128) != 0) throw new IOException("Invalid length.");
                        foreach (byte value in b) size = (size << 8) | value;
                        if (size < 65536) throw new IOException("Invalid length.");
                    }
                    if (size > MaximumPayload || (opcode < 8 && size + (ulong)message.Length > MaximumPayload)) throw new IOException("Notification frame is too large.");
                    if (opcode >= 8 && (!final || size > 125)) throw new IOException("Invalid control frame.");
                    var payload = await ExactAsync((int)size, token);
                    if (opcode == 8) throw new IOException("Notification connection closed.");
                    if (opcode == 9) { await WriteAsync(10, payload, token); if (!fragmented) return null; continue; }
                    if (opcode == 10) { if (!fragmented) return null; continue; }
                    if (opcode == 1 && !fragmented) fragmented = true;
                    else if (opcode != 0 || !fragmented) throw new IOException("Unexpected notification frame.");
                    message.Write(payload, 0, payload.Length);
                    if (final) return new UTF8Encoding(false, true).GetString(message.ToArray(), 0, (int)message.Length);
                }
            }
        }

        internal Task SendTextAsync(string value, CancellationToken token) { return WriteAsync(1, Encoding.UTF8.GetBytes(value), token); }
        internal Task SendPingAsync(CancellationToken token) { return WriteAsync(9, new byte[0], token); }

        private async Task WriteAsync(byte opcode, byte[] payload, CancellationToken token)
        {
            if (payload.Length > MaximumPayload) throw new IOException("Notification frame is too large.");
            using (var frame = new MemoryStream())
            {
                frame.WriteByte((byte)(128 | opcode));
                if (payload.Length < 126) frame.WriteByte((byte)(128 | payload.Length));
                else if (payload.Length <= 65535)
                {
                    frame.WriteByte(254); frame.WriteByte((byte)(payload.Length >> 8)); frame.WriteByte((byte)payload.Length);
                }
                else
                {
                    frame.WriteByte(255);
                    for (int shift = 56; shift >= 0; shift -= 8) frame.WriteByte((byte)((ulong)payload.Length >> shift));
                }
                var mask = new byte[4]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(mask);
                frame.Write(mask, 0, mask.Length);
                for (int i = 0; i < payload.Length; i++) frame.WriteByte((byte)(payload[i] ^ mask[i % 4]));
                var bytes = frame.ToArray();
                await output.WriteAsync(bytes, 0, bytes.Length, token); await output.FlushAsync(token);
            }
        }

        private async Task<byte[]> ExactAsync(int count, CancellationToken token)
        {
            var bytes = new byte[count]; int offset = 0;
            while (offset < count)
            {
                int read = await input.ReadAsync(bytes, offset, count - offset, token);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            return bytes;
        }

        internal static string ExpectedAccept(string key)
        {
            using (var sha = SHA1.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        }

        internal static void ValidateUpgrade(string response, string key)
        {
            if (!response.EndsWith("\r\n\r\n", StringComparison.Ordinal)) throw new IOException("Incomplete upgrade headers.");
            var lines = response.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.1 101 ", StringComparison.Ordinal)) throw new IOException("Server did not accept the notification connection.");
            var fields = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':'); if (colon <= 0) continue;
                string name = lines[i].Substring(0, colon).Trim();
                if (fields.ContainsKey(name)) throw new IOException("Duplicate upgrade header.");
                fields[name] = lines[i].Substring(colon + 1).Trim();
            }
            string upgrade, connection, accept;
            if (!fields.TryGetValue("Upgrade", out upgrade) || !string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase)
                || !fields.TryGetValue("Connection", out connection) || !Array.Exists(connection.Split(','), v => string.Equals(v.Trim(), "Upgrade", StringComparison.OrdinalIgnoreCase))
                || !fields.TryGetValue("Sec-WebSocket-Accept", out accept) || accept != ExpectedAccept(key)) throw new IOException("Invalid notification handshake.");
        }
    }
}
