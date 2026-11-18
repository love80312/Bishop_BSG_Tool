using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Bishop_BSG_Tool
{
    /// <summary>
    /// Bishop BSA archive reader/writer.
    /// Supports reading BSA v1/v2/v3 and writing archives in the version
    /// recorded by the extraction manifest. Without a manifest, v3 is used.
    /// File payloads are copied byte-for-byte; no compression or text conversion
    /// is performed.
    /// </summary>
    internal static class BSA
    {
        private const int HeaderSize = 0x10;
        private const int V1IndexRecordSize = 0x28;
        private const int V2IndexRecordSize = 0x0C;
        private const string ManifestFileName = ".bsa_manifest.json";
        private static readonly byte[] Signature = Encoding.ASCII.GetBytes("BSArc");
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static readonly Encoding Cp932 = CreateCp932();

        private static Encoding CreateCp932()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(
                932,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }

        private enum RecordType
        {
            File,
            DirectoryStart,
            DirectoryEnd
        }

        private sealed class ArchiveRecord
        {
            public RecordType Type { get; init; }
            public string Name { get; init; } = string.Empty;
            public byte[] RawName { get; init; } = Array.Empty<byte>();
            public string? RelativePath { get; init; }
            public uint Offset { get; init; }
            public uint Size { get; init; }
        }

        private sealed class ParsedArchive
        {
            public ushort Version { get; init; }
            public ushort RecordCount { get; init; }
            public uint IndexOffset { get; init; }
            public byte[] Header { get; init; } = Array.Empty<byte>();
            public List<ArchiveRecord> Records { get; init; } = new List<ArchiveRecord>();
            public List<ArchiveRecord> Files => Records.Where(x => x.Type == RecordType.File).ToList();
        }

        private sealed class Manifest
        {
            public int ManifestVersion { get; set; } = 1;
            public int ArchiveVersion { get; set; }
            public string HeaderBase64 { get; set; } = string.Empty;
            public string OriginalArchiveName { get; set; } = string.Empty;
            public List<ManifestRecord> Records { get; set; } = new List<ManifestRecord>();
        }

        private sealed class ManifestRecord
        {
            public string Type { get; set; } = string.Empty;
            public string? Path { get; set; }
            public string? Name { get; set; }
            public string? RawNameBase64 { get; set; }
            public uint OriginalOffset { get; set; }
            public uint OriginalSize { get; set; }
            public string? Sha256 { get; set; }
        }

        private sealed class FileOnDisk
        {
            public string RelativePath { get; init; } = string.Empty;
            public string FullPath { get; init; } = string.Empty;
            public long Length { get; init; }
        }

        private sealed class WriteRecord
        {
            public RecordType Type { get; init; }
            public byte[] RawName { get; init; } = Array.Empty<byte>();
            public string? RelativePath { get; init; }
            public string? FullPath { get; init; }
            public long InputLength { get; init; }
            public uint Offset { get; set; }
            public uint Size { get; set; }
        }

        /// <summary>
        /// Extracts a BSA archive into a new or empty directory and writes a
        /// manifest that preserves the original version and index ordering.
        /// </summary>
        public static void Extract(string archivePath, string outputDirectory)
        {
            archivePath = Path.GetFullPath(archivePath);
            outputDirectory = Path.GetFullPath(outputDirectory);

            if (!File.Exists(archivePath))
                throw new FileNotFoundException("找不到指定的 BSA 檔案。", archivePath);

            if (Directory.Exists(outputDirectory))
            {
                if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
                    throw new IOException("解包目標資料夾必須不存在或是空資料夾，避免覆蓋既有檔案。");
            }
            else if (File.Exists(outputDirectory))
            {
                throw new IOException("解包目標路徑已存在同名檔案。");
            }

            ParsedArchive archive = ReadArchive(archivePath);
            Directory.CreateDirectory(outputDirectory);

            var manifest = new Manifest
            {
                ManifestVersion = 1,
                ArchiveVersion = archive.Version,
                HeaderBase64 = Convert.ToBase64String(archive.Header),
                OriginalArchiveName = Path.GetFileName(archivePath)
            };

            try
            {
                foreach (ArchiveRecord record in archive.Records)
                {
                    switch (record.Type)
                    {
                        case RecordType.DirectoryStart:
                        {
                            string relativePath = RequireRelativePath(record.RelativePath, "BSA 目錄名稱");
                            string fullPath = GetSafeFullPath(outputDirectory, relativePath);
                            Directory.CreateDirectory(fullPath);
                            manifest.Records.Add(new ManifestRecord
                            {
                                Type = "DirectoryStart",
                                Path = relativePath,
                                Name = record.Name,
                                RawNameBase64 = Convert.ToBase64String(record.RawName)
                            });
                            Console.WriteLine($"目錄：{relativePath}");
                            break;
                        }

                        case RecordType.DirectoryEnd:
                            manifest.Records.Add(new ManifestRecord
                            {
                                Type = "DirectoryEnd",
                                Name = record.Name,
                                RawNameBase64 = Convert.ToBase64String(record.RawName)
                            });
                            break;

                        case RecordType.File:
                        {
                            string relativePath = RequireRelativePath(record.RelativePath, "BSA 檔名");
                            string fullPath = GetSafeFullPath(outputDirectory, relativePath);
                            string? parent = Path.GetDirectoryName(fullPath);
                            if (!string.IsNullOrEmpty(parent))
                                Directory.CreateDirectory(parent);

                            if (File.Exists(fullPath) || Directory.Exists(fullPath))
                                throw new InvalidDataException($"封包內有重複或衝突的路徑：{relativePath}");

                            string hash = CopyArchiveRangeToFile(archivePath, record.Offset, record.Size, fullPath);
                            manifest.Records.Add(new ManifestRecord
                            {
                                Type = "File",
                                Path = relativePath,
                                Name = record.Name,
                                RawNameBase64 = Convert.ToBase64String(record.RawName),
                                OriginalOffset = record.Offset,
                                OriginalSize = record.Size,
                                Sha256 = hash
                            });
                            Console.WriteLine($"解包：{relativePath} ({record.Size:N0} bytes)");
                            break;
                        }
                    }
                }

                string manifestPath = Path.Combine(outputDirectory, ManifestFileName);
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
                using (var stream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(json, 0, json.Length);

                try
                {
                    File.SetAttributes(manifestPath, FileAttributes.Hidden);
                }
                catch
                {
                    // Hidden attributes are cosmetic; the manifest remains usable without them.
                }

                Console.WriteLine();
                Console.WriteLine($"BSA 解包完成：版本 v{archive.Version}，檔案 {archive.Files.Count:N0} 個，索引記錄 {archive.RecordCount:N0} 筆。");
                Console.WriteLine($"中繼資料：{ManifestFileName}（用於保留原始版本及索引順序）");
            }
            catch
            {
                // Do not leave a seemingly complete extraction after a failure.
                try { Directory.Delete(outputDirectory, recursive: true); } catch { }
                throw;
            }
        }

        /// <summary>
        /// Packs a directory. If an extraction manifest exists, the original
        /// BSA version and record order are retained. Otherwise v3 is written.
        /// The temporary archive is parsed and every payload SHA-256 is checked
        /// before it replaces the requested output file.
        /// </summary>
        public static void Pack(string inputDirectory, string archivePath)
        {
            inputDirectory = Path.GetFullPath(inputDirectory);
            archivePath = Path.GetFullPath(archivePath);

            if (!Directory.Exists(inputDirectory))
                throw new DirectoryNotFoundException("找不到要打包的資料夾：" + inputDirectory);

            string? outputParent = Path.GetDirectoryName(archivePath);
            if (string.IsNullOrEmpty(outputParent))
                throw new InvalidOperationException("輸出 BSA 路徑無效。");
            Directory.CreateDirectory(outputParent);

            if (Directory.Exists(archivePath))
                throw new IOException("輸出 BSA 路徑目前是資料夾。");

            string normalizedRoot = inputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (archivePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetDirectoryName(archivePath), inputDirectory, StringComparison.OrdinalIgnoreCase))
                throw new IOException("輸出 BSA 請放在來源資料夾外，或直接放在來源資料夾根目錄；不要放在其子資料夾內。");

            string tempArchive = archivePath + ".tmp-" + Guid.NewGuid().ToString("N");
            string manifestPath = Path.Combine(inputDirectory, ManifestFileName);
            Manifest? manifest = null;
            ushort version = 3;
            byte[] header = CreateDefaultHeader();
            var inputFiles = CollectInputFiles(inputDirectory, archivePath, tempArchive);

            if (File.Exists(manifestPath))
            {
                manifest = LoadManifest(manifestPath);
                if (manifest.ManifestVersion != 1)
                    throw new InvalidDataException($"不支援的 BSA 中繼資料版本：{manifest.ManifestVersion}");
                if (manifest.ArchiveVersion < 1 || manifest.ArchiveVersion > 3)
                    throw new InvalidDataException($"中繼資料中的 BSA 版本無效：{manifest.ArchiveVersion}");

                version = checked((ushort)manifest.ArchiveVersion);
                try
                {
                    header = Convert.FromBase64String(manifest.HeaderBase64);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException("BSA 中繼資料的 HeaderBase64 無效。", ex);
                }

                if (header.Length != HeaderSize || !header.Take(Signature.Length).SequenceEqual(Signature))
                    throw new InvalidDataException("BSA 中繼資料中的原始標頭無效。");
            }

            List<WriteRecord> records = manifest == null
                ? BuildRecordsForNewArchive(inputDirectory, inputFiles)
                : BuildRecordsFromManifest(inputDirectory, inputFiles, manifest);

            if (records.Count == 0)
                throw new InvalidDataException("沒有可寫入的檔案或目錄記錄，無法建立有效的 BSA。");

            // GARbro reads the count as a signed Int16, so keep the count
            // within its positive range for maximum compatibility.
            if (records.Count > short.MaxValue)
                throw new InvalidDataException($"BSA 索引記錄超過 GARbro 相容上限 32767 筆：{records.Count:N0}");

            int originalFileCount = manifest?.Records.Count(x => string.Equals(x.Type, "File", StringComparison.OrdinalIgnoreCase)) ?? 0;
            var originalFilePaths = manifest == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(manifest.Records
                    .Where(x => string.Equals(x.Type, "File", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Path))
                    .Select(x => NormalizeRelativePath(x.Path!)), StringComparer.OrdinalIgnoreCase);
            int retainedOriginalCount = records.Count(x =>
                x.Type == RecordType.File && x.RelativePath != null && originalFilePaths.Contains(x.RelativePath));
            int addedCount = records.Count(x => x.Type == RecordType.File) - retainedOriginalCount;
            int removedCount = Math.Max(0, originalFileCount - retainedOriginalCount);

            try
            {
                WriteArchive(tempArchive, version, header, records);
                VerifyPackedArchive(tempArchive, version, records);

                // Replace the destination only after the temporary archive passed
                // structural parsing and byte-for-byte payload verification.
                File.Move(tempArchive, archivePath, overwrite: true);

                Console.WriteLine();
                Console.WriteLine($"BSA 打包完成：{Path.GetFileName(archivePath)}");
                Console.WriteLine($"格式：BSA v{version}；檔案：{records.Count(x => x.Type == RecordType.File):N0}；索引記錄：{records.Count:N0}");
                if (manifest != null)
                    Console.WriteLine($"沿用原始版本及索引順序；新增：{addedCount:N0}，移除：{removedCount:N0}");
                else
                    Console.WriteLine("未找到解包中繼資料，使用新建 BSA v3 格式。");
                Console.WriteLine("內部驗證：通過（可重新解析，所有檔案 SHA-256 相符）。");
            }
            finally
            {
                try { if (File.Exists(tempArchive)) File.Delete(tempArchive); } catch { }
            }
        }

        /// <summary>Validates the BSA index and payload ranges without extracting.</summary>
        public static void Verify(string archivePath)
        {
            archivePath = Path.GetFullPath(archivePath);
            ParsedArchive archive = ReadArchive(archivePath);
            foreach (ArchiveRecord entry in archive.Records)
            {
                if (entry.Type == RecordType.File)
                    _ = ComputeRangeSha256(archivePath, entry.Offset, entry.Size);
            }

            Console.WriteLine($"BSA 驗證通過：版本 v{archive.Version}，檔案 {archive.Files.Count:N0} 個，索引記錄 {archive.RecordCount:N0} 筆。");
        }

        private static Manifest LoadManifest(string manifestPath)
        {
            try
            {
                using FileStream stream = File.OpenRead(manifestPath);
                Manifest? manifest = JsonSerializer.Deserialize<Manifest>(stream, JsonOptions);
                if (manifest == null || manifest.Records == null)
                    throw new InvalidDataException("BSA 中繼資料內容不完整。");
                return manifest;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("BSA 中繼資料 JSON 格式錯誤。", ex);
            }
        }

        private static List<FileOnDisk> CollectInputFiles(string root, string archivePath, string tempArchive)
        {
            var result = new List<FileOnDisk>();
            var pathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string manifestFullPath = Path.GetFullPath(Path.Combine(root, ManifestFileName));
            string targetFullPath = Path.GetFullPath(archivePath);
            string tempFullPath = Path.GetFullPath(tempArchive);

            foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("不支援將符號連結或 Reparse Point 目錄加入 BSA：" + directory);
            }

            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string fullPath = Path.GetFullPath(file);
                if (string.Equals(fullPath, manifestFullPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fullPath, targetFullPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fullPath, tempFullPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                FileAttributes attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("不支援將符號連結或 Reparse Point 加入 BSA：" + fullPath);

                string relative = NormalizeRelativePath(Path.GetRelativePath(root, fullPath));
                relative = ValidateRelativePath(relative);
                if (!pathSet.Add(relative))
                    throw new InvalidDataException("資料夾內有不相容的重複路徑（Windows 不區分大小寫）：" + relative);

                long length = new FileInfo(fullPath).Length;
                if (length > uint.MaxValue)
                    throw new InvalidDataException($"單一檔案超過 BSA 32-bit 大小上限：{relative}");

                result.Add(new FileOnDisk
                {
                    RelativePath = relative,
                    FullPath = fullPath,
                    Length = length
                });
            }

            result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
            return result;
        }

        private static List<WriteRecord> BuildRecordsForNewArchive(string root, List<FileOnDisk> files)
        {
            var records = new List<WriteRecord>();
            foreach (FileOnDisk file in files)
                AppendNewFileWithDirectoryMarkers(records, file);

            foreach (string emptyDirectory in CollectEmptyDirectories(root))
                AppendNewEmptyDirectoryWithMarkers(records, emptyDirectory);

            return records;
        }

        private static List<WriteRecord> BuildRecordsFromManifest(
            string root,
            List<FileOnDisk> files,
            Manifest manifest)
        {
            var fileMap = files.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
            var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var records = new List<WriteRecord>();
            var directoryKept = new Stack<bool>();

            foreach (ManifestRecord item in manifest.Records)
            {
                if (string.Equals(item.Type, "DirectoryStart", StringComparison.OrdinalIgnoreCase))
                {
                    string directoryPath = RequireRelativePath(item.Path, "中繼資料目錄路徑");
                    string fullDirectoryPath = GetSafeFullPath(root, directoryPath);
                    bool keep = Directory.Exists(fullDirectoryPath);
                    directoryKept.Push(keep);

                    if (keep)
                    {
                        byte[] nameBytes = GetManifestNameBytes(item, item.Name ?? string.Empty);
                        ValidateArchiveNameBytes(nameBytes, "目錄名稱");
                        records.Add(new WriteRecord
                        {
                            Type = RecordType.DirectoryStart,
                            RawName = nameBytes
                        });
                    }
                }
                else if (string.Equals(item.Type, "DirectoryEnd", StringComparison.OrdinalIgnoreCase))
                {
                    if (directoryKept.Count > 0 && directoryKept.Pop())
                    {
                        byte[] nameBytes = GetManifestNameBytes(item, "<");
                        ValidateArchiveNameBytes(nameBytes, "目錄結束標記");
                        records.Add(new WriteRecord
                        {
                            Type = RecordType.DirectoryEnd,
                            RawName = nameBytes
                        });
                    }
                }
                else if (string.Equals(item.Type, "File", StringComparison.OrdinalIgnoreCase))
                {
                    string relativePath = RequireRelativePath(item.Path, "中繼資料檔案路徑");
                    if (!fileMap.TryGetValue(relativePath, out FileOnDisk? file))
                        continue; // Removed by the user.
                    if (!usedFiles.Add(relativePath))
                        throw new InvalidDataException("中繼資料中有重複檔案記錄：" + relativePath);

                    byte[] nameBytes = GetManifestNameBytes(item, item.Name ?? Path.GetFileName(relativePath));
                    ValidateArchiveNameBytes(nameBytes, "檔案名稱");
                    records.Add(new WriteRecord
                    {
                        Type = RecordType.File,
                        RawName = nameBytes,
                        RelativePath = relativePath,
                        FullPath = file.FullPath,
                        InputLength = file.Length
                    });
                }
                else
                {
                    throw new InvalidDataException("BSA 中繼資料包含未知記錄類型：" + item.Type);
                }
            }

            // Close any directory markers left open by a source archive. This
            // keeps subsequent newly-added files rooted at the archive root.
            while (directoryKept.Count > 0)
            {
                if (directoryKept.Pop())
                    records.Add(new WriteRecord { Type = RecordType.DirectoryEnd, RawName = new byte[] { (byte)'<' } });
            }

            // New files are appended after the original record sequence. Their
            // directory markers are self-contained, so existing ordering is kept.
            foreach (FileOnDisk file in files)
            {
                if (!usedFiles.Contains(file.RelativePath))
                    AppendNewFileWithDirectoryMarkers(records, file);
            }

            var originalDirectoryPaths = new HashSet<string>(
                manifest.Records
                    .Where(x => string.Equals(x.Type, "DirectoryStart", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Path))
                    .Select(x => NormalizeRelativePath(x.Path!)),
                StringComparer.OrdinalIgnoreCase);

            foreach (string emptyDirectory in CollectEmptyDirectories(root))
            {
                if (!originalDirectoryPaths.Contains(emptyDirectory))
                    AppendNewEmptyDirectoryWithMarkers(records, emptyDirectory);
            }

            return records;
        }

        private static List<string> CollectEmptyDirectories(string root)
        {
            var emptyDirectories = new List<string>();
            foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                FileAttributes attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("不支援將符號連結或 Reparse Point 目錄加入 BSA：" + directory);

                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    string relative = ValidateRelativePath(NormalizeRelativePath(Path.GetRelativePath(root, directory)));
                    emptyDirectories.Add(relative);
                }
            }

            emptyDirectories.Sort(StringComparer.OrdinalIgnoreCase);
            return emptyDirectories;
        }

        private static void AppendNewEmptyDirectoryWithMarkers(List<WriteRecord> records, string relativePath)
        {
            string[] parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                records.Add(new WriteRecord
                {
                    Type = RecordType.DirectoryStart,
                    RawName = EncodeArchiveName(">" + part)
                });
            }
            for (int i = 0; i < parts.Length; i++)
            {
                records.Add(new WriteRecord
                {
                    Type = RecordType.DirectoryEnd,
                    RawName = new byte[] { (byte)'<' }
                });
            }
        }

        private static void AppendNewFileWithDirectoryMarkers(List<WriteRecord> records, FileOnDisk file)
        {
            string[] parts = file.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                throw new InvalidDataException("無效的相對檔案路徑：" + file.RelativePath);

            for (int i = 0; i < parts.Length - 1; i++)
            {
                byte[] directoryName = EncodeArchiveName(">" + parts[i]);
                records.Add(new WriteRecord
                {
                    Type = RecordType.DirectoryStart,
                    RawName = directoryName
                });
            }

            records.Add(new WriteRecord
            {
                Type = RecordType.File,
                RawName = EncodeArchiveName(parts[^1]),
                RelativePath = file.RelativePath,
                FullPath = file.FullPath,
                InputLength = file.Length
            });

            for (int i = parts.Length - 2; i >= 0; i--)
            {
                records.Add(new WriteRecord
                {
                    Type = RecordType.DirectoryEnd,
                    RawName = new byte[] { (byte)'<' }
                });
            }
        }

        private static byte[] GetManifestNameBytes(ManifestRecord record, string fallbackName)
        {
            if (string.IsNullOrWhiteSpace(record.RawNameBase64))
                return EncodeArchiveName(fallbackName);

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(record.RawNameBase64);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("中繼資料中的 RawNameBase64 無效。", ex);
            }

            string decoded;
            try
            {
                decoded = Cp932.GetString(bytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("中繼資料中的原始檔名字節不是有效 CP932。", ex);
            }

            if (record.Name != null && !string.Equals(decoded, record.Name, StringComparison.Ordinal))
                throw new InvalidDataException("中繼資料中的檔名文字與原始 CP932 位元組不一致。");

            return bytes;
        }

        private static byte[] EncodeArchiveName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf('\0') >= 0)
                throw new InvalidDataException("BSA 檔名不可為空，也不可包含 NUL 字元。");

            try
            {
                return Cp932.GetBytes(name);
            }
            catch (EncoderFallbackException ex)
            {
                throw new InvalidDataException(
                    $"檔名無法使用 CP932（日文 Shift-JIS）表示，請先重新命名：{name}", ex);
            }
        }

        private static void ValidateArchiveNameBytes(byte[] nameBytes, string label)
        {
            if (nameBytes.Length == 0 || nameBytes.Contains((byte)0))
                throw new InvalidDataException($"BSA {label}為空或包含 NUL 位元組。");
            try
            {
                _ = Cp932.GetString(nameBytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException($"BSA {label}不是有效 CP932。", ex);
            }
        }

        private static void WriteArchive(
            string outputPath,
            ushort version,
            byte[] originalHeader,
            List<WriteRecord> records)
        {
            if (version < 1 || version > 3)
                throw new InvalidDataException("只支援建立 BSA v1、v2 或 v3。");

            using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(new byte[HeaderSize]);

            foreach (WriteRecord record in records)
            {
                if (record.Type != RecordType.File)
                    continue;

                if (record.FullPath == null || !File.Exists(record.FullPath))
                    throw new FileNotFoundException("找不到待打包的檔案。", record.FullPath);

                var infoBefore = new FileInfo(record.FullPath);
                long originalLength = record.InputLength;
                if (infoBefore.Length != originalLength)
                    throw new IOException("檔案在掃描與打包之間大小發生變化：" + record.RelativePath);
                if (originalLength > uint.MaxValue)
                    throw new InvalidDataException("單一檔案超過 BSA 32-bit 大小上限：" + record.RelativePath);

                long position = stream.Position;
                if (position > uint.MaxValue)
                    throw new InvalidDataException("BSA 資料偏移超過 32-bit 上限。");

                record.Offset = checked((uint)position);
                record.Size = checked((uint)originalLength);

                using (var input = new FileStream(record.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length != originalLength)
                        throw new IOException("檔案在打包期間大小發生變化：" + record.RelativePath);
                    CopyExactly(input, stream, originalLength, record.RelativePath!);
                }

                if (new FileInfo(record.FullPath).Length != originalLength)
                    throw new IOException("檔案在打包期間大小發生變化：" + record.RelativePath);
            }

            long indexPosition = stream.Position;
            if (indexPosition > uint.MaxValue)
                throw new InvalidDataException("BSA 索引偏移超過 32-bit 上限。");

            if (version == 1)
            {
                foreach (WriteRecord record in records)
                {
                    byte[] fixedName = new byte[0x20];
                    if (record.RawName.Length > fixedName.Length)
                        throw new InvalidDataException(
                            $"BSA v1 檔名最多只能使用 32 個 CP932 位元組，目前 {record.RawName.Length} bytes：{Cp932.GetString(record.RawName)}");
                    Buffer.BlockCopy(record.RawName, 0, fixedName, 0, record.RawName.Length);
                    writer.Write(fixedName);
                    writer.Write(record.Type == RecordType.File ? record.Offset : 0u);
                    writer.Write(record.Type == RecordType.File ? record.Size : 0u);
                }
            }
            else
            {
                using var namesStream = new MemoryStream();
                using var namesWriter = new BinaryWriter(namesStream, Encoding.UTF8, leaveOpen: true);
                foreach (WriteRecord record in records)
                {
                    if (namesStream.Length > int.MaxValue)
                        throw new InvalidDataException("BSA 檔名資料表超過 32-bit 偏移上限。");
                    int nameOffset = checked((int)namesStream.Position);
                    writer.Write(nameOffset);
                    writer.Write(record.Type == RecordType.File ? record.Offset : 0u);
                    writer.Write(record.Type == RecordType.File ? record.Size : 0u);
                    namesWriter.Write(record.RawName);
                    namesWriter.Write((byte)0);
                }
                namesWriter.Flush();
                namesStream.Position = 0;
                namesStream.CopyTo(stream);
            }

            if (stream.Length > uint.MaxValue)
                throw new InvalidDataException("BSA 檔案總長度超過 32-bit 位址上限。");

            byte[] header = originalHeader.Length == HeaderSize
                ? (byte[])originalHeader.Clone()
                : CreateDefaultHeader();
            if (!header.Take(Signature.Length).SequenceEqual(Signature))
                throw new InvalidDataException("BSA 標頭簽章無效。");

            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8, 2), version);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10, 2), checked((ushort)records.Count));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), checked((uint)indexPosition));

            stream.Position = 0;
            writer.Write(header);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        private static void VerifyPackedArchive(string archivePath, ushort expectedVersion, List<WriteRecord> expectedRecords)
        {
            ParsedArchive actual = ReadArchive(archivePath);
            if (actual.Version != expectedVersion)
                throw new InvalidDataException("打包驗證失敗：輸出 BSA 版本與預期不一致。");

            List<WriteRecord> expectedFiles = expectedRecords.Where(x => x.Type == RecordType.File).ToList();
            List<ArchiveRecord> actualFiles = actual.Files;
            if (actualFiles.Count != expectedFiles.Count)
                throw new InvalidDataException(
                    $"打包驗證失敗：檔案數量不一致，預期 {expectedFiles.Count}，實際 {actualFiles.Count}。");

            if (actual.Records.Count != expectedRecords.Count)
                throw new InvalidDataException("打包驗證失敗：索引記錄數量不一致。");

            var expectedByPath = expectedFiles.ToDictionary(x => x.RelativePath!, StringComparer.OrdinalIgnoreCase);
            foreach (ArchiveRecord actualFile in actualFiles)
            {
                if (actualFile.RelativePath == null || !expectedByPath.TryGetValue(actualFile.RelativePath, out WriteRecord? expected))
                    throw new InvalidDataException("打包驗證失敗：輸出封包出現非預期檔案路徑：" + actualFile.RelativePath);

                string sourceHash = ComputeFileSha256(expected.FullPath!);
                string archiveHash = ComputeRangeSha256(archivePath, actualFile.Offset, actualFile.Size);
                if (!string.Equals(sourceHash, archiveHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("打包驗證失敗：檔案 SHA-256 不一致：" + actualFile.RelativePath);
            }
        }

        private static ParsedArchive ReadArchive(string archivePath)
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            if (stream.Length < HeaderSize)
                throw new InvalidDataException("檔案太小，無法包含完整的 BSA 標頭。");

            byte[] header = ReadExactly(reader, HeaderSize);
            if (!header.Take(Signature.Length).SequenceEqual(Signature) || header[5] != 0)
                throw new InvalidDataException("不是有效的 Bishop BSA 檔案（缺少 BSArc\0 簽章）。");

            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2));
            short signedCount = BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(10, 2));
            uint indexOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));

            if (version < 1 || version > 3)
                throw new NotSupportedException($"不支援的 BSA 版本：{version}（目前支援 v1～v3）。");
            if (signedCount <= 0)
                throw new InvalidDataException("BSA 索引記錄數量無效（必須為 1～32767）。");
            ushort count = checked((ushort)signedCount);
            if (indexOffset < HeaderSize || indexOffset >= stream.Length)
                throw new InvalidDataException("BSA 標頭中的索引偏移無效。");

            long indexBytes = (long)count * (version == 1 ? V1IndexRecordSize : V2IndexRecordSize);
            if (indexBytes > stream.Length - indexOffset)
                throw new InvalidDataException("BSA 索引資料超出檔案範圍。");

            byte[]? namesBuffer = null;
            long namesStart = (long)indexOffset + indexBytes;
            if (version > 1)
            {
                long namesLength = stream.Length - namesStart;
                if (namesLength <= 0 || namesLength > int.MaxValue)
                    throw new InvalidDataException("BSA 檔名資料表長度無效或超過支援範圍。");
                namesBuffer = new byte[(int)namesLength];
                stream.Position = namesStart;
                ReadExactly(reader, namesBuffer.Length).CopyTo(namesBuffer, 0);
            }

            var records = new List<ArchiveRecord>(count);
            var directoryStack = new List<string>();
            var pathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            stream.Position = indexOffset;

            for (int i = 0; i < count; i++)
            {
                byte[] rawName;
                uint offset;
                uint size;

                if (version == 1)
                {
                    byte[] fixedName = ReadExactly(reader, 0x20);
                    int terminator = Array.IndexOf(fixedName, (byte)0);
                    int nameLength = terminator >= 0 ? terminator : fixedName.Length;
                    rawName = fixedName.Take(nameLength).ToArray();
                    offset = reader.ReadUInt32();
                    size = reader.ReadUInt32();
                }
                else
                {
                    int nameOffset = reader.ReadInt32();
                    offset = reader.ReadUInt32();
                    size = reader.ReadUInt32();
                    if (nameOffset < 0 || nameOffset >= namesBuffer!.Length)
                        throw new InvalidDataException($"BSA 第 {i + 1} 筆索引的檔名偏移無效。");

                    int end = Array.IndexOf(namesBuffer, (byte)0, nameOffset);
                    if (end < 0)
                        throw new InvalidDataException($"BSA 第 {i + 1} 筆索引的檔名缺少 NUL 結尾。");
                    rawName = namesBuffer.AsSpan(nameOffset, end - nameOffset).ToArray();
                }

                if (rawName.Length == 0)
                    throw new InvalidDataException($"BSA 第 {i + 1} 筆索引的名稱為空。");

                string name;
                try
                {
                    name = Cp932.GetString(rawName);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new InvalidDataException($"BSA 第 {i + 1} 筆索引的檔名不是有效 CP932。", ex);
                }

                if (name.StartsWith(">", StringComparison.Ordinal))
                {
                    string directoryName = name.Substring(1);
                    if (string.IsNullOrWhiteSpace(directoryName))
                        throw new InvalidDataException($"BSA 第 {i + 1} 筆索引包含空目錄名稱。");
                    directoryStack.Add(directoryName);
                    string directoryPath = BuildArchiveRelativePath(directoryStack, string.Empty, allowEmptyLeaf: true);
                    records.Add(new ArchiveRecord
                    {
                        Type = RecordType.DirectoryStart,
                        Name = name,
                        RawName = rawName,
                        RelativePath = directoryPath
                    });
                }
                else if (name.StartsWith("<", StringComparison.Ordinal))
                {
                    records.Add(new ArchiveRecord
                    {
                        Type = RecordType.DirectoryEnd,
                        Name = name,
                        RawName = rawName
                    });
                    if (directoryStack.Count > 0)
                        directoryStack.RemoveAt(directoryStack.Count - 1);
                }
                else
                {
                    string relativePath = BuildArchiveRelativePath(directoryStack, name);
                    if (!pathSet.Add(relativePath))
                        throw new InvalidDataException("BSA 封包包含重複檔案路徑：" + relativePath);

                    long endOffset = (long)offset + size;
                    if ((size > 0 && offset < HeaderSize) || endOffset > indexOffset || endOffset > stream.Length)
                        throw new InvalidDataException($"BSA 檔案資料範圍無效：{relativePath}（offset=0x{offset:X}, size=0x{size:X}）。");

                    records.Add(new ArchiveRecord
                    {
                        Type = RecordType.File,
                        Name = name,
                        RawName = rawName,
                        RelativePath = relativePath,
                        Offset = offset,
                        Size = size
                    });
                }
            }

            return new ParsedArchive
            {
                Version = version,
                RecordCount = count,
                IndexOffset = indexOffset,
                Header = header,
                Records = records
            };
        }

        private static string BuildArchiveRelativePath(List<string> directories, string name, bool allowEmptyLeaf = false)
        {
            var parts = new List<string>();
            foreach (string directory in directories)
            {
                string normalizedDirectory = directory.Replace('\\', '/');
                parts.Add(normalizedDirectory);
            }
            if (!string.IsNullOrEmpty(name))
                parts.Add(name.Replace('\\', '/'));

            string combined = string.Join("/", parts);
            if (allowEmptyLeaf && combined.Length == 0)
                throw new InvalidDataException("BSA 包含無效的空目錄路徑。");
            return ValidateRelativePath(combined);
        }

        private static string NormalizeRelativePath(string path)
        {
            return path.Replace('\\', '/');
        }

        private static string RequireRelativePath(string? path, string label)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidDataException(label + "為空。");
            return ValidateRelativePath(NormalizeRelativePath(path));
        }

        private static string ValidateRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidDataException("BSA 路徑不可為空。");

            string normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                Path.IsPathRooted(normalized) ||
                (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
                throw new InvalidDataException("BSA 路徑不可為絕對路徑：" + path);

            string[] components = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (components.Length == 0)
                throw new InvalidDataException("BSA 路徑不可為空：" + path);

            foreach (string component in components)
            {
                if (component == "." || component == "..")
                    throw new InvalidDataException("BSA 路徑包含不允許的 . 或 ..：" + path);
                if (component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    component.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*' }) >= 0 ||
                    component.EndsWith(".", StringComparison.Ordinal) ||
                    component.EndsWith(" ", StringComparison.Ordinal))
                    throw new InvalidDataException("BSA 路徑包含 Windows 不允許的檔名字元：" + path);

                string deviceName = component.Split('.')[0].TrimEnd(' ', '.');
                if (IsReservedWindowsDeviceName(deviceName))
                    throw new InvalidDataException("BSA 路徑使用 Windows 保留裝置名稱，無法安全解包：" + path);
            }

            return string.Join(Path.DirectorySeparatorChar.ToString(), components);
        }

        private static bool IsReservedWindowsDeviceName(string name)
        {
            if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
                return true;

            if (name.Length == 4 &&
                (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                name[3] >= '1' && name[3] <= '9')
                return true;

            return false;
        }

        private static string GetSafeFullPath(string root, string relativePath)
        {
            string safeRelative = ValidateRelativePath(relativePath);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(Path.Combine(fullRoot, safeRelative));
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("BSA 路徑超出指定資料夾：" + relativePath);
            return fullPath;
        }

        private static byte[] ReadExactly(BinaryReader reader, int count)
        {
            byte[] data = reader.ReadBytes(count);
            if (data.Length != count)
                throw new EndOfStreamException("BSA 檔案內容不完整。");
            return data;
        }

        private static string CopyArchiveRangeToFile(string archivePath, uint offset, uint size, string outputPath)
        {
            using var source = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var destination = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            source.Position = offset;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                long remaining = size;
                while (remaining > 0)
                {
                    int requested = (int)Math.Min(buffer.Length, remaining);
                    int read = source.Read(buffer, 0, requested);
                    if (read <= 0)
                        throw new EndOfStreamException("BSA 檔案資料遭到截斷。");
                    destination.Write(buffer, 0, read);
                    hash.AppendData(buffer, 0, read);
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static void CopyExactly(Stream input, Stream output, long length, string relativePath)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                long remaining = length;
                while (remaining > 0)
                {
                    int requested = (int)Math.Min(buffer.Length, remaining);
                    int read = input.Read(buffer, 0, requested);
                    if (read <= 0)
                        throw new EndOfStreamException("打包期間來源檔案遭到截斷：" + relativePath);
                    output.Write(buffer, 0, read);
                    remaining -= read;
                }

                if (input.ReadByte() != -1)
                    throw new IOException("檔案在打包期間大小增加：" + relativePath);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static string ComputeFileSha256(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using SHA256 sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        private static string ComputeRangeSha256(string archivePath, uint offset, uint size)
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            stream.Position = offset;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                long remaining = size;
                while (remaining > 0)
                {
                    int requested = (int)Math.Min(buffer.Length, remaining);
                    int read = stream.Read(buffer, 0, requested);
                    if (read <= 0)
                        throw new EndOfStreamException("BSA 檔案資料遭到截斷。");
                    hash.AppendData(buffer, 0, read);
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static byte[] CreateDefaultHeader()
        {
            byte[] header = new byte[HeaderSize];
            Buffer.BlockCopy(Signature, 0, header, 0, Signature.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8, 2), 3);
            return header;
        }
    }
}
