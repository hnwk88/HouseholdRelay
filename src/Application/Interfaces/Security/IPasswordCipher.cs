namespace Application.Interfaces.Security;

/// <summary>
/// 用管理员当场输入的密码加密、解密一整个文件：不绑本机，换一台电脑输入同一密码就能解开；密码不保存。
/// 与 <see cref="ISecretProtector"/>（DPAPI，只认这台电脑上的这个用户）是两件事，只用于把上游搬到另一套实例。
/// </summary>
public interface IPasswordCipher
{
    /// <summary>用 <paramref name="password"/> 加密 <paramref name="plain"/>，返回带格式版本号的完整文件；调用方负责清零明文。</summary>
    byte[] Seal(byte[] plain, string password);

    /// <summary>
    /// 用 <paramref name="password"/> 解开 <see cref="Seal"/> 产生的文件，返回明文（调用方用完清零）。
    /// 不是这种文件、格式版本不认识、密码不对、文件被改过或不完整时抛出 <see cref="InvalidOperationException"/>，消息读出是哪一种与下一步；不返回半截数据。
    /// </summary>
    byte[] Open(byte[] file, string password);
}
