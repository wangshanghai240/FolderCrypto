using System.Security.Cryptography;
using FolderCrypto.Core.Services;
using Xunit;

namespace FolderCrypto.Core.Tests;

public class ContainerServiceTests : IDisposable
{
    private readonly string _workDir;

    public ContainerServiceTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "FolderCryptoTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); }
        catch { /* 忽略清理失败 */ }
    }

    private const string ValidPassword = "Abcd1234!";

    [Fact]
    public void SingleFile_RoundTrip_RestoresContent()
    {
        string srcFile = Path.Combine(_workDir, "secret.txt");
        File.WriteAllText(srcFile, "机密内容 top-secret 123");

        string container = Path.Combine(_workDir, "out.fenc");
        string dest = Path.Combine(_workDir, "restored");

        ContainerService.CreateContainer(srcFile, container, ValidPassword);
        Assert.True(ContainerService.IsContainer(container));

        ContainerService.ExtractContainer(container, dest, ValidPassword);
        string restoredFile = Path.Combine(dest, "secret.txt");
        Assert.True(File.Exists(restoredFile));
        Assert.Equal("机密内容 top-secret 123", File.ReadAllText(restoredFile));
    }

    [Fact]
    public void Folder_RoundTrip_PreservesStructure()
    {
        string srcDir = Path.Combine(_workDir, "myfolder");
        Directory.CreateDirectory(Path.Combine(srcDir, "sub"));
        File.WriteAllText(Path.Combine(srcDir, "a.txt"), "hello A");
        File.WriteAllText(Path.Combine(srcDir, "sub", "b.txt"), "hello B");

        string container = Path.Combine(_workDir, "folder.fenc");
        ContainerService.CreateContainer(srcDir, container, ValidPassword);

        string dest = Path.Combine(_workDir, "restored");
        ContainerService.ExtractContainer(container, dest, ValidPassword);

        string restored = Path.Combine(dest, "myfolder");
        Assert.True(File.Exists(Path.Combine(restored, "a.txt")));
        Assert.True(File.Exists(Path.Combine(restored, "sub", "b.txt")));
        Assert.Equal("hello A", File.ReadAllText(Path.Combine(restored, "a.txt")));
        Assert.Equal("hello B", File.ReadAllText(Path.Combine(restored, "sub", "b.txt")));
    }

    [Fact]
    public void WrongPassword_VerifyReturnsFalse()
    {
        string srcFile = Path.Combine(_workDir, "s.txt");
        File.WriteAllText(srcFile, "data");
        string container = Path.Combine(_workDir, "c.fenc");

        ContainerService.CreateContainer(srcFile, container, ValidPassword);

        Assert.True(ContainerService.VerifyPassword(container, ValidPassword));
        Assert.False(ContainerService.VerifyPassword(container, "Wrong123!"));
    }

    [Fact]
    public void WrongPassword_ExtractThrowsCryptographicException()
    {
        string srcFile = Path.Combine(_workDir, "s.txt");
        File.WriteAllText(srcFile, "data");
        string container = Path.Combine(_workDir, "c.fenc");
        ContainerService.CreateContainer(srcFile, container, ValidPassword);

        Assert.Throws<CryptographicException>(
            () => ContainerService.ExtractContainer(container, _workDir, "Wrong123!"));
    }

    [Fact]
    public void ContainerContent_IsNotPlaintext()
    {
        const string secret = "ThisIsASecretPlaintext";
        string srcFile = Path.Combine(_workDir, "s.txt");
        File.WriteAllText(srcFile, secret);
        string container = Path.Combine(_workDir, "c.fenc");
        ContainerService.CreateContainer(srcFile, container, ValidPassword);

        // 容器的原始字节不应包含明文字符串
        byte[] bytes = File.ReadAllBytes(container);
        string utf8 = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(secret, utf8);
    }

    [Fact]
    public void PeekManifest_ReturnsOriginalName()
    {
        string srcFile = Path.Combine(_workDir, "report.md");
        File.WriteAllText(srcFile, "# 报告");
        string container = Path.Combine(_workDir, "r.fenc");
        ContainerService.CreateContainer(srcFile, container, ValidPassword);

        var manifest = ContainerService.PeekManifest(container, ValidPassword);
        Assert.Equal("report.md", manifest.OriginalName);
        Assert.False(manifest.IsDirectory);
    }
}
