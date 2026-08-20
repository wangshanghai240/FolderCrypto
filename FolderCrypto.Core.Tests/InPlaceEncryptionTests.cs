using System.Security.Cryptography;
using System.Text;
using FolderCrypto.Core.Encryption;
using FolderCrypto.Core.Security;
using FolderCrypto.Core.Services;
using Xunit;

namespace FolderCrypto.Core.Tests;

public class InPlaceEncryptionTests : IDisposable
{
    private readonly string _dir;

    public InPlaceEncryptionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "FCInPlace_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            // 先解除可能残留的文件夹浏览封锁，否则无法递归删除
            if (Directory.Exists(_dir))
            {
                foreach (var d in Directory.GetDirectories(_dir, "*", SearchOption.AllDirectories))
                    InPlaceEncryptionService.UnlockFolderAcl(d);
                InPlaceEncryptionService.UnlockFolderAcl(_dir);
                Directory.Delete(_dir, true);
            }
        }
        catch { /* 清理忽略 */ }
    }

    private const string ValidPassword = "Abcd1234!";

    [Fact]
    public void File_EncryptRoundTrip_RestoresOriginal()
    {
        string file = Path.Combine(_dir, "secret.txt");
        string content = "机密内容 top-secret 123";
        File.WriteAllText(file, content);

        InPlaceEncryptionService.EncryptFile(file, ValidPassword);
        Assert.True(InPlaceEncryptionService.IsFileEncrypted(file));
        Assert.True(InPlaceEncryptionService.VerifyFilePassword(file, ValidPassword));
        // 密文不应含明文
        Assert.DoesNotContain(content, File.ReadAllText(file));

        InPlaceEncryptionService.DecryptFile(file, ValidPassword);
        Assert.False(InPlaceEncryptionService.IsFileEncrypted(file));
        Assert.Equal(content, File.ReadAllText(file));
    }

    [Fact]
    public void File_WrongPassword_Rejected()
    {
        string file = Path.Combine(_dir, "s.txt");
        File.WriteAllText(file, "data");
        InPlaceEncryptionService.EncryptFile(file, ValidPassword);

        Assert.False(InPlaceEncryptionService.VerifyFilePassword(file, "Wrong123!"));
        Assert.Throws<CryptographicException>(
            () => InPlaceEncryptionService.DecryptFile(file, "Wrong123!"));
    }

    [Fact]
    public void File_AlreadyEncrypted_Throws()
    {
        string file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "x");
        InPlaceEncryptionService.EncryptFile(file, ValidPassword);
        Assert.Throws<InvalidOperationException>(
            () => InPlaceEncryptionService.EncryptFile(file, ValidPassword));
    }

    [Fact]
    public void File_StreamingRoundTrip_LargeMultiChunk_Restores()
    {
        // 大于单个分块（4MB）的文件：验证分块流式加解密往返正确
        string file = Path.Combine(_dir, "big.bin");
        byte[] data = new byte[9 * 1024 * 1024]; // 9MB → 3 个分块
        new Random(12345).NextBytes(data);
        File.WriteAllBytes(file, data);

        string rec = InPlaceEncryptionService.EncryptFile(file, ValidPassword);
        Assert.True(InPlaceEncryptionService.IsFileEncrypted(file));
        Assert.True(InPlaceEncryptionService.VerifyFilePassword(file, ValidPassword));

        InPlaceEncryptionService.DecryptFile(file, ValidPassword);
        Assert.False(InPlaceEncryptionService.IsFileEncrypted(file));
        Assert.Equal(data, File.ReadAllBytes(file));
    }

    [Fact]
    public void File_LegacyVer2_StillDecrypts()
    {
        // 旧版 ver=2 文件（整份密文为单条 AES-GCM）在新版本下仍可正常解密
        string file = Path.Combine(_dir, "legacy.bin");
        string plaintext = "legacy-ver2-data-机密";
        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        string rec = InPlaceEncryptionService.NormalizeRecoveryCode("TEST-RECOVERY-CODE-1234");

        File.WriteAllBytes(file, BuildLegacyVer2Payload(plain, ValidPassword, rec));
        Assert.True(InPlaceEncryptionService.VerifyFilePassword(file, ValidPassword));
        Assert.True(InPlaceEncryptionService.VerifyFilePassword(file, rec, isRecovery: true));

        InPlaceEncryptionService.DecryptFile(file, ValidPassword);
        Assert.False(InPlaceEncryptionService.IsFileEncrypted(file));
        Assert.Equal(plaintext, File.ReadAllText(file));
    }

    /// <summary>按旧版 ver=2 格式构造加密载荷（单条 AES-GCM 整文件），供兼容性测试使用。</summary>
    private static byte[] BuildLegacyVer2Payload(byte[] content, string password, string recoveryCode)
    {
        byte[] dataKey = RandomNumberGenerator.GetBytes(32);
        byte[] pwdSalt = RandomNumberGenerator.GetBytes(16);
        byte[] pwdKey = PasswordHasher.DeriveKey(password, pwdSalt);
        byte[] recSalt = RandomNumberGenerator.GetBytes(16);
        byte[] recKey = PasswordHasher.DeriveKey(InPlaceEncryptionService.NormalizeRecoveryCode(recoveryCode), recSalt);

        byte[] wrapPwd = AesGcmEncryption.Encrypt(Hkdf(pwdKey, "FolderCrypto:WrapPwd"), dataKey);
        byte[] wrapRec = AesGcmEncryption.Encrypt(Hkdf(recKey, "FolderCrypto:WrapRec"), dataKey);
        byte[] cipher = AesGcmEncryption.Encrypt(dataKey, content);

        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("FCENC000"));
        ms.WriteByte(2); // ver=2
        ms.Write(pwdSalt); ms.Write(SHA256.HashData(pwdKey));
        ms.Write(recSalt); ms.Write(SHA256.HashData(recKey));
        ms.Write(BitConverter.GetBytes(wrapPwd.Length));
        ms.Write(wrapPwd); ms.Write(wrapRec);
        ms.Write(cipher);
        return ms.ToArray();
    }

    private static byte[] Hkdf(byte[] ikm, string info)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt: null, Encoding.UTF8.GetBytes(info));

    [Fact]
    public void Folder_EncryptLockAndDecrypt_Restores()
    {
        string dir = Path.Combine(_dir, "myfolder");
        Directory.CreateDirectory(dir);
        // 新行为：加密时递归加密内部文件（内容为密文），解密后还原
        string inner = Path.Combine(dir, "inner.txt");
        File.WriteAllText(inner, "keep me");

        InPlaceEncryptionService.EncryptFolder(dir, ValidPassword);
        Assert.True(InPlaceEncryptionService.IsFolderEncrypted(dir));
        Assert.True(InPlaceEncryptionService.VerifyFolderPassword(dir, ValidPassword));
        // 文件夹本身不应被隐藏（否则会在资源管理器中“消失”）
        Assert.False((File.GetAttributes(dir) & FileAttributes.Hidden) != 0);

        // 新行为：锁定后无法浏览/枚举文件夹内容（双击进入会被拒绝）
        Assert.ThrowsAny<Exception>(() => Directory.GetFiles(dir));
        Assert.ThrowsAny<Exception>(() => Directory.GetFileSystemEntries(dir).ToArray());

        // 错误密码无法解锁
        Assert.Throws<CryptographicException>(
            () => InPlaceEncryptionService.DecryptFolder(dir, "Wrong123!"));

        InPlaceEncryptionService.DecryptFolder(dir, ValidPassword);
        Assert.False(InPlaceEncryptionService.IsFolderEncrypted(dir));
        // 解密后恢复正常访问且内容完好
        Assert.Equal("keep me", File.ReadAllText(inner));
    }

    [Fact]
    public void Folder_VerifyPassword_WrongPassword_Rejected_AfterAclLock()
    {
        string dir = Path.Combine(_dir, "lockedfolder");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "data");

        InPlaceEncryptionService.EncryptFolder(dir, ValidPassword);

        // 权限锁定后，错误密码校验失败
        Assert.False(InPlaceEncryptionService.VerifyFolderPassword(dir, "Wrong123!"));
        // 正确密码仍可校验
        Assert.True(InPlaceEncryptionService.VerifyFolderPassword(dir, ValidPassword));
        // 校验本身不应破坏锁定（校验后仍不可浏览）
        Assert.ThrowsAny<Exception>(() => Directory.GetFiles(dir));
    }

    [Fact]
    public void Folder_Encrypted_PreventsAddingFilesOrSubdirs()
    {
        string dir = Path.Combine(_dir, "locked_no_drop");
        Directory.CreateDirectory(dir);

        InPlaceEncryptionService.EncryptFolder(dir, ValidPassword);
        Assert.True(InPlaceEncryptionService.IsFolderEncrypted(dir));

        // 不能再往加密文件夹里创建文件（拖入/粘贴未加密文件的底层操作）
        Assert.ThrowsAny<Exception>(() => File.WriteAllText(Path.Combine(dir, "dropped.txt"), "x"));
        // 不能再往加密文件夹里创建子文件夹（拖入文件夹）
        Assert.ThrowsAny<Exception>(() => Directory.CreateDirectory(Path.Combine(dir, "dropped_sub")));

        // 解锁后恢复可写入，且正常解密
        InPlaceEncryptionService.DecryptFolder(dir, ValidPassword);
        Assert.False(InPlaceEncryptionService.IsFolderEncrypted(dir));
        File.WriteAllText(Path.Combine(dir, "after_decrypt.txt"), "ok");
        Assert.True(File.Exists(Path.Combine(dir, "after_decrypt.txt")));
    }

    [Fact]
    public void Folder_EncryptRoundTrip_EncryptsAllFilesRecursively()
    {
        string dir = Path.Combine(_dir, "deepfolder");
        Directory.CreateDirectory(Path.Combine(dir, "sub1", "sub2"));
        string f1 = Path.Combine(dir, "root.txt");
        string f2 = Path.Combine(dir, "sub1", "mid.txt");
        string f3 = Path.Combine(dir, "sub1", "sub2", "leaf.txt");
        File.WriteAllText(f1, "ROOT-DATA-机密");
        File.WriteAllText(f2, "MID-DATA");
        File.WriteAllText(f3, "LEAF-DATA");

        InPlaceEncryptionService.EncryptFolder(dir, ValidPassword);
        Assert.True(InPlaceEncryptionService.IsFolderEncrypted(dir));

        // 解锁 ACL 以便读取内部文件；内容应已被真正加密
        InPlaceEncryptionService.UnlockFolderAcl(dir);
        Assert.True(InPlaceEncryptionService.IsFileEncrypted(f1));
        Assert.True(InPlaceEncryptionService.IsFileEncrypted(f2));
        Assert.True(InPlaceEncryptionService.IsFileEncrypted(f3));
        Assert.DoesNotContain("ROOT-DATA-机密", File.ReadAllText(f1));

        // 解密后所有层级内容还原
        InPlaceEncryptionService.DecryptFolder(dir, ValidPassword);
        Assert.False(InPlaceEncryptionService.IsFolderEncrypted(dir));
        Assert.Equal("ROOT-DATA-机密", File.ReadAllText(f1));
        Assert.Equal("MID-DATA", File.ReadAllText(f2));
        Assert.Equal("LEAF-DATA", File.ReadAllText(f3));
    }

    [Fact]
    public void Folder_RecoveryCode_DecryptsAllFiles()
    {
        string dir = Path.Combine(_dir, "recfolder");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "a.txt"), "AAA");
        File.WriteAllText(Path.Combine(dir, "sub", "b.txt"), "BBB");

        // 所有文件共用同一恢复码
        string recovery = InPlaceEncryptionService.EncryptFolder(dir, ValidPassword);
        Assert.True(InPlaceEncryptionService.IsFolderEncrypted(dir));

        // 密码已忘：仅凭恢复码解密整个文件夹
        InPlaceEncryptionService.DecryptFolder(dir, password: null, recoveryCode: recovery);
        Assert.False(InPlaceEncryptionService.IsFolderEncrypted(dir));
        Assert.Equal("AAA", File.ReadAllText(Path.Combine(dir, "a.txt")));
        Assert.Equal("BBB", File.ReadAllText(Path.Combine(dir, "sub", "b.txt")));
    }

    [Fact]
    public void Folder_Decrypt_OldStyleAclOnly_StillWorks()
    {
        // 旧版（< v1.0.14.7）加密：仅放置标记 + ACL 封锁，内部文件未加密。
        // 新版解密需跳过未加密文件，仅移除标记与封锁，保证旧加密文件夹可正常解锁。
        string dir = Path.Combine(_dir, "oldstyle");
        Directory.CreateDirectory(dir);
        string plain = Path.Combine(dir, "plain.txt");
        File.WriteAllText(plain, "plain content");

        // 用旧版 ver=2 格式构造标记（标记需 Hidden + 有效密文头；旧版标记是单块格式）
        string markerPath = Path.Combine(dir, InPlaceEncryptionService.FolderLockFileName);
        File.WriteAllBytes(markerPath, BuildLegacyVer2Payload(Array.Empty<byte>(), ValidPassword, "OLD-STYLE-MARKER"));
        File.SetAttributes(markerPath, FileAttributes.Hidden | FileAttributes.System);

        InPlaceEncryptionService.LockFolderAcl(dir);
        Assert.True(InPlaceEncryptionService.IsFolderEncrypted(dir));
        // 旧版行为：内部文件未被加密
        Assert.False(InPlaceEncryptionService.IsFileEncrypted(plain));

        InPlaceEncryptionService.DecryptFolder(dir, ValidPassword);
        Assert.False(InPlaceEncryptionService.IsFolderEncrypted(dir));
        Assert.Equal("plain content", File.ReadAllText(plain)); // 未加密文件不被改动
    }

    [Fact]
    public void IsEncrypted_DetectsFileAndFolder()
    {
        string file = Path.Combine(_dir, "f.txt");
        File.WriteAllText(file, "hi");
        InPlaceEncryptionService.EncryptFile(file, ValidPassword);

        string dir = Path.Combine(_dir, "d");
        Directory.CreateDirectory(dir);
        InPlaceEncryptionService.EncryptFolder(dir, ValidPassword);

        Assert.True(InPlaceEncryptionService.IsEncrypted(file));
        Assert.True(InPlaceEncryptionService.IsEncrypted(dir));
        Assert.True(InPlaceEncryptionService.VerifyPassword(file, ValidPassword));
        Assert.True(InPlaceEncryptionService.VerifyPassword(dir, ValidPassword));
    }

    [Fact]
    public void RecoveryCode_CanDecryptFile_WhenPasswordForgotten()
    {
        string file = Path.Combine(_dir, "r.txt");
        string content = "保密内容 42";
        File.WriteAllText(file, content);

        // 加密时返回恢复码
        string recovery = InPlaceEncryptionService.EncryptFile(file, ValidPassword);
        Assert.Equal(6, recovery.Split('-').Length); // 6 组

        // 忘记密码，用恢复码解密
        Assert.True(InPlaceEncryptionService.VerifyFilePassword(file, recovery, isRecovery: true));
        Assert.False(InPlaceEncryptionService.VerifyFilePassword(file, ValidPassword, isRecovery: true));

        InPlaceEncryptionService.DecryptFile(file, password: "wrong-password-with-no-rec", recoveryCode: recovery);
        Assert.Equal(content, File.ReadAllText(file));
        Assert.False(InPlaceEncryptionService.IsFileEncrypted(file));
    }

    [Fact]
    public void RecoveryCode_WrongSecret_Rejected()
    {
        string file = Path.Combine(_dir, "w.txt");
        File.WriteAllText(file, "data");
        string recovery = InPlaceEncryptionService.EncryptFile(file, ValidPassword);

        // 错误的恢复码无法解密
        Assert.False(InPlaceEncryptionService.VerifyFilePassword(file, "AAAA-BBBB-CCCC-DDDD-EEEE-FFFF", isRecovery: true));
        Assert.Throws<CryptographicException>(
            () => InPlaceEncryptionService.DecryptFile(file, password: "x", recoveryCode: "AAAA-BBBB-CCCC-DDDD-EEEE-FFFF"));
    }

    [Fact]
    public void RecoveryCode_Folder_CanDecrypt_WhenPasswordForgotten()
    {
        string dir = Path.Combine(_dir, "recfolder");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "inner.txt"), "保密内容 88");

        string recovery = InPlaceEncryptionService.EncryptFolder(dir, ValidPassword);

        // 忘记密码，用恢复码解锁（带上连字符/大小写亦可，会被规范化）
        Assert.True(InPlaceEncryptionService.VerifyFolderPassword(dir, recovery, isRecovery: true));
        // 密码当恢复码用是错误的
        Assert.False(InPlaceEncryptionService.VerifyFolderPassword(dir, ValidPassword, isRecovery: true));

        InPlaceEncryptionService.DecryptFolder(dir, password: "not-the-password", recoveryCode: recovery);
        Assert.False(InPlaceEncryptionService.IsFolderEncrypted(dir));
        Assert.Equal("保密内容 88", File.ReadAllText(Path.Combine(dir, "inner.txt")));
    }
}
