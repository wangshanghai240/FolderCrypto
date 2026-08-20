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
// 文件（ver=3）：把原文件字节就地改写为
//   [magic "FCENC000"][ver=3][pwdSalt(16)][pwdVerifier(32)][recSalt(16)][recVerifier(32)]
//   [wrapLen(4)][wrappedDataKey_byPwd][wrappedDataKey_byRecovery]
//   后接若干“分块”：[chunkLen(4)][nonce(12)][AES-GCM 密文块][tag(16)] × N
//   - dataKey 为随机 32 字节；分别用“密码派生的密钥”和“恢复码派生的密钥”各包裹一次。
//   - 解密时输入密码或恢复码任一正确，都能解开 dataKey 从而解密。
//   - 分块流式加解密 → 内存峰值 ≈ 一个分块（4MB），与文件大小无关。
//   - 兼容读取旧版 ver=2（单条 AES-GCM 整文件，整块解密）。
//
// 文件夹：递归加密文件夹内所有文件（ver=3），并在文件夹内放置隐藏的
//   .folderlock 标记文件（ver=2 单块格式，体积极小；同样含 password/recovery 两套验证），
//   然后以 ACL 封锁禁止浏览/进入、禁止拖入未加密内容。文件夹自身属性不改（不隐藏）。
// ===========================================================================
    /// <summary>原地加密/解密（文件 + 文件夹锁定，密码/恢复码双通道）。</summary>
    public static class InPlaceEncryptionService
    {
        private const string Magic = "FCENC000";        // 8 bytes
        private const byte Version = 3;                 // 当前写入版本：分块流式加解密（内存有界）
        private const byte LegacyVersion = 2;           // 旧版本：单条 AES-GCM 整文件（读取兼容）
        private const int SaltSize = 16;
        private const int VerifierSize = 32;
        private const int DataKeySize = 32;
        private const int WrapLenFieldSize = 4;
        private const int NonceSize = 12;               // AES-GCM nonce
        private const int TagSize = 16;                 // AES-GCM 认证标签

        /// <summary>分块大小（4MB）：流式加解密内存峰值与文件大小无关。</summary>
        private const int ChunkSize = 4 * 1024 * 1024;

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
            EncryptFileWithRecovery(filePath, password, recoveryCode, progress);
            return recoveryCode;
        }

        /// <summary>用指定恢复码流式加密文件（ver=3 分块，内存有界）。文件夹递归加密时所有文件共用同一恢复码。</summary>
        private static void EncryptFileWithRecovery(string filePath, string password, string recoveryCode, IProgress<int>? fileProgress = null)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("文件不存在。", filePath);

            // 随机数据密钥：分别用“密码派生的密钥”与“恢复码派生的密钥”各包裹一次
            byte[] dataKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(DataKeySize);
            byte[] pwdSalt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(SaltSize);
            byte[] passwordKey = PasswordHasher.DeriveKey(password, pwdSalt);
            byte[] recSalt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(SaltSize);
            byte[] recoveryKey = PasswordHasher.DeriveKey(NormalizeRecoveryCode(recoveryCode), recSalt);

            byte[] wrapPwd = AesGcmEncryption.Encrypt(HkdfDerive(passwordKey, WrapPwdInfo), dataKey);
            byte[] wrapRec = AesGcmEncryption.Encrypt(HkdfDerive(recoveryKey, WrapRecInfo), dataKey);

            // 头部：magic + 版本(3) + 两套 salt/verifier + wrapLen
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

            // 流式：分块 AES-GCM，逐块 [len4][nonce12][ciphertext][tag16] 写出（内存峰值 ≈ 一个分块）
            string tmp = filePath + ".tmp";
            long total = new FileInfo(filePath).Length;
            fileProgress?.Report(0);
            using (var src = File.OpenRead(filePath))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                dst.Write(head, 0, head.Length);
                dst.Write(wrapPwd, 0, wrapPwd.Length);
                dst.Write(wrapRec, 0, wrapRec.Length);

                var chunk = new byte[ChunkSize];
                long done = 0;
                using var aes = new AesGcm(dataKey, TagSize);
                int n;
                while ((n = src.Read(chunk, 0, chunk.Length)) > 0)
                {
                    var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(NonceSize);
                    var ciphertext = new byte[n];
                    var tag = new byte[TagSize];
                    aes.Encrypt(nonce, chunk.AsSpan(0, n), ciphertext, tag);

                    dst.Write(BitConverter.GetBytes(n), 0, 4);
                    dst.Write(nonce, 0, nonce.Length);
                    dst.Write(ciphertext, 0, n);
                    dst.Write(tag, 0, tag.Length);

                    Array.Clear(chunk, 0, n);   // 清零明文块
                    done += n;
                    if (fileProgress != null && total > 0)
                        fileProgress.Report((int)(done * 100 / total));
                }
            }

            File.Delete(filePath);
            File.Move(tmp, filePath);

            CryptoUtil.Zero(dataKey);
            CryptoUtil.Zero(passwordKey);
            CryptoUtil.Zero(recoveryKey);
            CryptoUtil.Zero(wrapPwd);
            CryptoUtil.Zero(wrapRec);
            fileProgress?.Report(100);
        }

        /// <summary>对已加密文件进行流式解密。新版 ver=3 分块（内存有界）；旧版 ver=2 整块解密兼容。密码或恢复码任一正确即可。</summary>
        public static void DecryptFile(string filePath, string? password = null, string? recoveryCode = null, IProgress<int>? progress = null)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("文件不存在。", filePath);
            if (!IsFileEncrypted(filePath))
                throw new InvalidOperationException("该文件不是已加密的文件。");

            long total = new FileInfo(filePath).Length;
            string tmp = filePath + ".tmp";
            progress?.Report(0);

            using (var fs = File.OpenRead(filePath))
            {
                var h = ReadContainerHeader(fs);
                byte[] dataKey = UnwrapDataKey(fs, h, password, recoveryCode);

                using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write);
                if (h.Version <= LegacyVersion)
                {
                    // 旧版 ver=2：整份密文是单条 AES-GCM 消息（只能整块解密，兼容旧文件）
                    byte[] payload = new byte[fs.Length - fs.Position];
                    fs.ReadExactly(payload);
                    byte[] plain = AesGcmEncryption.Decrypt(dataKey, payload);
                    dst.Write(plain, 0, plain.Length);
                    CryptoUtil.Zero(plain);
                    CryptoUtil.Zero(payload);
                }
                else
                {
                    DecryptChunks(fs, dst, dataKey, progress, total);
                }

                CryptoUtil.Zero(dataKey);
            }

            File.Delete(filePath);
            File.Move(tmp, filePath);
            progress?.Report(100);
        }

        /// <summary>读取两段包裹密钥并解开数据密钥；密码或恢复码任一正确即可，均失败抛异常。</summary>
        private static byte[] UnwrapDataKey(Stream fs, HeaderInfo h, string? password, string? recoveryCode)
        {
            byte[] wrapPwd = new byte[h.WrapLen]; fs.ReadExactly(wrapPwd);
            byte[] wrapRec = new byte[h.WrapLen]; fs.ReadExactly(wrapRec);

            if (!string.IsNullOrEmpty(password))
            {
                byte[] passwordKey = PasswordHasher.DeriveKey(password, h.PwdSalt);
                if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(passwordKey), h.PwdVerifier))
                {
                    var dataKey = UnwrapKey(wrapPwd, HkdfDerive(passwordKey, WrapPwdInfo));
                    if (dataKey != null)
                    {
                        CryptoUtil.Zero(passwordKey); CryptoUtil.Zero(wrapPwd); CryptoUtil.Zero(wrapRec);
                        return dataKey;
                    }
                }
                CryptoUtil.Zero(passwordKey);
            }

            if (!string.IsNullOrEmpty(recoveryCode))
            {
                byte[] recoveryKey = PasswordHasher.DeriveKey(NormalizeRecoveryCode(recoveryCode), h.RecSalt);
                if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(recoveryKey), h.RecVerifier))
                {
                    var dataKey = UnwrapKey(wrapRec, HkdfDerive(recoveryKey, WrapRecInfo));
                    if (dataKey != null)
                    {
                        CryptoUtil.Zero(recoveryKey); CryptoUtil.Zero(wrapPwd); CryptoUtil.Zero(wrapRec);
                        return dataKey;
                    }
                }
                CryptoUtil.Zero(recoveryKey);
            }

            CryptoUtil.Zero(wrapPwd);
            CryptoUtil.Zero(wrapRec);
            throw new CryptographicException("密码或恢复码错误，无法解密。");
        }

        /// <summary>分块流式解密 ver=3 密文并写入目标流。</summary>
        private static void DecryptChunks(Stream src, Stream dst, byte[] dataKey, IProgress<int>? progress, long total)
        {
            var lenBuf = new byte[4];
            using var aes = new AesGcm(dataKey, TagSize);
            long done = 0;
            while (true)
            {
                int r = src.Read(lenBuf, 0, 4);
                if (r == 0) break;                    // 正常结束
                if (r < 4) throw new CryptographicException("文件已损坏：块长度字段不完整。");
                int chunkLen = BitConverter.ToInt32(lenBuf, 0);
                if (chunkLen <= 0 || chunkLen > ChunkSize)
                    throw new CryptographicException("文件已损坏：非法块长度。");

                var nonce = new byte[NonceSize]; src.ReadExactly(nonce);
                var ciphertext = new byte[chunkLen]; src.ReadExactly(ciphertext);
                var tag = new byte[TagSize]; src.ReadExactly(tag);

                var plain = new byte[chunkLen];
                aes.Decrypt(nonce, ciphertext, tag, plain);
                dst.Write(plain, 0, plain.Length);
                Array.Clear(plain, 0, plain.Length);

                done += chunkLen;
                if (progress != null && total > 0)
                    progress.Report((int)(done * 100 / total));
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

        /// <summary>
        /// 加密（锁定）文件夹：递归加密文件夹内所有文件 + 放置隐藏标记 + 权限封锁。
        /// 加密后内容真正加密（正在被其他程序占用的文件会提示关闭后重试），
        /// 并禁止浏览/进入、禁止拖入未加密内容。
        /// </summary>
        public static string EncryptFolder(string folderPath, string password, IProgress<int>? progress = null)
        {
            if (!Directory.Exists(folderPath))
                throw new DirectoryNotFoundException("文件夹不存在。");
            if (IsFolderEncrypted(folderPath))
                throw new InvalidOperationException("该文件夹已加密。");

            string recoveryCode = GenerateRecoveryCode();
            string normalizedRec = NormalizeRecoveryCode(recoveryCode);

            // 递归加密文件夹内所有文件（真正的内容加密）
            EncryptFilesRecursive(folderPath, password, normalizedRec, progress);

            // 放置隐藏标记（含密码/恢复码验证）
            string markerPath = Path.Combine(folderPath, FolderLockFileName);
            byte[] marker = BuildPayload(Array.Empty<byte>(), password, recoveryCode);
            File.WriteAllBytes(markerPath, marker);
            File.SetAttributes(markerPath, FileAttributes.Hidden | FileAttributes.System);

            // 用权限封锁：禁止浏览/进入 + 禁止创建文件/子文件夹（阻止拖入）
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

        /// <summary>
        /// 解密（解锁）文件夹：递归解密文件夹内所有文件 + 删除标记 + 解除权限封锁。
        /// 密码或恢复码任一正确即可。
        /// </summary>
        public static void DecryptFolder(string folderPath, string? password = null, string? recoveryCode = null, IProgress<int>? progress = null)
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

                // 递归解密所有已加密文件（旧式仅 ACL 锁定的文件夹内没有加密文件，自动跳过）
                DecryptFilesRecursive(folderPath, password, recoveryCode, progress);

                File.Delete(markerPath);
            }
            catch
            {
                // 密码/恢复码错误或解密失败：重新锁回去，避免错误输入导致解锁。
                if (wasLocked) LockFolderAcl(folderPath);
                throw;
            }
        }

        /// <summary>递归加密文件夹内所有文件（跳过已加密与标记文件），并把每个文件的内部进度映射到文件夹整体进度。</summary>
        private static void EncryptFilesRecursive(string folderPath, string password, string recoveryCode, IProgress<int>? progress)
        {
            string[] files;
            try { files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories); }
            catch { files = Array.Empty<string>(); }

            for (int i = 0; i < files.Length; i++)
            {
                string f = files[i];
                if (Path.GetFileName(f) == FolderLockFileName) continue;
                if (IsFileEncrypted(f)) continue;

                try
                {
                    if (progress != null && files.Length > 0)
                    {
                        // 把当前文件的内部进度(0-100)映射到文件夹整体进度，避免第一个大文件期间一直 0%
                        EncryptFileWithRecovery(f, password, recoveryCode,
                            new FolderProgress(progress, i, files.Length));
                    }
                    else
                    {
                        EncryptFileWithRecovery(f, password, recoveryCode);
                    }
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && ex is not FileNotFoundException)
                {
                    throw new IOException($"加密失败：文件正被其他程序占用或无法访问：{f}\n请关闭正在使用的文件后重试。", ex);
                }
            }
        }

        /// <summary>递归解密文件夹内所有已加密文件（跳过标记文件与未加密文件），并把每个文件的内部进度映射到文件夹整体进度。</summary>
        private static void DecryptFilesRecursive(string folderPath, string? password, string? recoveryCode, IProgress<int>? progress)
        {
            string[] files;
            try { files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories); }
            catch { files = Array.Empty<string>(); }

            for (int i = 0; i < files.Length; i++)
            {
                string f = files[i];
                if (Path.GetFileName(f) == FolderLockFileName) continue;
                if (!IsFileEncrypted(f)) continue;

                try
                {
                    if (progress != null && files.Length > 0)
                    {
                        DecryptFile(f, password, recoveryCode, new FolderProgress(progress, i, files.Length));
                    }
                    else
                    {
                        DecryptFile(f, password, recoveryCode);
                    }
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && ex is not FileNotFoundException)
                {
                    throw new IOException($"解密失败：文件正被其他程序占用或无法访问：{f}\n请关闭正在使用的文件后重试。", ex);
                }
            }
        }

        /// <summary>把某个文件的内部进度(0-100)映射为文件夹整体进度，保证进度条随文件内部进度实时前进。</summary>
        private sealed class FolderProgress : IProgress<int>
        {
            private readonly IProgress<int> _inner;
            private readonly int _index;   // 当前文件序号（0-based）
            private readonly int _total;   // 文件总数

            public FolderProgress(IProgress<int> inner, int index, int total)
            {
                _inner = inner;
                _index = index;
                _total = total;
            }

            public void Report(int value)
                => _inner.Report((int)((_index + value / 100.0) / _total * 100));
        }

        /// <summary>
        /// 用权限封锁文件夹：拒绝当前用户浏览/进入（双击 → 访问被拒绝），
        /// 同时拒绝创建文件与子文件夹，防止把未加密的文件/文件夹拖入已加密文件夹。
        /// 仅封锁这些权限，解锁时移除对应 Deny 即可由所有者正常恢复。
        /// </summary>
        public static void LockFolderAcl(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return;
            var id = WindowsIdentity.GetCurrent().User;
            if (id == null) return;

            var di = new DirectoryInfo(folderPath);
            var sec = di.GetAccessControl(AccessControlSections.Access);

            // 先移除当前用户既有的拒绝规则，避免重复累积
            foreach (var rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule is FileSystemAccessRule fr &&
                    fr.IdentityReference.Equals(id) &&
                    fr.AccessControlType == AccessControlType.Deny)
                {
                    sec.RemoveAccessRule(fr);
                }
            }

            var deny = new FileSystemAccessRule(
                id,
                // 禁止浏览/遍历（无法进入、列出内容），
                // 禁止创建文件(CreateFiles=WriteData)与子文件夹(CreateDirectories=AppendData)，
                // 从而无法把其他文件/文件夹拖入或粘贴进已加密文件夹。
                FileSystemRights.ListDirectory | FileSystemRights.Traverse |
                FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
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
            head[o++] = LegacyVersion;   // 标记保持旧版单块格式（体积极小，无内存问题）
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
            if (ver != Version && ver != LegacyVersion)
                throw new InvalidDataException($"不支持的版本: {ver}");

            var h = new HeaderInfo { Version = (byte)ver };
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
            public byte Version;
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
