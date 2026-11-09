using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                RunInteractive();
                return 0;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "encrypt":
                    EncryptFile(args);
                    break;
                case "decrypt":
                    DecryptFile(args);
                    break;
                case "benchmark":
                    RunBenchmark(args);
                    break;
                case "self-test":
                    RunSelfTests();
                    break;
                case "help":
                case "--help":
                case "-h":
                    PrintUsage();
                    break;
                default:
                    throw new ArgumentException($"未知指令：{args[0]}");
            }

            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidDataException or CryptographicException or
            OverflowException)
        {
            Console.Error.WriteLine($"錯誤：{exception.Message}");
            return 1;
        }
    }

    private static void RunInteractive()
    {
        Console.WriteLine("文件對稱加密工具");
        Console.WriteLine("1. 加密文件");
        Console.WriteLine("2. 解密文件");
        string operation = PromptChoice("請選擇操作 [1/2]：", new[] { "1", "2" });
        string inputPath = PromptPath("請輸入文件路徑（也可將文件拖入此視窗）：");
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("找不到輸入文件", inputPath);
        }

        string outputDirectory = PromptPath("請輸入輸出目錄路徑：");
        if (!Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException($"找不到輸出目錄：{outputDirectory}");
        }

        string outputPath = GetOutputPath(inputPath, outputDirectory, operation == "1");
        Console.WriteLine($"輸出檔案：{outputPath}");

        if (operation == "1")
        {
            string[] modes = { "ECB", "CBC", "CFB", "OFB", "CTR" };
            Console.WriteLine("加密模式：1=ECB、2=CBC、3=CFB、4=OFB、5=CTR");
            string mode = modes[int.Parse(PromptChoice(
                "請選擇模式 [1-5，預設 2=CBC]：", new[] { "1", "2", "3", "4", "5" }, "2")) - 1];
            string blockBitsText = PromptChoice(
                "區塊大小 [16/64 位元，預設 64]：", new[] { "16", "64" }, "64");
            EncryptFile(new[] { "encrypt", inputPath, outputPath, "--mode", mode, "--block-size", blockBitsText });
        }
        else
        {
            DecryptFile(new[] { "decrypt", inputPath, outputPath });
        }
    }

    private static string PromptChoice(string prompt, string[] allowedValues, string? defaultValue = null)
    {
        while (true)
        {
            Console.Write(prompt);
            string? value = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(value) && defaultValue is not null)
            {
                return defaultValue;
            }

            if (value is not null && allowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return value;
            }

            Console.WriteLine($"請輸入以下選項：{string.Join("、", allowedValues)}");
        }
    }

    private static string PromptPath(string prompt)
    {
        Console.Write(prompt);
        string? value = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("文件路徑不可為空");
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return value;
    }

    private static string GetOutputPath(string inputPath, string outputDirectory, bool encrypt)
    {
        string fileName = Path.GetFileName(inputPath);
        if (encrypt)
        {
            fileName += ".sed";
        }
        else if (fileName.EndsWith(".sed", StringComparison.OrdinalIgnoreCase))
        {
            string originalName = fileName[..^4];
            string extension = Path.GetExtension(originalName);
            string nameWithoutExtension = Path.GetFileNameWithoutExtension(originalName);
            fileName = $"{nameWithoutExtension}.restored{extension}";
        }
        else
        {
            fileName += ".restored";
        }

        return Path.Combine(outputDirectory, fileName);
    }

    private static void EncryptFile(string[] args)
    {
        if (args.Length < 3)
        {
            throw new ArgumentException("用法：encrypt <輸入檔> <輸出檔> [--mode ECB|CBC|CFB|OFB|CTR] [--block-size 16|64]");
        }

        string mode = "CBC";
        int blockBits = 64;
        for (int index = 3; index < args.Length; index++)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"選項缺少值：{args[index]}");
            }

            string value = args[++index];
            switch (args[index - 1])
            {
                case "--mode":
                    mode = value.ToUpperInvariant();
                    break;
                case "--block-size":
                    if (!int.TryParse(value, out blockBits))
                    {
                        throw new ArgumentException("區塊大小必須是 16 或 64 位元");
                    }
                    break;
                default:
                    throw new ArgumentException($"未知選項：{args[index - 1]}");
            }
        }

        if (mode is not ("ECB" or "CBC" or "CFB" or "OFB" or "CTR"))
        {
            throw new ArgumentException("模式必須是 ECB、CBC、CFB、OFB 或 CTR");
        }

        if (blockBits is not (16 or 64))
        {
            throw new ArgumentException("區塊大小必須是 16 或 64 位元");
        }

        ValidatePaths(args[1], args[2]);
        byte[] source = File.ReadAllBytes(args[1]);
        string password = ReadPassword("設定加密密碼：");
        string confirmation = ReadPassword("再次輸入密碼：");
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            throw new ArgumentException("兩次輸入的密碼不一致");
        }

        byte[] encrypted = FileCipher.Encrypt(source, password, mode, blockBits);
        WriteNewFile(args[2], encrypted);
        Console.WriteLine($"加密完成：{args[2]}");
    }

    private static void DecryptFile(string[] args)
    {
        if (args.Length != 3)
        {
            throw new ArgumentException("用法：decrypt <加密檔> <輸出檔>");
        }

        ValidatePaths(args[1], args[2]);
        byte[] envelope = File.ReadAllBytes(args[1]);
        string password = ReadPassword("輸入解密密碼：");
        byte[] plaintext = FileCipher.Decrypt(envelope, password);
        WriteNewFile(args[2], plaintext);
        Console.WriteLine($"解密完成：{args[2]}");
    }

    private static void RunBenchmark(string[] args)
    {
        int sizeMiB = 1;
        if (args.Length == 3 && args[1] == "--size-mib" &&
            int.TryParse(args[2], out int parsedSize))
        {
            sizeMiB = parsedSize;
        }
        else if (args.Length != 1)
        {
            throw new ArgumentException("用法：benchmark [--size-mib 正整數]");
        }

        if (sizeMiB < 1)
        {
            throw new ArgumentException("測試大小至少為 1 MiB");
        }

        byte[] data = new byte[checked(sizeMiB * 1024 * 1024)];
        for (int index = 0; index < data.Length; index++)
        {
            data[index] = (byte)index;
        }

        const string password = "educational-benchmark";
        Console.WriteLine($"測試資料：{sizeMiB} MiB（包含 PBKDF2 密碼衍生時間）");
        Console.WriteLine($"{"區塊",8} {"模式",6} {"測試MiB",10} {"秒數",10} {"MiB/s",10}");
        foreach (int blockBits in new[] { 16, 64 })
        {
            foreach (string mode in new[] { "ECB", "CBC", "CFB", "OFB", "CTR" })
            {
                int sampleLength = data.Length;
                if (mode == "CTR" && blockBits == 16)
                {
                    sampleLength = Math.Min(sampleLength, (1 << 16) * (blockBits / 8));
                }

                byte[] sample = data.AsSpan(0, sampleLength).ToArray();
                Stopwatch stopwatch = Stopwatch.StartNew();
                byte[] encrypted = FileCipher.Encrypt(sample, password, mode, blockBits);
                stopwatch.Stop();
                double seconds = stopwatch.Elapsed.TotalSeconds;
                double sampleMiB = sampleLength / (1024d * 1024d);
                double throughput = sampleMiB / seconds;
                Console.WriteLine($"{blockBits + "-bit",8} {mode,6} {sampleMiB,10:F3} {seconds,10:F3} {throughput,10:F2}");

                if (!FileCipher.Decrypt(encrypted, password).AsSpan().SequenceEqual(sample))
                {
                    throw new InvalidDataException("效能測試的解密結果不符");
                }
            }
        }
    }

    private static void RunSelfTests()
    {
        byte[][] samples =
        [
            [],
            Encoding.UTF8.GetBytes("hello"),
            Enumerable.Range(0, 768).Select(index => (byte)index).ToArray()
        ];

        foreach (string mode in new[] { "ECB", "CBC", "CFB", "OFB", "CTR" })
        {
            foreach (int blockBits in new[] { 16, 64 })
            {
                foreach (byte[] sample in samples)
                {
                    byte[] encrypted = FileCipher.Encrypt(sample, "correct horse battery staple", mode, blockBits);
                    byte[] decrypted = FileCipher.Decrypt(encrypted, "correct horse battery staple");
                    Assert(decrypted.AsSpan().SequenceEqual(sample), $"{mode}, {blockBits}-bit round trip");
                }
            }
        }

        byte[] protectedData = FileCipher.Encrypt(
            Encoding.UTF8.GetBytes("document contents"), "right password", "CBC", 64);
        AssertThrows(
            () => FileCipher.Decrypt(protectedData, "wrong password"),
            "wrong password rejection");
        protectedData[^1] ^= 1;
        AssertThrows(
            () => FileCipher.Decrypt(protectedData, "right password"),
            "tampering rejection");
        AssertThrows(
            () => FileCipher.Encrypt([], "password", "XYZ", 64),
            "invalid mode rejection");
        AssertThrows(
            () => FileCipher.Encrypt([], "password", "CBC", 32),
            "invalid block-size rejection");
        AssertThrows(
            () => FileCipher.Encrypt(new byte[128 * 1024 + 1], "password", "CTR", 16),
            "16-bit CTR counter limit");

        byte[] legacyEnvelope = Convert.FromHexString(
            "5345444601024008000102030405060708090a0b0c0d0e0f0000000000000018" +
            "1011121314151617fd35d84e7c53aef3d6257e7e4b7b7b5701101517c779f69b" +
            "c24961cfffece807fbdb6e8abd33519fc0576ee1ebddc597ae3e7fe0633f9484");
        byte[] compatibilityPlaintext = Encoding.UTF8.GetBytes("compatibility test");
        byte[] fixedSalt = Enumerable.Range(0, 16).Select(index => (byte)index).ToArray();
        byte[] fixedIv = Enumerable.Range(16, 8).Select(index => (byte)index).ToArray();
        byte[] csharpEnvelope = FileCipher.EncryptForTest(
            compatibilityPlaintext, "compatibility password", "CBC", 64, fixedSalt, fixedIv);
        Assert(csharpEnvelope.AsSpan().SequenceEqual(legacyEnvelope), "Python/C# file format compatibility");
        Assert(
            FileCipher.Decrypt(legacyEnvelope, "compatibility password")
                .AsSpan().SequenceEqual(compatibilityPlaintext),
            "Python encrypted file decryption");
        Assert(
            GetOutputPath(@"C:\input\report.docx", @"D:\output", encrypt: true) ==
                Path.Combine(@"D:\output", "report.docx.sed"),
            "automatic encrypted filename");
        Assert(
            GetOutputPath(@"C:\input\report.docx.sed", @"D:\output", encrypt: false) ==
                Path.Combine(@"D:\output", "report.restored.docx"),
            "automatic decrypted filename");

        string temporaryPath = Path.Combine(
            Path.GetTempPath(), $"file-encryptor-self-test-{Guid.NewGuid():N}.sed");
        try
        {
            WriteNewFile(temporaryPath, protectedData);
            AssertThrows(() => WriteNewFile(temporaryPath, protectedData), "existing output refusal");
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        Console.WriteLine("自我測試通過：30 組模式／區塊大小／資料往返、錯誤密碼、竄改偵測、舊版相容性及檔案輸出保護。");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidDataException($"自我測試失敗：{name}");
        }
    }

    private static void AssertThrows(Action action, string name)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (InvalidDataException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        throw new InvalidDataException($"自我測試失敗：{name}");
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            string? redirectedPassword = Console.ReadLine();
            if (string.IsNullOrEmpty(redirectedPassword))
            {
                throw new ArgumentException("密碼不可為空");
            }

            return redirectedPassword;
        }

        StringBuilder password = new();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }

        if (password.Length == 0)
        {
            throw new ArgumentException("密碼不可為空");
        }

        return password.ToString();
    }

    private static void ValidatePaths(string source, string destination)
    {
        string fullSource = Path.GetFullPath(source);
        string fullDestination = Path.GetFullPath(destination);
        if (string.Equals(fullSource, fullDestination, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("輸入與輸出不可為同一個檔案");
        }

        if (File.Exists(fullDestination))
        {
            throw new IOException($"輸出檔已存在，為避免覆寫而停止：{destination}");
        }
    }

    private static void WriteNewFile(string path, byte[] contents)
    {
        bool created = false;
        try
        {
            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            if (created)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception cleanupException) when (
                    cleanupException is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"警告：無法移除未完成的輸出檔：{path}（{cleanupException.Message}）");
                }
            }

            throw;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            文件對稱加密課堂示範（自製演算法，非正式安全用途）

            用法：
              dotnet run -- encrypt <輸入檔> <輸出檔> [--mode ECB|CBC|CFB|OFB|CTR] [--block-size 16|64]
              dotnet run -- decrypt <加密檔> <輸出檔>
              dotnet run -- benchmark [--size-mib 正整數]
              dotnet run -- self-test

            不帶參數啟動時會開啟互動選單，可依提示選擇操作與檔案。
            加密預設為 CBC、64 位元區塊；解密時會自動讀取檔案中的參數。
            """
        );
    }
}

internal static class FileCipher
{
    private const int HeaderSize = 32;
    private const int SaltSize = 16;
    private const int TagSize = 32;
    private const int Rounds = 8;
    private const int KdfIterations = 200_000;
    private const byte Version = 1;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SEDF");
    private static readonly byte[] SBox =
    [
        0xC, 0x5, 0x6, 0xB, 0x9, 0x0, 0xA, 0xD,
        0x3, 0xE, 0xF, 0x8, 0x4, 0x7, 0x1, 0x2
    ];

    internal static byte[] Encrypt(byte[] data, string password, string mode, int blockBits)
    {
        mode = mode.ToUpperInvariant();
        ValidateParameters(password, mode, blockBits);

        int blockSize = blockBits / 8;
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] iv = mode == "ECB" ? new byte[blockSize] : RandomNumberGenerator.GetBytes(blockSize);
        return EncryptWithParameters(data, password, mode, blockBits, salt, iv);
    }

    internal static byte[] EncryptForTest(
        byte[] data, string password, string mode, int blockBits, byte[] salt, byte[] iv)
    {
        mode = mode.ToUpperInvariant();
        ValidateParameters(password, mode, blockBits);
        int blockSize = blockBits / 8;
        if (salt.Length != SaltSize || iv.Length != blockSize)
        {
            throw new ArgumentException("測試用 salt 或 IV 長度錯誤");
        }

        return EncryptWithParameters(data, password, mode, blockBits, salt, iv);
    }

    private static byte[] EncryptWithParameters(
        byte[] data, string password, string mode, int blockBits, byte[] salt, byte[] iv)
    {
        int blockSize = blockBits / 8;
        (uint[] roundKeys, byte[] macKey) = DeriveKeys(password, salt, blockSize);
        bool usesPadding = mode is "ECB" or "CBC";
        byte[] input = usesPadding ? Pad(data, blockSize) : data;
        ValidateCounterCapacity(input.Length, blockSize, mode);
        byte[] ciphertext = CryptBlocks(input, roundKeys, blockSize, mode, iv, decrypt: false);

        byte[] authenticatedData = new byte[HeaderSize + blockSize + ciphertext.Length];
        Magic.CopyTo(authenticatedData, 0);
        authenticatedData[4] = Version;
        authenticatedData[5] = ModeToId(mode);
        authenticatedData[6] = (byte)blockBits;
        authenticatedData[7] = Rounds;
        salt.CopyTo(authenticatedData, 8);
        BinaryPrimitives.WriteUInt64BigEndian(authenticatedData.AsSpan(24, 8), (ulong)ciphertext.Length);
        iv.CopyTo(authenticatedData, HeaderSize);
        ciphertext.CopyTo(authenticatedData, HeaderSize + blockSize);

        byte[] tag = HMACSHA256.HashData(macKey, authenticatedData);
        byte[] envelope = new byte[authenticatedData.Length + tag.Length];
        authenticatedData.CopyTo(envelope, 0);
        tag.CopyTo(envelope, authenticatedData.Length);
        return envelope;
    }

    internal static byte[] Decrypt(byte[] envelope, string password)
    {
        if (envelope.Length < HeaderSize + TagSize)
        {
            throw new InvalidDataException("檔案太短或格式不正確");
        }

        if (!envelope.AsSpan(0, Magic.Length).SequenceEqual(Magic) || envelope[4] != Version)
        {
            throw new InvalidDataException("不是支援的加密文件格式");
        }

        byte modeId = envelope[5];
        int blockBits = envelope[6];
        int rounds = envelope[7];
        if (modeId is < 1 or > 5 || blockBits is not (16 or 64) || rounds != Rounds)
        {
            throw new InvalidDataException("加密文件包含不支援的參數");
        }

        string mode = IdToMode(modeId);
        ValidateParameters(password, mode, blockBits);
        int blockSize = blockBits / 8;
        ulong ciphertextLength = BinaryPrimitives.ReadUInt64BigEndian(envelope.AsSpan(24, 8));
        bool usesPadding = mode is "ECB" or "CBC";
        if (ciphertextLength > int.MaxValue ||
            (usesPadding && (ciphertextLength == 0 || ciphertextLength % (uint)blockSize != 0)))
        {
            throw new InvalidDataException("加密文件長度不正確");
        }
        ValidateCounterCapacity((int)ciphertextLength, blockSize, mode);

        long expectedLength = (long)HeaderSize + blockSize + (long)ciphertextLength + TagSize;
        if (envelope.Length != expectedLength)
        {
            throw new InvalidDataException("加密文件長度不正確");
        }

        int ciphertextSize = (int)ciphertextLength;
        int authenticatedLength = HeaderSize + blockSize + ciphertextSize;
        byte[] salt = envelope.AsSpan(8, SaltSize).ToArray();
        (uint[] roundKeys, byte[] macKey) = DeriveKeys(password, salt, blockSize);
        byte[] expectedTag = HMACSHA256.HashData(macKey, envelope.AsSpan(0, authenticatedLength));
        if (!CryptographicOperations.FixedTimeEquals(
                expectedTag, envelope.AsSpan(authenticatedLength, TagSize)))
        {
            throw new InvalidDataException("密碼錯誤或加密文件已遭修改");
        }

        byte[] iv = envelope.AsSpan(HeaderSize, blockSize).ToArray();
        byte[] ciphertext = envelope.AsSpan(HeaderSize + blockSize, ciphertextSize).ToArray();
        byte[] plaintext = CryptBlocks(ciphertext, roundKeys, blockSize, mode, iv, decrypt: true);
        return usesPadding ? Unpad(plaintext, blockSize) : plaintext;
    }

    private static void ValidateParameters(string password, string mode, int blockBits)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("密碼不可為空");
        }

        if (mode is not ("ECB" or "CBC" or "CFB" or "OFB" or "CTR"))
        {
            throw new ArgumentException("模式必須是 ECB、CBC、CFB、OFB 或 CTR");
        }

        if (blockBits is not (16 or 64))
        {
            throw new ArgumentException("區塊大小必須是 16 或 64 位元");
        }
    }

    private static (uint[] RoundKeys, byte[] MacKey) DeriveKeys(
        string password, byte[] salt, int blockSize)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        byte[] masterKey = Rfc2898DeriveBytes.Pbkdf2(
            passwordBytes, salt, KdfIterations, HashAlgorithmName.SHA256, blockSize);
        CryptographicOperations.ZeroMemory(passwordBytes);

        int halfSize = blockSize / 2;
        uint[] roundKeys = new uint[Rounds];
        for (int index = 0; index < Rounds; index++)
        {
            byte[] message = [.. Encoding.ASCII.GetBytes("round-key"), (byte)index];
            byte[] roundKey = HMACSHA256.HashData(masterKey, message);
            roundKeys[index] = ReadBigEndian(roundKey.AsSpan(0, halfSize));
        }

        byte[] macKey = HMACSHA256.HashData(
            masterKey, Encoding.ASCII.GetBytes("file-authentication"));
        CryptographicOperations.ZeroMemory(masterKey);
        return (roundKeys, macKey);
    }

    private static byte[] CryptBlocks(
        byte[] data, uint[] roundKeys, int blockSize, string mode, byte[] iv, bool decrypt)
    {
        byte[] output = new byte[data.Length];
        byte[] previous = (byte[])iv.Clone();
        for (int offset = 0; offset < data.Length; offset += blockSize)
        {
            int segmentSize = Math.Min(blockSize, data.Length - offset);
            ReadOnlySpan<byte> block = data.AsSpan(offset, segmentSize);
            Span<byte> outputBlock = output.AsSpan(offset, segmentSize);
            if (mode == "CBC" && decrypt)
            {
                byte[] transformed = DecryptBlock(block, roundKeys);
                for (int index = 0; index < blockSize; index++)
                {
                    outputBlock[index] = (byte)(transformed[index] ^ previous[index]);
                    previous[index] = block[index];
                }
            }
            else if (mode == "CBC")
            {
                byte[] mixed = new byte[blockSize];
                for (int index = 0; index < blockSize; index++)
                {
                    mixed[index] = (byte)(block[index] ^ previous[index]);
                }

                byte[] encrypted = EncryptBlock(mixed, roundKeys);
                encrypted.CopyTo(outputBlock);
                encrypted.CopyTo(previous, 0);
            }
            else if (mode == "ECB" && decrypt)
            {
                DecryptBlock(block, roundKeys).CopyTo(outputBlock);
            }
            else if (mode == "CFB")
            {
                byte[] keyStream = EncryptBlock(previous, roundKeys);
                for (int index = 0; index < segmentSize; index++)
                {
                    outputBlock[index] = (byte)(block[index] ^ keyStream[index]);
                }

                if (segmentSize == blockSize)
                {
                    (decrypt ? block : outputBlock).CopyTo(previous);
                }
            }
            else if (mode == "OFB")
            {
                previous = EncryptBlock(previous, roundKeys);
                for (int index = 0; index < segmentSize; index++)
                {
                    outputBlock[index] = (byte)(block[index] ^ previous[index]);
                }
            }
            else if (mode == "CTR")
            {
                byte[] keyStream = EncryptBlock(previous, roundKeys);
                for (int index = 0; index < segmentSize; index++)
                {
                    outputBlock[index] = (byte)(block[index] ^ keyStream[index]);
                }

                if (offset + segmentSize < data.Length)
                {
                    IncrementCounter(previous);
                }
            }
            else
            {
                EncryptBlock(block, roundKeys).CopyTo(outputBlock);
            }
        }

        return output;
    }

    private static void ValidateCounterCapacity(int dataLength, int blockSize, string mode)
    {
        long blockCount = ((long)dataLength + blockSize - 1) / blockSize;
        if (mode == "CTR" && blockSize == 2 && blockCount > 1L << 16)
        {
            throw new ArgumentException(
                "CTR 搭配 16 位元區塊最多可處理 65,536 個區塊（128 KiB）；請改用 64 位元區塊");
        }
    }

    private static void IncrementCounter(Span<byte> counter)
    {
        for (int index = counter.Length - 1; index >= 0; index--)
        {
            counter[index]++;
            if (counter[index] != 0)
            {
                return;
            }
        }
    }

    private static byte ModeToId(string mode) => mode switch
    {
        "ECB" => 1,
        "CBC" => 2,
        "CFB" => 3,
        "OFB" => 4,
        "CTR" => 5,
        _ => throw new ArgumentException($"不支援的模式：{mode}")
    };

    private static string IdToMode(byte modeId) => modeId switch
    {
        1 => "ECB",
        2 => "CBC",
        3 => "CFB",
        4 => "OFB",
        5 => "CTR",
        _ => throw new InvalidDataException("加密文件包含不支援的操作模式")
    };

    private static byte[] EncryptBlock(ReadOnlySpan<byte> block, uint[] roundKeys)
    {
        int halfSize = block.Length / 2;
        int width = halfSize * 8;
        uint left = ReadBigEndian(block[..halfSize]);
        uint right = ReadBigEndian(block[halfSize..]);
        for (int index = 0; index < roundKeys.Length; index++)
        {
            (left, right) = (right, left ^ RoundFunction(right, width, roundKeys[index], index));
        }

        byte[] result = new byte[block.Length];
        WriteBigEndian(left, result.AsSpan(0, halfSize));
        WriteBigEndian(right, result.AsSpan(halfSize));
        return result;
    }

    private static byte[] DecryptBlock(ReadOnlySpan<byte> block, uint[] roundKeys)
    {
        int halfSize = block.Length / 2;
        int width = halfSize * 8;
        uint left = ReadBigEndian(block[..halfSize]);
        uint right = ReadBigEndian(block[halfSize..]);
        for (int index = roundKeys.Length - 1; index >= 0; index--)
        {
            (left, right) = (
                right ^ RoundFunction(left, width, roundKeys[index], index),
                left);
        }

        byte[] result = new byte[block.Length];
        WriteBigEndian(left, result.AsSpan(0, halfSize));
        WriteBigEndian(right, result.AsSpan(halfSize));
        return result;
    }

    private static uint RoundFunction(uint value, int width, uint roundKey, int roundIndex)
    {
        uint mask = width == 32 ? uint.MaxValue : (1u << width) - 1;
        value ^= roundKey;

        uint substituted = 0;
        for (int shift = 0; shift < width; shift += 4)
        {
            int nibble = (int)((value >> shift) & 0xF);
            substituted |= (uint)SBox[nibble] << shift;
        }

        uint permuted = 0;
        for (int bit = 0; bit < width; bit++)
        {
            if (((substituted >> bit) & 1) != 0)
            {
                permuted |= 1u << ((bit * 5) % width);
            }
        }

        int rotation = (roundIndex % width) + 1;
        uint rotated = ((permuted << rotation) | (permuted >> (width - rotation))) & mask;
        uint constant = unchecked((uint)((roundIndex + 1) * 0x9E3779B9u)) & mask;
        return unchecked(rotated + constant) & mask;
    }

    private static uint ReadBigEndian(ReadOnlySpan<byte> data)
    {
        uint value = 0;
        foreach (byte item in data)
        {
            value = (value << 8) | item;
        }
        return value;
    }

    private static void WriteBigEndian(uint value, Span<byte> destination)
    {
        for (int index = destination.Length - 1; index >= 0; index--)
        {
            destination[index] = (byte)value;
            value >>= 8;
        }
    }

    private static byte[] Pad(byte[] data, int blockSize)
    {
        int paddingSize = blockSize - data.Length % blockSize;
        byte[] padded = new byte[data.Length + paddingSize];
        data.CopyTo(padded, 0);
        padded.AsSpan(data.Length).Fill((byte)paddingSize);
        return padded;
    }

    private static byte[] Unpad(byte[] data, int blockSize)
    {
        if (data.Length == 0)
        {
            throw new InvalidDataException("密文內容為空");
        }

        int paddingSize = data[^1];
        if (paddingSize < 1 || paddingSize > blockSize ||
            !data.AsSpan(data.Length - paddingSize, paddingSize).SequenceEqual(
                Enumerable.Repeat((byte)paddingSize, paddingSize).ToArray()))
        {
            throw new InvalidDataException("填充格式錯誤，密碼可能不正確");
        }

        return data.AsSpan(0, data.Length - paddingSize).ToArray();
    }
}
