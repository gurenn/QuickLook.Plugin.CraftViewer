using System;
using System.IO;
using System.IO.Compression;

namespace ProbeCraftFormats
{
    /// <summary>
    /// 纯 BCL 最小 PNG 编码器（RGBA/8bit，无过滤器行）：
    /// 探针合成预览图与对抗样本用，不引入第三方库。
    /// </summary>
    internal static class MiniPng
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static uint[] _crcTable;

        /// <summary>生成完整合法 PNG（纯色 RGBA）。</summary>
        public static byte[] CreatePng(int width, int height, byte r, byte g, byte b)
        {
            using var ms = new MemoryStream();
            ms.Write(Signature, 0, Signature.Length);

            // IHDR
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, (uint)width);
            WriteBE(ihdr, 4, (uint)height);
            ihdr[8] = 8;   // bit depth
            ihdr[9] = 6;   // color type RGBA
            ihdr[10] = 0;  // compression
            ihdr[11] = 0;  // filter
            ihdr[12] = 0;  // interlace
            WriteChunk(ms, "IHDR", ihdr);

            // 原始扫描行（filter=0 + RGBA）
            int stride = 1 + width * 4;
            var raw = new byte[height * stride];
            for (int y = 0; y < height; y++)
            {
                int rowBase = y * stride;
                raw[rowBase] = 0; // filter none
                for (int x = 0; x < width; x++)
                {
                    int p = rowBase + 1 + x * 4;
                    raw[p] = r; raw[p + 1] = g; raw[p + 2] = b; raw[p + 3] = 255;
                }
            }

            WriteChunk(ms, "IDAT", ZlibCompress(raw));
            WriteChunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        /// <summary>
        /// 仅含签名 + 合法 IHDR 的"声明超大尺寸"PNG 头（A11）：
        /// 无 IDAT 数据——PngSniffer 只读头部即拒收，WPF 解码不会被触发。
        /// </summary>
        public static byte[] CreateHeaderOnlyPng(int width, int height)
        {
            using var ms = new MemoryStream();
            ms.Write(Signature, 0, Signature.Length);
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, (uint)width);
            WriteBE(ihdr, 4, (uint)height);
            ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            WriteChunk(ms, "IHDR", ihdr);
            return ms.ToArray();
        }

        // ------------------------------------------------------------------

        private static void WriteChunk(MemoryStream ms, string type, byte[] data)
        {
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            WriteBE(ms, (uint)data.Length);
            ms.Write(typeBytes, 0, typeBytes.Length);
            ms.Write(data, 0, data.Length);
            uint crc = Crc32(typeBytes, data);
            WriteBE(ms, crc);
        }

        private static byte[] ZlibCompress(byte[] raw)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0x78); // zlib header (CMF)
            ms.WriteByte(0x9C); // zlib header (FLG, 32K window)
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                ds.Write(raw, 0, raw.Length);
            WriteBE(ms, Adler32(raw));
            return ms.ToArray();
        }

        private static void WriteBE(MemoryStream ms, uint value)
        {
            ms.WriteByte((byte)(value >> 24));
            ms.WriteByte((byte)((value >> 16) & 0xFF));
            ms.WriteByte((byte)((value >> 8) & 0xFF));
            ms.WriteByte((byte)(value & 0xFF));
        }

        private static void WriteBE(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)((value >> 16) & 0xFF);
            buf[offset + 2] = (byte)((value >> 8) & 0xFF);
            buf[offset + 3] = (byte)(value & 0xFF);
        }

        private static uint Crc32(byte[] a, byte[] b)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++)
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    _crcTable[n] = c;
                }
            }
            uint crc = 0xFFFFFFFF;
            foreach (byte x in a) crc = _crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
            foreach (byte x in b) crc = _crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }

        private static uint Adler32(byte[] data)
        {
            uint s1 = 1, s2 = 0;
            foreach (byte x in data)
            {
                s1 = (s1 + x) % 65521;
                s2 = (s2 + s1) % 65521;
            }
            return (s2 << 16) | s1;
        }
    }
}
