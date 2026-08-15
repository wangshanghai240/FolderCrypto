using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FolderCrypto.Core.Encryption;
using FolderCrypto.Core.Model;
using FolderCrypto.Core.Security;

namespace FolderCrypto.Core.Services;

/// <summary>
/// 容器化加密的服务层：
///   - 将单个文件或整个文件夹递归地打包加密为 .fenc 容器
///   - 校验密码（校验逻辑由密码验证器实现）
///   - 将容器解密还原回原文件/目录
/// </summary>
public sealed class ContainerService
{
    private const string MetaInfo = "FolderCrypto:MetaKey";
    private const string DataInfo = "FolderCrypto:DataKey";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    // ---- 对外接口 ----

    /// <summary>把文件或文件夹加密打包为 .fenc 容器。</summary>
    public static void CreateContainer(string sourcePath, string containerPath, string password)
    {
        PasswordHasher derived = PasswordHasher.Derive(password);
        var source = ResolveSource(sourcePath);

        var manifest = new ContainerManifest
        {
            IsDirectory = source.IsDirectory,
            OriginalName = source.OriginalName,
            OriginalSize = source.TotalSize
        };

        // 第一步：逐文件加密并记录相对偏移（不写入文件，先确定元数据长度）。
        // PayloadOffset 为相对 payload 起始位置的偏移，与元数据长度无关。
        var payloads = new List<(string Rel, byte[] Data)>();
        long relOffset = 0;
        foreach (var file in source.Files)
        {
            string rel = source.IsDirectory
                ? Path.GetRelativePath(sourcePath, file).Replace('\\', '/')
                : Path.GetFileName(file);

            byte[] encrypted = EncryptFileData(file, derived.Key);
            manifest.Entries.Add(new ManifestEntry
            {
                Path = rel,
                IsDirectory = false,
                PayloadOffset = relOffset,
                EncryptedLength = encrypted.Length
            });
            payloads.Add((rel, encrypted));
            relOffset += encrypted.Length;
        }

        // 目录条目（用于重建空目录结构）
        if (source.IsDirectory)
        {
            foreach (var dir in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories)
                                         .OrderBy(d => d, StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(sourcePath, dir).Replace('\\', '/') + "/";
                manifest.Entries.Add(new ManifestEntry { Path = rel, IsDirectory = true });
            }
        }

        // 第二步：写出容器 = header + 加密元数据 + 全部加密文件负载
        using var fs = File.Create(containerPath);
        WriteHeader(fs, derived);
        long metaLen = WriteMeta(fs, manifest, derived.Key);   // 位于 FixedHeaderSize 之后

        long payloadStart = ContainerFormat.FixedHeaderSize + metaLen;
        fs.Position = payloadStart;
        foreach (var (_, data) in payloads)
        {
            fs.Write(data, 0, data.Length);
        }
    }

    /// <summary>是否一个 .fenc 容器文件（校验 magic）。</summary>
    public static bool IsContainer(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[ContainerFormat.Magic.Length];
            int read = fs.Read(head);
            return read == ContainerFormat.Magic.Length && ContainerFormat.HasMatchingMagic(head);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 校验密码是否正确。使用固定时间比较，错误密码返回 false。
    /// </summary>
    public static bool VerifyPassword(string containerPath, string password)
    {
        try
        {
            using var fs = File.OpenRead(containerPath);
            var header = ReadHeader(fs);
            byte[] key = PasswordHasher.DeriveKey(password, header.Salt);
            return CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(key), header.Verifier);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>取得容器内清单（需正确密码解密元数据），供 UI 展示。</summary>
    public static ContainerManifest PeekManifest(string containerPath, string password)
    {
        using var fs = File.OpenRead(containerPath);
        var header = ReadHeader(fs);
        byte[] key = RequireValidPassword(fs, header, password);
        return ReadMeta(fs, header, key);
    }

    /// <summary>解压容器到指定目录，密码校验通过后还原原文件/目录结构。</summary>
    public static void ExtractContainer(string containerPath, string destDirectory, string password)
    {
        using var fs = File.OpenRead(containerPath);
        var header = ReadHeader(fs);
        byte[] key = RequireValidPassword(fs, header, password);
        var manifest = ReadMeta(fs, header, key);

        // 目录容器：还原到 destDir\OriginalName\...；单文件容器：直接还原到 destDir\OriginalName。
        // 单文件容器中 entry.Path 即文件名，因此根目录应为 destDirectory，避免路径重复。
        string root = manifest.IsDirectory
            ? Path.Combine(destDirectory, SanitizeName(manifest.OriginalName))
            : destDirectory;

        // 先解密内容文件
        foreach (var entry in manifest.Entries.Where(e => !e.IsDirectory))
        {
            string targetFile = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            string? targetDir = Path.GetDirectoryName(targetFile);
            if (targetDir != null) Directory.CreateDirectory(targetDir);

            byte[] cipher = ReadAt(fs, header.PayloadStart + entry.PayloadOffset, entry.EncryptedLength);
            byte[] plain = AesGcmEncryption.Decrypt(HkdfDerive(key, DataInfo), cipher);
            File.WriteAllBytes(targetFile, plain);
        }

        // 再创建空目录
        foreach (var entry in manifest.Entries.Where(e => e.IsDirectory))
        {
            string targetDir = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(targetDir);
        }
    }

    // ---- 内部实现 ----

    private static (List<string> Files, bool IsDirectory, string OriginalName, long TotalSize) ResolveSource(string path)
    {
        if (Directory.Exists(path))
        {
            List<string> files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                                          .OrderBy(f => f, StringComparer.Ordinal)
                                          .ToList();
            return (files, true, new DirectoryInfo(path).Name, files.Sum(f => new FileInfo(f).Length));
        }

        if (File.Exists(path))
            return (new List<string> { path }, false, Path.GetFileName(path), new FileInfo(path).Length);

        throw new FileNotFoundException("源文件或文件夹不存在。", path);
    }

    private static void WriteHeader(Stream fs, PasswordHasher derived)
    {
        fs.Write(ContainerFormat.MagicBytes);
        fs.WriteByte(ContainerFormat.Version);
        fs.Write(derived.Salt);
        fs.Write(derived.Verifier);
        fs.Write(BitConverter.GetBytes(0)); // 占位 metaLength，WriteMeta 回填
    }

    private static byte[] EncryptFileData(string filePath, byte[] masterKey)
    {
        byte[] dataKey = HkdfDerive(masterKey, DataInfo);
        return AesGcmEncryption.Encrypt(dataKey, File.ReadAllBytes(filePath));
    }

    private static long WriteMeta(Stream fs, ContainerManifest manifest, byte[] masterKey)
    {
        byte[] metaKey = HkdfDerive(masterKey, MetaInfo);
        byte[] metaJson = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        byte[] metaCipher = AesGcmEncryption.Encrypt(metaKey, metaJson);

        // 回填 metaLength 字段（位于 FixedHeaderSize - 4 处），随后写入加密元数据
        fs.Position = ContainerFormat.FixedHeaderSize - ContainerFormat.MetaLengthFieldSize;
        fs.Write(BitConverter.GetBytes(metaCipher.Length));
        fs.Write(metaCipher, 0, metaCipher.Length);
        return metaCipher.Length;
    }

    private static HeaderData ReadHeader(Stream fs)
    {
        fs.Position = 0;
        Span<byte> magic = stackalloc byte[ContainerFormat.Magic.Length];
        fs.ReadExactly(magic);
        if (!ContainerFormat.HasMatchingMagic(magic))
            throw new InvalidDataException("不是有效的 FolderCrypto 容器文件。");

        int ver = fs.ReadByte();
        if (ver != ContainerFormat.Version)
            throw new InvalidDataException($"不支持的容器版本: {ver}");

        byte[] salt = new byte[ContainerFormat.SaltSize];
        fs.ReadExactly(salt);

        byte[] verifier = new byte[ContainerFormat.VerifierSize];
        fs.ReadExactly(verifier);

        Span<byte> lenBuf = stackalloc byte[ContainerFormat.MetaLengthFieldSize];
        fs.ReadExactly(lenBuf);
        int metaLength = BitConverter.ToInt32(lenBuf);

        return new HeaderData { Salt = salt, Verifier = verifier, MetaLength = metaLength };
    }

    /// <summary>用密码派生密钥并校验验证器；通过则返回密钥，否则抛 <see cref="CryptographicException"/>。</summary>
    private static byte[] RequireValidPassword(Stream fs, HeaderData header, string password)
    {
        byte[] key = PasswordHasher.DeriveKey(password, header.Salt);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(key), header.Verifier))
            throw new CryptographicException("密码错误，无法解密。");
        return key;
    }

    private static ContainerManifest ReadMeta(Stream fs, HeaderData header, byte[] masterKey)
    {
        byte[] metaKey = HkdfDerive(masterKey, MetaInfo);
        byte[] metaCipher = ReadAt(fs, ContainerFormat.FixedHeaderSize, header.MetaLength);
        byte[] plain = AesGcmEncryption.Decrypt(metaKey, metaCipher);
        return JsonSerializer.Deserialize<ContainerManifest>(plain, JsonOptions)
               ?? throw new InvalidDataException("清单解析失败。");
    }

    private static byte[] ReadAt(Stream fs, long offset, long count)
    {
        byte[] buffer = new byte[count];
        fs.Position = offset;
        fs.ReadExactly(buffer);
        return buffer;
    }

    private static byte[] HkdfDerive(byte[] ikm, string info)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt: null, Encoding.UTF8.GetBytes(info));

    private static string SanitizeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c.ToString(), "_");
        return string.IsNullOrWhiteSpace(name) ? "encrypted" : name;
    }

    private sealed class HeaderData
    {
        public byte[] Salt = Array.Empty<byte>();
        public byte[] Verifier = Array.Empty<byte>();
        public int MetaLength;

        /// <summary>payload 数据区起始位置（即固定头部 + 加密元数据之后）。</summary>
        public int FixedHeaderSize => ContainerFormat.FixedHeaderSize;

        /// <summary>payload 数据区起始位置。</summary>
        public long PayloadStart => ContainerFormat.FixedHeaderSize + MetaLength;
    }
}

// ===========================================================================
// 原地加密/解密服务（不再使用容器）。
//
// 文件：把原文件字节就地改写为
//   [magic "FCENC000"][ver=2][pwdSalt(16)][pwdVerifier(32)][recSalt(16)][recVerifier(32)]
//   [wrapLen(4)][wrappedDataKey_byPwd][wrappedDataKey_byRecovery][AES-GCM(dataKey) 密文]
//   - dataKey 为随机 32 字节；分别用“密码派生的密钥”和“恢复码派生的密钥”各包裹一次。
//   - 解密时输入密码或恢复码任一正确，都能解开 dataKey 从而解密。
//
// 文件夹：只加密“文件夹本身”，不改动内部内容。
//   在文件夹内放置隐藏的 .folderlock 标记文件（同样含 password/recovery 两套验证与包裹），
//   不改文件夹自身属性（不隐藏）。
// ===========================================================================
    /// <summary>原地加密/解密（文件 + 文件夹锁定，密码/恢复码双通道）。</summary>
    public static class InPlaceEncryptionService
    {
        private const string Magic = "FCENC000";        // 8 bytes
        private const byte Version = 2;
        private const int SaltSize = 16;
        private const int VerifierSize = 32;
        private const int DataKeySize = 32;
        private const int WrapLenFieldSize = 4;

        /// <summary>文件夹加密锁定标记文件名。</summary>
        public const string FolderLockFileName = ".folderlock";

        private const string WrapPwdInfo = "FolderCrypto:WrapPwd";
        private const string WrapRecInfo = "FolderCrypto:WrapRec";

        private static readonly byte[] MagicBytes = System.Text.Encoding.ASCII.GetBytes(Magic);

        #region 恢复码

        private static readonly char[] RecAlphabet =
            "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray(); // 去除易混淆的 0/O/1/I

        /// <summary>返回新的恢复码：6 组 × 4 字符，如 "ABCD-EFGH-JKLM-NPQR-STUV-WXYZ"。</summary>
        public static string GenerateRecoveryCode()
        {
            var chars = new char[24];
            var rnd = System.Security.Cryptography.RandomNumberGenerator.GetBytes(24);
            for (int i = 0; i < 24; i++)
                chars[i] = RecAlphabet[rnd[i] % RecAlphabet.Length];

            var sb = new System.Text.StringBuilder(28);
            for (int i = 0; i < 24; i++)
            {
                if (i > 0 && i % 4 == 0) sb.Append('-');
                sb.Append(chars[i]);
            }
            return sb.ToString();
        }

        /// <summary>规范化恢复码：去空白与分隔符，转大写。</summary>
        public static string NormalizeRecoveryCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return string.Empty;
            var sb = new System.Text.StringBuilder(code.Length);
            foreach (var c in code)
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        #endregion

        #region 文件

        /// <summary>对文件进行原地加密（覆盖源文件）。返回恢复码（需展示给用户保存）。</summary>
        public static string EncryptFile(string filePath, string password, IProgress<int>? progress = null)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("文件不存在。", filePath);
            if (IsFileEncrypted(filePath))
                throw new InvalidOperationException("该文件已加密。");

            string recoveryCode = GenerateRecoveryCode();

            // 读取原文件（分块，报告进度 0-50）
            progress?.Report(0);
            byte[] content = ReadFileWithProgress(filePath, progress, 0, 50);

            // 加密（含头/密钥包裹）。整段加密后报告 85。
            byte[] payload = BuildPayload(content, password, recoveryCode);
            progress?.Report(85);

            // 写回（报告 85-100）
            WriteFileWithProgress(filePath, payload, progress, 85, 100);

            CryptoUtil.Zero(content);
            return recoveryCode;
        }

        /// <summary>对已加密文件进行原地解密。输入密码或恢复码任一正确即可。</summary>
        public static void DecryptFile(string filePath, string? password = null, string? recoveryCode = null, IProgress<int>? progress = null)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("文件不存在。", filePath);
            if (!IsFileEncrypted(filePath))
                throw new InvalidOperationException("该文件不是已加密的文件。");

            progress?.Report(0);
            byte[] cipher = ReadFileWithProgress(filePath, progress, 0, 50);

            byte[] plain = DecryptPayload(cipher, encrypted: true, password, recoveryCode);
            progress?.Report(85);

            string tmp = filePath + ".tmp";
            File.WriteAllBytes(tmp, plain);
            // 写回后删除原文件并替换
            File.Delete(filePath);
            File.Move(tmp, filePath);
            /* 进度在替换后视为 100 */

            CryptoUtil.Zero(plain);
            CryptoUtil.Zero(cipher);
            progress?.Report(100);
        }

        /// <summary>分块读取文件并按读取量报告进度（from..to 百分比区间）。</summary>
        private static byte[] ReadFileWithProgress(string path, IProgress<int>? progress, int from, int to)
        {
            FileInfo fi = new FileInfo(path);
            long total = fi.Length;
            using var fs = File.OpenRead(path);
            byte[] all = new byte[total];
            const int chunk = 1 << 20; // 1MB
            int read = 0;
            while (read < total)
            {
                int n = fs.Read(all, read, (int)Math.Min(chunk, total - read));
                if (n <= 0) break;
                read += n;
                if (progress != null && total > 0)
                    progress.Report(from + (int)((double)(from == to ? 0 : (to - from)) * read / total));
            }
            return all;
        }

        /// <summary>分块写文件并按写入量报告进度（from..to 百分比区间）。</summary>
        private static void WriteFileWithProgress(string path, byte[] data, IProgress<int>? progress, int from, int to)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            const int chunk = 1 << 20;
            int written = 0;
            while (written < data.Length)
            {
                int n = (int)Math.Min(chunk, data.Length - written);
                fs.Write(data, written, n);
                written += n;
                if (progress != null && data.Length > 0)
                    progress.Report(from + (int)((double)(from == to ? 0 : (to - from)) * written / data.Length));
            }
        }

        /// <summary>是否已加密的文件。</summary>
        public static bool IsFileEncrypted(string filePath)
        {
            try
            {
                using var fs = File.OpenRead(filePath);
                Span<byte> head = stackalloc byte[Magic.Length];
                int read = fs.Read(head);
                return read == Magic.Length && head.SequenceEqual(MagicBytes);
            }
            catch { return false; }
        }

        /// <summary>校验文件“密码或恢复码”是否正确。</summary>
        public static bool VerifyFilePassword(string filePath, string secret, bool isRecovery = false)
        {
            try
            {
                using var fs = File.OpenRead(filePath);
                var h = ReadContainerHeader(fs);
                return isRecovery
                    ? TryVerify(h, NormalizeRecoveryCode(secret), useRecovery: true)
                    : TryVerify(h, secret, useRecovery: false);
            }
            catch { return false; }
        }

        #endregion

        #region 文件夹

        /// <summary>加密（锁定）文件夹：放置隐藏标记 + 用权限禁止浏览该文件夹。</summary>
        /// <remarks>加密后双击该文件夹会被拒绝访问（访问被拒绝），从而无法进入。</remarks>
        public static string EncryptFolder(string folderPath, string password)
        {
            if (!Directory.Exists(folderPath))
                throw new DirectoryNotFoundException("文件夹不存在。");
            if (IsFolderEncrypted(folderPath))
                throw new InvalidOperationException("该文件夹已加密。");

            string recoveryCode = GenerateRecoveryCode();
            byte[] marker = BuildPayload(Array.Empty<byte>(), password, recoveryCode);

            string markerPath = Path.Combine(folderPath, FolderLockFileName);
            File.WriteAllBytes(markerPath, marker);
            File.SetAttributes(markerPath, FileAttributes.Hidden | FileAttributes.System);

            // 用权限禁止浏览该文件夹（双击 → 访问被拒绝）。
            LockFolderAcl(folderPath);
            return recoveryCode;
        }

        /// <summary>是否已加密（锁定）的文件夹。标记存在 + 已做浏览权限封锁。</summary>
        public static bool IsFolderEncrypted(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return false;
            string markerPath = Path.Combine(folderPath, FolderLockFileName);
            if (!File.Exists(markerPath)) return false;

            var markerAttrs = File.GetAttributes(markerPath);
            if ((markerAttrs & FileAttributes.Hidden) == 0) return false;

            return IsFileEncrypted(markerPath);
        }

        /// <summary>校验文件夹“密码或恢复码”是否正确。若文件夹被权限锁定，先临时解锁以读取标记，校验后重新锁回。</summary>
        public static bool VerifyFolderPassword(string folderPath, string secret, bool isRecovery = false)
        {
            string markerPath = Path.Combine(folderPath, FolderLockFileName);
            if (!File.Exists(markerPath)) return false;

            bool wasLocked = UnlockFolderAcl(folderPath);
            try
            {
                return VerifyFilePassword(markerPath, secret, isRecovery);
            }
            finally
            {
                if (wasLocked) LockFolderAcl(folderPath);
            }
        }

        /// <summary>解密（解锁）文件夹：移除浏览权限封锁并删除标记。密码或恢复码任一正确即可。</summary>
        public static void DecryptFolder(string folderPath, string? password = null, string? recoveryCode = null)
        {
            if (!Directory.Exists(folderPath))
                throw new DirectoryNotFoundException("文件夹不存在。");

            string markerPath = Path.Combine(folderPath, FolderLockFileName);
            if (!File.Exists(markerPath))
                throw new InvalidOperationException("该文件夹不是已加密的文件夹。");

            // 权限锁定的文件夹无法直接读取标记：先解锁再读取校验。
            bool wasLocked = UnlockFolderAcl(folderPath);
            try
            {
                DecryptPayload(File.ReadAllBytes(markerPath), encrypted: true, password, recoveryCode);
            }
            catch
            {
                // 密码/恢复码错误：重新锁回去，避免错误输入导致解锁。
                if (wasLocked) LockFolderAcl(folderPath);
                throw;
            }

            File.Delete(markerPath);
        }

        /// <summary>
        /// 用权限封锁文件夹的“浏览”能力（拒绝当前用户进入/列出），
        /// 使双击无法进入，达到“真正锁定文件夹”的效果。
        /// 仅封锁浏览权限，保留所有者修改权限，因此可由所有者正常解锁。
        /// </summary>
        public static void LockFolderAcl(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return;
            var id = WindowsIdentity.GetCurrent().User;
            if (id == null) return;

            var di = new DirectoryInfo(folderPath);
            var sec = di.GetAccessControl(AccessControlSections.Access);
            var deny = new FileSystemAccessRule(
                id,
                FileSystemRights.ListDirectory | FileSystemRights.Traverse,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Deny);
            sec.AddAccessRule(deny);
            di.SetAccessControl(sec);
        }

        /// <summary>移除文件夹上的“浏览拒绝”权限，恢复为可访问。返回是否确实曾经有锁定。</summary>
        public static bool UnlockFolderAcl(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return false;
            var id = WindowsIdentity.GetCurrent().User;
            if (id == null) return false;

            var di = new DirectoryInfo(folderPath);
            var sec = di.GetAccessControl(AccessControlSections.Access);
            bool removedAny = false;
            foreach (var rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule is FileSystemAccessRule fr &&
                    fr.IdentityReference.Equals(id) &&
                    fr.AccessControlType == AccessControlType.Deny)
                {
                    sec.RemoveAccessRule(fr);
                    removedAny = true;
                }
            }
            if (removedAny)
                di.SetAccessControl(sec);
            return removedAny;
        }

        #endregion

        #region 通用

        /// <summary>判断路径是否已加密。</summary>
        public static bool IsEncrypted(string path)
        {
            if (Directory.Exists(path)) return IsFolderEncrypted(path);
            if (File.Exists(path)) return IsFileEncrypted(path);
            return false;
        }

        /// <summary>校验路径的“密码或恢复码”。</summary>
        public static bool VerifyPassword(string path, string secret, bool isRecovery = false)
        {
            if (Directory.Exists(path)) return VerifyFolderPassword(path, secret, isRecovery);
            if (File.Exists(path)) return VerifyFilePassword(path, secret, isRecovery);
            return false;
        }

        #endregion

        #region 内部

        /// <summary>构建完整密文（文件）或标记（文件夹）。</summary>
        private static byte[] BuildPayload(byte[] content, string password, string recoveryCode)
        {
            byte[] pwdSalt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(SaltSize);
            byte[] passwordKey = PasswordHasher.DeriveKey(password, pwdSalt);

            byte[] recSalt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(SaltSize);
            byte[] recoveryKey = PasswordHasher.DeriveKey(NormalizeRecoveryCode(recoveryCode), recSalt);

            byte[] dataKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(DataKeySize);

            byte[] wrapPwd = AesGcmEncryption.Encrypt(HkdfDerive(passwordKey, WrapPwdInfo), dataKey);
            byte[] wrapRec = AesGcmEncryption.Encrypt(HkdfDerive(recoveryKey, WrapRecInfo), dataKey);

            byte[] cipher = AesGcmEncryption.Encrypt(dataKey, content);

            int headLen = Magic.Length + 1 + 2 * (SaltSize + VerifierSize) + WrapLenFieldSize;
            var head = new byte[headLen];
            int o = 0;
            MagicBytes.CopyTo(head, o); o += Magic.Length;
            head[o++] = Version;
            pwdSalt.CopyTo(head, o); o += SaltSize;
            SHA256.HashData(passwordKey).CopyTo(head, o); o += VerifierSize;      // pwdVerifier
            recSalt.CopyTo(head, o); o += SaltSize;
            SHA256.HashData(recoveryKey).CopyTo(head, o); o += VerifierSize;      // recVerifier
            BitConverter.GetBytes(wrapPwd.Length).CopyTo(head, o); o += WrapLenFieldSize;

            var payload = new byte[head.Length + wrapPwd.Length + wrapRec.Length + cipher.Length];
            Buffer.BlockCopy(head, 0, payload, 0, head.Length);
            int p = head.Length;
            Buffer.BlockCopy(wrapPwd, 0, payload, p, wrapPwd.Length); p += wrapPwd.Length;
            Buffer.BlockCopy(wrapRec, 0, payload, p, wrapRec.Length); p += wrapRec.Length;
            Buffer.BlockCopy(cipher, 0, payload, p, cipher.Length);

            CryptoUtil.Zero(passwordKey);
            CryptoUtil.Zero(recoveryKey);
            CryptoUtil.Zero(dataKey);
            CryptoUtil.Zero(content);
            CryptoUtil.Zero(cipher);
            return payload;
        }

        /// <summary>从密文/标记中解密出原文。密码或恢复码任一正确即可。</summary>
        private static byte[] DecryptPayload(byte[] payload, bool encrypted, string? password, string? recoveryCode)
        {
            _ = encrypted;
            using var ms = new MemoryStream(payload, writable: false);
            var h = ReadContainerHeader(ms);

            // 读取两段包裹密钥与密文
            ms.Position = h.DataStart;
            byte[] wrapPwd = new byte[h.WrapLen]; ms.ReadExactly(wrapPwd);
            byte[] wrapRec = new byte[h.WrapLen]; ms.ReadExactly(wrapRec);
            int cipherLen = payload.Length - (int)h.DataStart - h.WrapLen - h.WrapLen;
            byte[] cipher = new byte[cipherLen];
            ms.ReadExactly(cipher);

            // 尝试用密码解锁
            if (!string.IsNullOrEmpty(password))
            {
                byte[] passwordKey = PasswordHasher.DeriveKey(password, h.PwdSalt);
                if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(passwordKey), h.PwdVerifier))
                {
                    var dataKey = UnwrapKey(wrapPwd, HkdfDerive(passwordKey, WrapPwdInfo));
                    if (dataKey != null) return AesGcmEncryption.Decrypt(dataKey, cipher);
                }
            }

            // 尝试用恢复码解锁
            if (!string.IsNullOrEmpty(recoveryCode))
            {
                byte[] recoveryKey = PasswordHasher.DeriveKey(NormalizeRecoveryCode(recoveryCode), h.RecSalt);
                if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(recoveryKey), h.RecVerifier))
                {
                    var dataKey = UnwrapKey(wrapRec, HkdfDerive(recoveryKey, WrapRecInfo));
                    if (dataKey != null) return AesGcmEncryption.Decrypt(dataKey, cipher);
                }
            }

            throw new CryptographicException("密码或恢复码错误，无法解密。");
        }

        /// <summary>解开包裹的数据密钥；认证失败（密钥不对）返回 null。</summary>
        private static byte[]? UnwrapKey(byte[] wrapped, byte[] wrapKey)
        {
            try
            {
                return AesGcmEncryption.Decrypt(wrapKey, wrapped);
            }
            catch (CryptographicException)
            {
                return null; // 认证失败 = 该通道不正确
            }
        }

        private static HeaderInfo ReadContainerHeader(Stream fs)
        {
            fs.Position = 0;
            Span<byte> magic = stackalloc byte[Magic.Length];
            fs.ReadExactly(magic);
            if (!magic.SequenceEqual(MagicBytes))
                throw new InvalidDataException("不是有效的加密文件。");

            int ver = fs.ReadByte();
            if (ver != Version)
                throw new InvalidDataException($"不支持的版本: {ver}");

            var h = new HeaderInfo();
            h.PwdSalt = new byte[SaltSize]; fs.ReadExactly(h.PwdSalt);
            h.PwdVerifier = new byte[VerifierSize]; fs.ReadExactly(h.PwdVerifier);
            h.RecSalt = new byte[SaltSize]; fs.ReadExactly(h.RecSalt);
            h.RecVerifier = new byte[VerifierSize]; fs.ReadExactly(h.RecVerifier);

            Span<byte> lenBuf = stackalloc byte[WrapLenFieldSize];
            fs.ReadExactly(lenBuf);
            h.WrapLen = BitConverter.ToInt32(lenBuf);

            h.DataStart = Magic.Length + 1 + 2 * (SaltSize + VerifierSize) + WrapLenFieldSize;
            return h;
        }

        private static bool TryVerify(HeaderInfo h, string secret, bool useRecovery)
        {
            if (useRecovery)
            {
                byte[] key = PasswordHasher.DeriveKey(secret, h.RecSalt);
                return CryptographicOperations.FixedTimeEquals(SHA256.HashData(key), h.RecVerifier);
            }
            byte[] k = PasswordHasher.DeriveKey(secret, h.PwdSalt);
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(k), h.PwdVerifier);
        }

        private static byte[] HkdfDerive(byte[] ikm, string info)
            => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt: null, Encoding.UTF8.GetBytes(info));

        private sealed class HeaderInfo
        {
            public byte[] PwdSalt = Array.Empty<byte>();
            public byte[] PwdVerifier = Array.Empty<byte>();
            public byte[] RecSalt = Array.Empty<byte>();
            public byte[] RecVerifier = Array.Empty<byte>();
            public int WrapLen;
            public long DataStart;
        }
        #endregion
    }

    /// <summary>敏感字节清零工具。</summary>
    internal static class CryptoUtil
    {
        public static void Zero(byte[] data)
        {
            if (data != null) Array.Clear(data, 0, data.Length);
        }
    }
