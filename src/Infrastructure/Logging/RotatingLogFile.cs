using System.Collections.Concurrent;
using System.Text;

namespace Infrastructure.Logging;

/// <summary>
/// 限大小、按个数轮换的追加文件：写满 <see cref="MaxBytes"/> 就把 name.log 挪成 name.1.log、name.1.log 挪成 name.2.log……最多留 <see cref="Keep"/> 份（含正在写的那份），最老的丢掉。
/// 同一路径进程内只有一份（<see cref="For"/>），管理宿主、中转宿主与命令行写同一个文件也不会各自轮换打架；每次追加单独打开文件，别的进程同时追加也只是交错成行。
/// 写不进去（磁盘满、没有权限、被占用）只计数，不抛出：日志写失败不能让请求或命令失败。
/// </summary>
public sealed class RotatingLogFile
{
    /// <summary>一份日志文件的上限：5 MB。</summary>
    public const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>最多留几份（含正在写的那份）：3 份，一种日志最多约 15 MB。</summary>
    public const int Keep = 3;

    private static readonly ConcurrentDictionary<string, RotatingLogFile> _shared = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly long _maxBytes;
    private readonly int _keep;
    private long _failures;

    /// <summary>按完整路径取进程内共用的那一份；第一次取时按默认上限建。</summary>
    /// <param name="path">日志文件路径。</param>
    public static RotatingLogFile For(string path) => _shared.GetOrAdd(System.IO.Path.GetFullPath(path), full => new RotatingLogFile(full, MaxBytes, Keep));

    /// <summary>建一份独立的（不进共用表）；用例用它把上限调小来验证轮换。</summary>
    /// <param name="path">日志文件路径。</param>
    /// <param name="maxBytes">一份文件的上限（字节），至少 1。</param>
    /// <param name="keep">最多留几份，至少 1。</param>
    internal RotatingLogFile(string path, long maxBytes, int keep)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1); ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        Path = System.IO.Path.GetFullPath(path); _maxBytes = maxBytes; _keep = keep;
    }

    /// <summary>正在写的那份文件的完整路径。</summary>
    public string Path { get; }

    /// <summary>写失败的次数（进程内累计）；写失败本身不抛出。</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>第 <paramref name="index"/> 份旧文件的路径：name.log 的第 1 份是 name.1.log。</summary>
    /// <param name="index">第几份，从 1 起。</param>
    public string Rotated(int index) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, System.IO.Path.GetFileNameWithoutExtension(Path) + "." + index + System.IO.Path.GetExtension(Path));

    /// <summary>追加一行（自动补换行，UTF-8 不带 BOM）；加上这一行会超过上限且文件不空时先轮换。写不进去只计数。</summary>
    /// <param name="line">一行文本，不含换行。</param>
    public void Append(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        // 量长度、轮换、追加三步要在同一把锁里：否则两个线程同时判到超限会轮换两次，把刚挪走的那份又覆盖掉。
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var length = File.Exists(Path) ? new FileInfo(Path).Length : 0;
                if (length > 0 && length + bytes.Length > _maxBytes) Rotate();
                using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
            }
            // 日志写不进去不能让请求、页面或命令失败：只计数，管理员从失败次数和日志缺行里看得出来。
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Interlocked.Increment(ref _failures); }
        }
    }

    /// <summary>轮换：最老的一份被覆盖，其余依次往后挪一位，正在写的那份挪成第 1 份。只在持锁时调用。</summary>
    private void Rotate()
    {
        // 只留一份时直接删掉写满的那份重新开始：上限就是上限，日志里没有秘密，丢最老的行是有意的。
        if (_keep == 1) { File.Delete(Path); return; }
        // 从最老的往前挪，覆盖掉的只会是超出份数的最老那份；挪到一半失败由调用方计数，下次追加再试。
        for (var index = _keep - 1; index > 1; index--)
            if (File.Exists(Rotated(index - 1))) File.Move(Rotated(index - 1), Rotated(index), true);
        // 正在写的那份挪成第 1 份，之后的追加新建文件。
        File.Move(Path, Rotated(1), true);
    }
}
