
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Bishop_BSG_Tool
{
    internal static class BSG
    {
        private static readonly byte[] Signature =
            Encoding.ASCII.GetBytes("BSS-Graphics\x00\x00\x00\x00");

        private const int HeaderSize = 0x40;
        private const int BppTypeOffset = 0x30;
        private const byte Bpp32Type = 0;
        private const byte Bpp8Type = 2;

        private sealed class BsgHeader
        {
            public int Width { get; init; }
            public int Height { get; init; }
            public byte BppType { get; init; }
            public byte CompressionMethod { get; init; }
            public int DataPosition { get; init; }
            public int DataLength { get; init; }
        }

        /// <summary>
        /// Reads the BPP type from the BSG header: 8 or 32.
        /// </summary>
        public static int GetBpp(string sourcePath)
        {
            BsgHeader header = ReadHeader(sourcePath);
            return header.BppType == Bpp8Type ? 8 : 32;
        }

        /// <summary>
        /// Automatically selects BPP8 for an 8-bit grayscale PNG;
        /// other supported 32-bit PNG images are written as BPP32.
        /// </summary>
        public static void CreateAuto(string filePath, string sourcePath)
        {
            var sourceFormat = Image.DetectFormat(sourcePath);

            if (sourceFormat == null || sourceFormat.Name != "PNG")
            {
                throw new InvalidDataException(
                    "Automatic BPP detection only supports PNG files.");
            }

            var sourceInfo = Image.Identify(sourcePath);

            if (sourceInfo == null)
                throw new InvalidDataException("Unable to identify the source PNG image.");

            var pngMetadata = sourceInfo.Metadata.GetPngMetadata();

            if (pngMetadata.BitDepth == PngBitDepth.Bit8 &&
                pngMetadata.ColorType == PngColorType.Grayscale)
            {
                Console.WriteLine("Detected PNG format: 8-bit grayscale -> BPP8");
                CreateBpp8(filePath, sourcePath);
                return;
            }

            Console.WriteLine("Detected PNG format: trying BPP32");
            Create(filePath, sourcePath);
        }

        public static void Create(string filePath, string sourcePath)
        {
            using var source = Image.Load(sourcePath);

            if (source.PixelType.BitsPerPixel != 32)
            {
                throw new InvalidDataException(
                    "BPP32 conversion requires a 32-bit image (for example, an 8-bit-per-channel RGBA PNG).");
            }

            if (source.Width <= 0 ||
                source.Height <= 0 ||
                source.Width > ushort.MaxValue ||
                source.Height > ushort.MaxValue)
            {
                throw new InvalidDataException(
                    "Image dimensions are outside the supported BSG range (1..65535).");
            }

            long unpackedSizeLong = (long)source.Width * source.Height * 4;

            if (unpackedSizeLong > int.MaxValue)
                throw new InvalidDataException(
                    "Image pixel data is too large for the BSG format.");

            using var image = source.CloneAs<Bgra32>();
            image.Mutate(x => x.Flip(FlipMode.Vertical));

            int unpackedSize = (int)unpackedSizeLong;
            ushort imageWidth = checked((ushort)image.Width);
            ushort imageHeight = checked((ushort)image.Height);

            byte[] pixelData = new byte[unpackedSize];
            int offset = 0;

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    Span<Bgra32> row = accessor.GetRowSpan(y);

                    for (int x = 0; x < row.Length; x++)
                    {
                        uint packed = row[x].PackedValue;

                        pixelData[offset++] = (byte)packed;
                        pixelData[offset++] = (byte)(packed >> 8);
                        pixelData[offset++] = (byte)(packed >> 16);
                        pixelData[offset++] = (byte)(packed >> 24);
                    }
                }
            });

            using var stream = File.Create(filePath);
            using var writer = new BinaryWriter(stream);

            WriteHeader(
                writer,
                imageWidth,
                imageHeight,
                unpackedSize,
                Bpp32Type);

            Compression.RleCompress(writer, pixelData, 4);
            UpdateDataLength(writer, stream);
        }

        public static void CreateBpp8(string filePath, string sourcePath)
        {
            var sourceFormat = Image.DetectFormat(sourcePath);

            if (sourceFormat == null || sourceFormat.Name != "PNG")
            {
                throw new InvalidDataException(
                    "BPP8 conversion only supports PNG files.");
            }

            var sourceInfo = Image.Identify(sourcePath);

            if (sourceInfo == null)
                throw new InvalidDataException("Unable to identify the source PNG image.");

            var pngMetadata = sourceInfo.Metadata.GetPngMetadata();

            if (pngMetadata.BitDepth != PngBitDepth.Bit8 ||
                pngMetadata.ColorType != PngColorType.Grayscale)
            {
                throw new InvalidDataException(
                    "BPP8 conversion requires an 8-bit grayscale PNG.");
            }

            if (sourceInfo.Width <= 0 ||
                sourceInfo.Height <= 0 ||
                sourceInfo.Width > short.MaxValue ||
                sourceInfo.Height > short.MaxValue)
            {
                throw new InvalidDataException(
                    "BPP8 image dimensions must be between 1 and 32767.");
            }

            long bufferSizeLong = (long)sourceInfo.Width * sourceInfo.Height;

            if (bufferSizeLong > int.MaxValue)
                throw new InvalidDataException(
                    "Image pixel data is too large for the BSG format.");

            using var image = Image.Load<L8>(sourcePath);
            image.Mutate(x => x.Flip(FlipMode.Vertical));

            int bufferSize = (int)bufferSizeLong;
            byte[] pixels = new byte[bufferSize];
            int offset = 0;

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    Span<L8> row = accessor.GetRowSpan(y);

                    for (int x = 0; x < row.Length; x++)
                    {
                        pixels[offset++] = row[x].PackedValue;
                    }
                }
            });

            using var stream = File.Create(filePath);
            using var writer = new BinaryWriter(stream);

            WriteHeader(
                writer,
                checked((ushort)image.Width),
                checked((ushort)image.Height),
                bufferSize,
                Bpp8Type);

            Compression.RleCompress(writer, pixels, 1);
            UpdateDataLength(writer, stream);
        }

        public static void ExtractAuto(string filePath, string sourcePath)
        {
            ExtractCore(filePath, sourcePath, expectedBpp: 0);
        }

        public static void ExtractBpp8(string filePath, string sourcePath)
        {
            ExtractCore(filePath, sourcePath, expectedBpp: 8);
        }

        public static void ExtractBpp32(string filePath, string sourcePath)
        {
            ExtractCore(filePath, sourcePath, expectedBpp: 32);
        }

        private static void ExtractCore(
            string filePath,
            string sourcePath,
            int expectedBpp)
        {
            BsgHeader header = ReadHeader(sourcePath);
            int actualBpp = header.BppType == Bpp8Type ? 8 : 32;

            if (expectedBpp != 0 && expectedBpp != actualBpp)
            {
                throw new InvalidDataException(
                    $"The BSG header identifies this file as BPP{actualBpp}, not BPP{expectedBpp}.");
            }

            int bytesPerPixel = actualBpp == 8 ? 1 : 4;
            int expectedLength = checked(
                header.Width * header.Height * bytesPerPixel);

            byte[] encodedData;

            using (var stream = File.OpenRead(sourcePath))
            using (var reader = new BinaryReader(stream))
            {
                stream.Position = header.DataPosition;
                encodedData = reader.ReadBytes(header.DataLength);
            }

            if (encodedData.Length != header.DataLength)
                throw new InvalidDataException(
                    "The BSG image data is truncated.");

            byte[] pixels;

            switch (header.CompressionMethod)
            {
                case 0:
                    pixels = encodedData;

                    if (pixels.Length != expectedLength)
                    {
                        throw new InvalidDataException(
                            $"Uncompressed pixel data length is {pixels.Length}; expected {expectedLength}.");
                    }

                    break;

                case 1:
                    pixels = new byte[expectedLength];

                    Compression.RleUncompress(
                        encodedData,
                        pixels,
                        0,
                        bytesPerPixel);

                    break;

                default:
                    throw new NotSupportedException(
                        $"BSG compression method {header.CompressionMethod} is not supported. Supported methods are RAW (0) and RLE (1).");
            }

            if (actualBpp == 8)
            {
                using var image = Image.LoadPixelData<L8>(
                    pixels,
                    header.Width,
                    header.Height);

                image.Mutate(x => x.Flip(FlipMode.Vertical));

                image.SaveAsPng(filePath, new PngEncoder
                {
                    BitDepth = PngBitDepth.Bit8,
                    ColorType = PngColorType.Grayscale
                });
            }
            else
            {
                using var image = Image.LoadPixelData<Bgra32>(
                    pixels,
                    header.Width,
                    header.Height);

                image.Mutate(x => x.Flip(FlipMode.Vertical));

                image.SaveAsPng(filePath, new PngEncoder
                {
                    BitDepth = PngBitDepth.Bit8,
                    ColorType = PngColorType.RgbWithAlpha
                });
            }
        }

        private static BsgHeader ReadHeader(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException(
                    "A source file path is required.",
                    nameof(sourcePath));
            }

            using var stream = File.OpenRead(sourcePath);
            using var reader = new BinaryReader(stream);

            if (stream.Length < HeaderSize)
            {
                throw new InvalidDataException(
                    "The file is too small to contain a complete BSG header.");
            }

            byte[] signature = reader.ReadBytes(Signature.Length);

            if (!signature.SequenceEqual(Signature))
                throw new InvalidDataException(
                    "Not a valid BSS-Graphics BSG file.");

            stream.Position = 0x16;

            int width = reader.ReadUInt16();
            int height = reader.ReadUInt16();

            stream.Position = BppTypeOffset;

            byte bppType = reader.ReadByte();
            byte compressionMethod = reader.ReadByte();
            int dataPosition = reader.ReadInt32();
            int dataLength = reader.ReadInt32();

            if (width <= 0 || height <= 0)
                throw new InvalidDataException(
                    "The BSG header contains invalid image dimensions.");

            if (bppType != Bpp8Type && bppType != Bpp32Type)
            {
                throw new NotSupportedException(
                    $"Unsupported BSG BPP type: 0x{bppType:X2}. Expected 0x00 (BPP32) or 0x02 (BPP8).");
            }

            if (dataPosition < HeaderSize ||
                dataLength < 0 ||
                (long)dataPosition + dataLength > stream.Length)
            {
                throw new InvalidDataException(
                    "The BSG header contains an invalid pixel-data offset or length.");
            }

            if (compressionMethod != 0 && compressionMethod != 1)
            {
                throw new NotSupportedException(
                    $"BSG compression method {compressionMethod} is not supported.");
            }

            return new BsgHeader
            {
                Width = width,
                Height = height,
                BppType = bppType,
                CompressionMethod = compressionMethod,
                DataPosition = dataPosition,
                DataLength = dataLength
            };
        }

        private static void WriteHeader(
            BinaryWriter writer,
            ushort width,
            ushort height,
            int unpackedSize,
            byte bppType)
        {
            writer.Write(Signature);
            writer.WriteByte(0);
            writer.WriteByte(1);
            writer.WriteInt32(unpackedSize);
            writer.WriteUInt16(width);
            writer.WriteUInt16(height);
            writer.WriteInt32(0x10);
            writer.WriteUInt16(0);

            writer.WriteUInt16(0); // offset x
            writer.WriteUInt16(0); // offset y
            writer.WriteUInt16(0);
            writer.WriteUInt16(0);
            writer.WriteUInt16(width);
            writer.WriteUInt16(height);
            writer.WriteByte(0);
            writer.WriteByte(1);
            writer.WriteUInt16(0);

            writer.WriteByte(bppType);
            writer.WriteByte(1);
            writer.WriteInt32(HeaderSize);
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteByte(0);
            writer.WriteByte(
                bppType == Bpp32Type ? (byte)2 : (byte)0);

            if (writer.BaseStream.Position != HeaderSize)
            {
                throw new InvalidDataException(
                    "Internal error: BSG header size is not 0x40 bytes.");
            }
        }

        private static void UpdateDataLength(
            BinaryWriter writer,
            Stream stream)
        {
            long endPosition = stream.Position;
            long dataLength = endPosition - HeaderSize;

            if (dataLength < 0 || dataLength > int.MaxValue)
            {
                throw new InvalidDataException(
                    "Compressed BSG image data is too large.");
            }

            stream.Position = 0x36;
            writer.Write((int)dataLength);
            stream.Position = endPosition;
            writer.Flush();
        }
    }
}
