using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Infrastructure.Security;

/// <summary>直接调用 Windows DPAPI（当前用户范围）加解密本机机密，并为状态目录设置只允许当前用户和 SYSTEM 访问的权限；不依赖外部包，也不向证书存储安装任何东西。密文只能由同一 Windows 用户在本机解开。</summary>
public static class WindowsSecrets
{
    /// <summary>对应 Win32 的 DATA_BLOB 结构：字节长度加非托管内存指针，用于与 crypt32 交换数据。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }
    /// <summary>crypt32 的 CryptProtectData：用当前用户密钥加密输入；输出内存由系统分配，须用 <see cref="LocalFree"/> 释放。</summary>
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    /// <summary>crypt32 的 CryptUnprotectData：解密由同一用户加密的数据；输出内存由系统分配，须用 <see cref="LocalFree"/> 释放。</summary>
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    /// <summary>kernel32 的 LocalFree：释放 DPAPI 输出缓冲区。</summary>
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>用当前 Windows 用户的 DPAPI 密钥加密字节，禁止弹出任何界面；返回的密文可存库或落盘。</summary>
    public static byte[] Protect(byte[] value) => Transform(value, true);
    /// <summary>解密由当前 Windows 用户在本机加密的 DPAPI 密文；换用户、换机器或数据损坏时抛 <see cref="Win32Exception"/>。返回的明文由调用方负责用完清零。</summary>
    public static byte[] Unprotect(byte[] value) => Transform(value, false);
    /// <summary>加解密的共同实现：把输入复制到非托管内存后调用 DPAPI（标志 1 即禁止界面），失败时抛出带系统错误码的 <see cref="Win32Exception"/>；结束时清零并释放输入与输出两块非托管内存。</summary>
    private static byte[] Transform(byte[] value, bool protect)
    {
        var input = new Blob { Length = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            var success = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        // 无论成功与否都先逐字节清零再释放非托管内存，明文和密文都不会残留在进程堆里。
        finally
        {
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }

    /// <summary>创建（或沿用）目录并替换其访问控制：断开继承、丢弃继承来的规则，只给当前用户和 LocalSystem 完全控制，并向子目录和文件继承，使数据库与备份不被本机其他账户读取。</summary>
    public static void ProtectDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path);
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("无法确定 Windows 用户。");
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    /// <summary>用 DPAPI 加密后写入文件：先写同名 .tmp 再覆盖移动到目标，避免中途断电留下写了一半的密文文件。</summary>
    public static void Save(string path, byte[] value)
    {
        // 先写临时文件再整体覆盖目标：目标文件要么保持旧内容，要么换成完整的新密文；遗留的同名 .tmp 会被直接覆盖。
        var protectedValue = Protect(value); File.WriteAllBytes(path + ".tmp", protectedValue); File.Move(path + ".tmp", path, true);
    }
    /// <summary>读取由 <see cref="Save"/> 写入的文件并用 DPAPI 解密，返回明文由调用方负责清零。</summary>
    public static byte[] Load(string path) => Unprotect(File.ReadAllBytes(path));
}
