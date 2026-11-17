using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bishop_BSG_Tool
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 3)
            {
                PrintUsage();
                return 2;
            }

            string mode = args[0].Trim().ToLowerInvariant();
            string bppMode = args[1].Trim().ToLowerInvariant();

            if (mode != "-c" && mode != "-e")
            {
                Console.Error.WriteLine("錯誤：模式只能使用 -c（PNG 轉 BSG）或 -e（BSG 轉 PNG）。");
                PrintUsage();
                return 2;
            }

            if (bppMode != "-auto" && bppMode != "-bpp8" && bppMode != "-bpp32")
            {
                Console.Error.WriteLine("錯誤：格式只能使用 -auto、-bpp8 或 -bpp32。");
                PrintUsage();
                return 2;
            }

            string path;
            try
            {
                path = Path.GetFullPath(args[2]);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"路徑錯誤：{ex.Message}");
                return 2;
            }

            try
            {
                List<string> files = GetInputFiles(path, mode);
                if (files.Count == 0)
                {
                    Console.Error.WriteLine("找不到符合格式的輸入檔案。");
                    return 1;
                }

                int succeeded = 0;
                int failed = 0;

                foreach (string file in files)
                {
                    Console.WriteLine($"處理：{Path.GetFileName(file)}");

                    try
                    {
                        ProcessFile(mode, bppMode, file);
                        succeeded++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.Error.WriteLine($"失敗：{Path.GetFileName(file)}");
                        Console.Error.WriteLine($"原因：{ex.Message}");
                    }
                }

                Console.WriteLine();
                Console.WriteLine($"完成。成功：{succeeded}，失敗：{failed}，總數：{files.Count}");
                return failed == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"執行失敗：{ex.Message}");
                return 1;
            }
        }

        private static List<string> GetInputFiles(string path, string mode)
        {
            string expectedExtension = mode == "-c" ? ".png" : ".bsg";

            if (Directory.Exists(path))
            {
                return Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                    .Where(file => string.Equals(Path.GetExtension(file), expectedExtension, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (!File.Exists(path))
                throw new FileNotFoundException("找不到指定的檔案或資料夾。", path);

            if (!string.Equals(Path.GetExtension(path), expectedExtension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"此模式只接受 {expectedExtension} 檔案。");

            return new List<string> { path };
        }

        private static void ProcessFile(string mode, string bppMode, string sourcePath)
        {
            string outputPath = Path.ChangeExtension(
                sourcePath,
                mode == "-c" ? ".bsg" : ".png");

            if (mode == "-c")
            {
                switch (bppMode)
                {
                    case "-auto":
                        BSG.CreateAuto(outputPath, sourcePath);
                        break;
                    case "-bpp8":
                        BSG.CreateBpp8(outputPath, sourcePath);
                        break;
                    case "-bpp32":
                        BSG.Create(outputPath, sourcePath);
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported BPP mode.");
                }
            }
            else
            {
                switch (bppMode)
                {
                    case "-auto":
                        int detectedBpp = BSG.GetBpp(sourcePath);
                        Console.WriteLine($"標頭辨識格式：BPP{detectedBpp}");
                        BSG.ExtractAuto(outputPath, sourcePath);
                        break;
                    case "-bpp8":
                        BSG.ExtractBpp8(outputPath, sourcePath);
                        break;
                    case "-bpp32":
                        BSG.ExtractBpp32(outputPath, sourcePath);
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported BPP mode.");
                }
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Bishop BSG Tool");
            Console.WriteLine("用法：");
            Console.WriteLine("  PNG 轉 BSG：Bishop_BSG_Tool -c [-auto|-bpp8|-bpp32] [image.png|folder]");
            Console.WriteLine("  BSG 轉 PNG：Bishop_BSG_Tool -e [-auto|-bpp8|-bpp32] [image.bsg|folder]");
            Console.WriteLine();
            Console.WriteLine("格式判斷：");
            Console.WriteLine("  -auto   自動判斷格式（建議）");
            Console.WriteLine("  -bpp8   強制使用 BPP8");
            Console.WriteLine("  -bpp32  強制使用 BPP32");
            Console.WriteLine();
            Console.WriteLine("自動判斷規則：");
            Console.WriteLine("  BSG 解包：讀取 BSG 標頭 0x30 位移的 BPP 類型欄位。");
            Console.WriteLine("  PNG 打包：8-bit 灰階 PNG 判定為 BPP8；其他圖片嘗試以 BPP32 處理。");
            Console.WriteLine("  資料夾模式只處理該資料夾第一層的 PNG 或 BSG 檔案。");
        }
    }
}
