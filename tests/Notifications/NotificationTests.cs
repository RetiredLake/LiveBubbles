using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiveBubbles.Notifications;

internal static class NotificationTests
{
    private static int passed;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    private static bool Observe(NotificationCursor c, string id, long time, bool outgoing = false, bool read = false, bool muted = false, bool active = false)
    { return NotificationPolicy.Observe(c, id, time, 100, outgoing, read, muted, active); }
    private static async Task Reject(byte[] bytes, string name)
    {
        try { await new WebSocketWire(new MemoryStream(bytes), new MemoryStream()).ReadTextAsync(CancellationToken.None); }
        catch (IOException) { Check(true, name); return; }
        throw new Exception("Accepted invalid frame: " + name);
    }
    private static byte[] Frame(byte opcode, string text, bool final = true)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        return new[] { (byte)((final ? 128 : 0) | opcode), (byte)body.Length }.Concat(body).ToArray();
    }
    private static async Task Run()
    {
        var cursor = new NotificationCursor();
        Check(!Observe(cursor, "historical", 50), "first sync suppresses historical messages");
        Check(Observe(cursor, "incoming", 101), "new incoming message alerts");
        Check(!Observe(cursor, "incoming", 101), "socket plus recovery snapshot deduplicates");
        Check(!Observe(cursor, "outgoing", 102, outgoing: true), "outgoing does not alert");
        Check(!Observe(cursor, "server-read", 103, read: true), "already read does not alert");
        Check(!Observe(cursor, "muted", 104, muted: true) && cursor.Unread, "muting suppresses toast while retaining unread");
        Check(!Observe(cursor, "muted", 104), "unmuting does not replay a muted message");
        Check(!Observe(cursor, "active", 105, active: true) && !cursor.Unread, "visible chat marks incoming read without toast");
        Check(!Observe(cursor, "late", 104), "read watermark suppresses stale arrivals");
        Check(Observe(cursor, "same-time-a", 106) && Observe(cursor, "same-time-b", 106), "distinct messages sharing a timestamp both alert");
        Check(Observe(cursor, "later", 110) && Observe(cursor, "out-of-order", 109), "unread out-of-order messages are not discarded");
        Check(!Observe(cursor, "", 111) && !Observe(cursor, "invalid-time", 0), "incomplete metadata is suppressed");
        var bounded = new NotificationCursor();
        for (int i = 0; i < 300; i++) Observe(bounded, "id-" + i, 1000 + i);
        Check(bounded.Seen.Count == 256 && bounded.Order.Count == 256, "dedup state is bounded");
        Check(!Observe(bounded, "id-0", 1000), "evicted IDs remain behind replay floor");
        var input = new FragmentedStream(Frame(1, "42[\"new-message\",{}]"));
        Check(await new WebSocketWire(input, new MemoryStream()).ReadTextAsync(CancellationToken.None) == "42[\"new-message\",{}]", "split TCP reads preserve message frame");
        var mixed = Frame(1, "hel", false).Concat(Frame(9, "p")).Concat(Frame(0, "lo")).ToArray();
        var output = new MemoryStream();
        Check(await new WebSocketWire(new FragmentedStream(mixed), output).ReadTextAsync(CancellationToken.None) == "hello", "fragmentation with interleaved ping");
        Check((output.ToArray()[0] & 15) == 10 && (output.ToArray()[1] & 128) != 0, "pong is masked");
        Check(await new WebSocketWire(new MemoryStream(Frame(9, "p")), new MemoryStream()).ReadTextAsync(CancellationToken.None) == null, "standalone ping yields immediately for broker handoff");
        Check(await new WebSocketWire(new MemoryStream(Frame(10, "p")), new MemoryStream()).ReadTextAsync(CancellationToken.None) == null, "standalone pong yields immediately");
        output = new MemoryStream(); await new WebSocketWire(Stream.Null, output).SendTextAsync("40", CancellationToken.None);
        byte[] sent = output.ToArray();
        Check(sent[0] == 129 && sent[1] == 130 && (sent[6] ^ sent[2]) == '4' && (sent[7] ^ sent[3]) == '0', "client sends masked Socket.IO authentication frame");
        await Reject(new byte[] { 129, 128 }, "reject masked server frame");
        await Reject(new byte[] { 193, 0 }, "reject unnegotiated compression");
        await Reject(Frame(0, "orphan"), "reject orphan continuation");
        await Reject(new byte[] { 137, 126, 0, 126 }, "reject oversized control frame");
        await Reject(new byte[] { 129, 127, 0, 0, 0, 0, 0, 32, 0, 0 }, "reject oversized notification payload");
        await Reject(new byte[] { 129, 3, 65 }, "detect truncated payload");
        await Reject(new byte[] { 130, 0 }, "reject binary notification frame");
        await Reject(new byte[] { 129, 126, 0, 1 }, "reject noncanonical length");
        var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: keep-alive, Upgrade\r\nSec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n";
        WebSocketWire.ValidateUpgrade(response, "dGhlIHNhbXBsZSBub25jZQ=="); Check(true, "RFC 6455 handshake example");
        try { WebSocketWire.ValidateUpgrade(response, "wrong-key"); throw new Exception("wrong handshake accepted"); } catch (IOException) { Check(true, "reject incorrect handshake accept"); }
        try { WebSocketWire.ValidateUpgrade(response.Replace("101 Switching Protocols", "302 Found"), "dGhlIHNhbXBsZSBub25jZQ=="); throw new Exception("redirect accepted"); } catch (IOException) { Check(true, "reject redirect instead of leaking saved credentials"); }
        Console.WriteLine(passed + " notification checks passed.");
    }
    private sealed class FragmentedStream : MemoryStream
    {
        internal FragmentedStream(byte[] value) : base(value) { }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) { return base.ReadAsync(buffer, offset, Math.Min(count, 1), token); }
    }
    private static int Main() { try { Run().GetAwaiter().GetResult(); return 0; } catch (Exception e) { Console.Error.WriteLine(e); return 1; } }
}
