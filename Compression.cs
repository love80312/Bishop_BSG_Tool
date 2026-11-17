using System;
using System.IO;

namespace Bishop_BSG_Tool
{
    /// <summary>
    /// BSS-Graphics RLE codec.
    /// BPP8 stores one RLE stream. BPP32 stores four independent byte-channel
    /// streams (BGRA), each prefixed by its own Int32 compressed-byte length.
    /// </summary>
    internal static class Compression
    {
        private const int MaxLiteralLength = 127;
        private const int MaxRunLength = 128;

        public static void RleCompress(BinaryWriter writer, byte[] data, int pixelSize)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (pixelSize <= 0) throw new ArgumentOutOfRangeException(nameof(pixelSize));
            if (data.Length % pixelSize != 0)
                throw new ArgumentException("Pixel data length must be divisible by pixel size.", nameof(data));

            int sampleCount = data.Length / pixelSize;

            // The original BSG format stores each byte component as a separate
            // stream. For BPP32, the four streams represent B, G, R and A.
            for (int channel = 0; channel < pixelSize; channel++)
            {
                using var encodedStream = new MemoryStream();

                int index = 0;
                while (index < sampleCount)
                {
                    int runLength = CountRun(data, index, channel, pixelSize, sampleCount);

                    if (runLength >= 2)
                    {
                        int count = Math.Min(runLength, MaxRunLength);

                        // A negative signed-byte control means repeat the next
                        // byte (1 - control) times. The stored byte is two's complement.
                        encodedStream.WriteByte(unchecked((byte)(1 - count)));
                        encodedStream.WriteByte(data[(index * pixelSize) + channel]);
                        index += count;
                        continue;
                    }

                    int literalStart = index;
                    index++;

                    // Literal controls are 0..126 and therefore carry 1..127 bytes.
                    while (index < sampleCount && (index - literalStart) < MaxLiteralLength)
                    {
                        if (CountRun(data, index, channel, pixelSize, sampleCount) >= 2)
                            break;

                        index++;
                    }

                    int literalCount = index - literalStart;
                    encodedStream.WriteByte((byte)(literalCount - 1));

                    for (int i = 0; i < literalCount; i++)
                    {
                        encodedStream.WriteByte(data[((literalStart + i) * pixelSize) + channel]);
                    }
                }

                if (encodedStream.Length > int.MaxValue)
                    throw new InvalidDataException("Compressed RLE stream is too large.");

                writer.Write((int)encodedStream.Length);
                encodedStream.Position = 0;
                encodedStream.CopyTo(writer.BaseStream);
            }
        }

        /// <summary>
        /// Decodes one or more channel streams. pixelSize=1 is BPP8;
        /// pixelSize=4 is BPP32 (four separately compressed channels).
        /// </summary>
        public static void RleUncompress(byte[] input, byte[] output, int dst, int pixelSize)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (pixelSize <= 0) throw new ArgumentOutOfRangeException(nameof(pixelSize));
            if (dst < 0 || dst > output.Length)
                throw new ArgumentOutOfRangeException(nameof(dst));

            int availableBytes = output.Length - dst;
            if (availableBytes % pixelSize != 0)
                throw new ArgumentException("Output length must be divisible by pixel size.", nameof(output));

            int sampleCount = availableBytes / pixelSize;

            using var inputStream = new MemoryStream(input, writable: false);
            using var reader = new BinaryReader(inputStream);

            for (int channel = 0; channel < pixelSize; channel++)
            {
                if (inputStream.Length - inputStream.Position < sizeof(int))
                    throw new InvalidDataException($"RLE channel {channel + 1} is missing its length header.");

                int encodedLength = reader.ReadInt32();
                if (encodedLength < 0 || encodedLength > inputStream.Length - inputStream.Position)
                    throw new InvalidDataException($"RLE channel {channel + 1} has an invalid compressed length.");

                long channelEnd = inputStream.Position + encodedLength;
                int samplesWritten = 0;

                while (inputStream.Position < channelEnd)
                {
                    int controlByte = reader.ReadByte();
                    sbyte control = unchecked((sbyte)controlByte);

                    if (control >= 0)
                    {
                        int count = control + 1;
                        EnsureCanWrite(samplesWritten, count, sampleCount, channel);
                        EnsureChannelBytesAvailable(inputStream, channelEnd, count, channel);

                        for (int i = 0; i < count; i++)
                        {
                            int outputIndex = dst + ((samplesWritten + i) * pixelSize) + channel;
                            output[outputIndex] = reader.ReadByte();
                        }

                        samplesWritten += count;
                    }
                    else
                    {
                        int count = 1 - control;
                        EnsureCanWrite(samplesWritten, count, sampleCount, channel);
                        EnsureChannelBytesAvailable(inputStream, channelEnd, 1, channel);

                        byte value = reader.ReadByte();
                        for (int i = 0; i < count; i++)
                        {
                            int outputIndex = dst + ((samplesWritten + i) * pixelSize) + channel;
                            output[outputIndex] = value;
                        }

                        samplesWritten += count;
                    }
                }

                if (inputStream.Position != channelEnd)
                    throw new InvalidDataException($"RLE channel {channel + 1} exceeded its declared length.");

                if (samplesWritten != sampleCount)
                {
                    throw new InvalidDataException(
                        $"RLE channel {channel + 1} decoded {samplesWritten} samples; expected {sampleCount}.");
                }
            }

            if (inputStream.Position != inputStream.Length)
                throw new InvalidDataException("The RLE data contains unexpected trailing bytes.");
        }

        private static int CountRun(byte[] data, int sampleIndex, int channel, int pixelSize, int sampleCount)
        {
            byte value = data[(sampleIndex * pixelSize) + channel];
            int count = 1;
            int max = Math.Min(MaxRunLength, sampleCount - sampleIndex);

            while (count < max && data[((sampleIndex + count) * pixelSize) + channel] == value)
                count++;

            return count;
        }

        private static void EnsureCanWrite(int samplesWritten, int count, int sampleCount, int channel)
        {
            if (count <= 0 || samplesWritten > sampleCount - count)
                throw new InvalidDataException($"RLE channel {channel + 1} expands beyond the expected pixel count.");
        }

        private static void EnsureChannelBytesAvailable(
            MemoryStream inputStream,
            long channelEnd,
            int requiredBytes,
            int channel)
        {
            if (requiredBytes < 0 || channelEnd - inputStream.Position < requiredBytes)
                throw new InvalidDataException($"RLE channel {channel + 1} is truncated.");
        }
    }
}
