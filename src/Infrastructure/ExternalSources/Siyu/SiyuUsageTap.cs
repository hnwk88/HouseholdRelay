using System.Text;
using System.Text.Json;
using Core.Usage;

namespace Infrastructure.ExternalSources.Siyu;

/// <summary>
/// 思域回应体的用量旁路：转发路径把写给客户端的同一份线上字节也交给它（<see cref="TeeStream"/>），它只读不改。
/// 边收边解开 chunked 分块；<c>text/event-stream</c> 按空行切事件交给 <see cref="UsageAccumulator.Event"/>，其余按 JSON 有界收集，结束时按 Content-Encoding 解码后交给
/// <see cref="UsageAccumulator.Json"/>。压缩的事件流、超限、解析失败都只把这次用量记成看不懂或不完整，从不影响转发。
/// </summary>
public sealed class SiyuUsageTap
{
    /// <summary>单个事件或整份 JSON 收集的上限。</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    private readonly UsageAccumulator _usage;
    private readonly Http1ResponseHead _head;
    private readonly bool _events;
    private readonly bool _encoded;
    private readonly MemoryStream _collected = new();
    private bool _overflow;
    /// <summary>chunked 解析状态：在读块长行、在读块数据（剩余字节数）、在读块尾 CRLF、在读 trailer、已结束。</summary>
    private enum ChunkState { Size, Data, DataEnd, Trailer, Done }
    private ChunkState _state = ChunkState.Size;
    private long _remaining;
    private int _skip;
    private readonly List<byte> _line = [];

    /// <summary>按回应头决定怎么看：事件流还是 JSON、是否压缩。</summary>
    public SiyuUsageTap(UsageAccumulator usage, Http1ResponseHead head)
    {
        ArgumentNullException.ThrowIfNull(usage); ArgumentNullException.ThrowIfNull(head);
        _usage = usage; _head = head;
        var type = Http1HeadParser.All(head.Fields, "Content-Type").Select(field => field.Value).FirstOrDefault() ?? "";
        _events = type.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase);
        _encoded = Http1HeadParser.All(head.Fields, "Content-Encoding").Any(field => !string.Equals(field.Value.Trim(), "identity", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>收到一段线上字节。</summary>
    public void Observe(ReadOnlySpan<byte> wire)
    {
        if (_head.Framing != Http1BodyFraming.Chunked) { Entity(wire); return; }
        while (!wire.IsEmpty && _state != ChunkState.Done)
        {
            switch (_state)
            {
                case ChunkState.Size or ChunkState.Trailer:
                {
                    var end = wire.IndexOf((byte)'\n');
                    var take = end < 0 ? wire.Length : end + 1;
                    if (_line.Count + take <= Http1Reader.MaxChunkLineBytes + Http1Reader.MaxTrailerBytes) _line.AddRange(wire[..take]);
                    wire = wire[take..];
                    if (end < 0) break;
                    if (_state == ChunkState.Trailer) { if (_line.Count <= 2) _state = ChunkState.Done; _line.Clear(); break; }
                    long size;
                    try { size = Http1Reader.ChunkSize(_line.ToArray()); }
                    // 转发路径已经按同一规则核对过块长；这里再不合就停止观察，用量记成不完整。
                    catch (Http1Rejection) { _state = ChunkState.Done; _overflow = true; break; }
                    _line.Clear();
                    if (size == 0) _state = ChunkState.Trailer;
                    else { _remaining = size; _state = ChunkState.Data; }
                    break;
                }
                case ChunkState.Data:
                {
                    var take = (int)Math.Min(_remaining, wire.Length);
                    Entity(wire[..take]);
                    wire = wire[take..];
                    if ((_remaining -= take) == 0) { _state = ChunkState.DataEnd; _skip = 2; }
                    break;
                }
                case ChunkState.DataEnd:
                {
                    var take = Math.Min(_skip, wire.Length);
                    wire = wire[take..];
                    if ((_skip -= take) == 0) _state = ChunkState.Size;
                    break;
                }
            }
        }
    }

    /// <summary>回应体结束（<paramref name="complete"/> 为假表示中途断开）：把剩下的交给累加器。</summary>
    public void Complete(bool complete)
    {
        if (_events)
        {
            if (_encoded)
            {
                // 压缩的事件流：收全了才整份解码，再按事件切开（用量只要结果，不必边收边解）；解不开（如 zstd、超限）记看不懂。
                var decoded = complete && !_overflow ? Http1Entity.Decode(_collected.ToArray(), Http1BodyFraming.Length, _head.Fields) : null;
                _collected.SetLength(0);
                if (decoded is null) { _usage.Event("", "", false); _usage.Unreadable(); return; }
                Events(decoded);
            }
            if (_collected.Length > 0) Flush(false);
            return;
        }
        if (!complete || _overflow) return;
        var bytes = _collected.ToArray();
        if (_encoded)
            bytes = Http1Entity.Decode(bytes, Http1BodyFraming.Length, _head.Fields) ?? [];
        if (bytes.Length == 0) { if (_head.Status is < 200 or >= 300) _usage.Unreadable(); return; }
        try { _usage.Json(bytes); }
        // 不是合法 JSON：只记看不懂，转发不受影响。
        catch (JsonException) { _usage.Unreadable(); }
    }

    /// <summary>收到一段实体字节：事件流按空行切事件，JSON 有界收集。</summary>
    private void Entity(ReadOnlySpan<byte> bytes)
    {
        if (_overflow) return;
        if (!_events || _encoded)
        {
            if (_collected.Length + bytes.Length > MaxBytes) { _overflow = true; _collected.SetLength(0); return; }
            _collected.Write(bytes);
            return;
        }
        Events(bytes);
    }

    /// <summary>按空行切事件交给累加器（未压缩的事件流边收边切；压缩的在结束时解码后整份切）。</summary>
    private void Events(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (_collected.Length >= MaxBytes) { _overflow = true; _collected.SetLength(0); return; }
            _collected.WriteByte(value);
            if (value != (byte)'\n') continue;
            var buffer = _collected.GetBuffer(); var length = (int)_collected.Length;
            // 一个事件以空行结束：末尾是 "\n\n" 或 "\r\n\r\n"（中间的 \r 可有可无）。
            if (length >= 2 && (buffer[length - 2] == '\n' || length >= 3 && buffer[length - 2] == '\r' && buffer[length - 3] == '\n')) Flush(true);
        }
    }

    /// <summary>把收集到的一个事件交给累加器（与旧转发路径的事件解析同一规则：event 取第一行，data 多行用换行连起来）。</summary>
    private void Flush(bool terminated)
    {
        var lines = Encoding.UTF8.GetString(_collected.GetBuffer(), 0, (int)_collected.Length).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        _collected.SetLength(0);
        var data = string.Join('\n', lines.Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].TrimStart(' ')));
        var name = lines.FirstOrDefault(line => line.StartsWith("event:", StringComparison.Ordinal))?[6..].Trim() ?? "";
        if (data.Length == 0 && name.Length == 0) return;
        _usage.Event(name, data, terminated);
    }
}
