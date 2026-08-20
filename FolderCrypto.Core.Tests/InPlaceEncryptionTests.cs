using System.Security.Cryptography;
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
    public void Folder_EncryptLockAndDecrypt_Restores()
    {
        string dir = Path.Combine(_dir, "myfolder");
        Directory.CreateDirectory(dir);
        // 内部内容不应被修改（仅被封锁浏览）
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
