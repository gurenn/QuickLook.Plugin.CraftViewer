using System;
using System.IO;
using System.Text;

namespace QuickLook.Plugin.CraftViewer.Formats
{
    /// <summary>ZIP 中央目录条目检查结果。</summary>
    public sealed class ZipEntryInfo
    {
        /// <summary>中央目录记录的未压缩数据 CRC-32。</summary>
        public uint Crc32 { get; internal set; }

        /// <summary>General Purpose Flag bit0：加密（A10）。</summary>
        public bool Encrypted { get; internal set; }

        /// <summary>EOCD 字段为 0xFFFF/0xFFFFFFFF：ZIP64（A10）。</summary>
        public bool Zip64 { get; internal set; }
    }

    /// <summary>
    /// ZIP 中央目录轻量检查器：net462 的 System.IO.Compression 既不校验 CRC 也不强制
    /// 加密标志（A7/A10 需要明确报错），本类直接解析中央目录补齐边界。
    /// 任何解析失败返回 null（非致命，交由 BCL 自然报错），绝不抛异常。
    /// </summary>
    internal static class ZipInspector
    {
        /// <summary>检查指定 entry；null = 无法检查。</summary>
        public static ZipEntryInfo Inspect(Stream stream, string entryName)
        {
            long saved = -1;
            try
            {
                saved = stream.Position;
                long len = stream.Length;
                int tail = (int)Math.Min(len, 65536 + 22);
                stream.Seek(len - tail, SeekOrigin.Begin);
                var buf = new byte[tail];
                ReadAll(stream, buf);

                // 从末尾向前找 EOCD（PK\x05\x06，容忍 comment）
                int eocd = -1;
                for (int i = buf.Length - 22; i >= 0; i--)
                {
                    if (buf[i] == 0x50 && buf[i + 1] == 0x4B && buf[i + 2] == 0x05 && buf[i + 3] == 0x06)
                    {
                        eocd = i;
                        break;
                    }
                }
                if (eocd < 0) { Restore(stream, saved); return null; }

                ushort count = LE16(buf, eocd + 10);
                uint cdSize = LE32(buf, eocd + 12);
                uint cdOffset = LE32(buf, eocd + 16);

                bool zip64 = count == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF;
                Restore(stream, saved);
                if (zip64)
                    return new ZipEntryInfo { Zip64 = true };

                if (cdOffset + cdSize > len) return null;

                stream.Seek(cdOffset, SeekOrigin.Begin);
                var cd = new byte[cdSize];
                ReadAll(stream, cd);
                Restore(stream, saved);

                int p = 0;
                while (p + 46 <= cd.Length)
                {
                    if (cd[p] != 0x50 || cd[p + 1] != 0x4B || cd[p + 2] != 0x01 || cd[p + 3] != 0x02)
                        break; // 中央目录损坏：停止
                    ushort flags = LE16(cd, p + 8);
                    uint crc = LE32(cd, p + 16);
                    ushort nameLen = LE16(cd, p + 28);
                    ushort extraLen = LE16(cd, p + 30);
                    ushort commentLen = LE16(cd, p + 32);
                    if (p + 46 + nameLen > cd.Length) break;

                    string name = Encoding.UTF8.GetString(cd, p + 46, nameLen);
                    if (string.Equals(name, entryName, StringComparison.Ordinal))
                        return new ZipEntryInfo
                        {
                            Crc32 = crc,
                            Encrypted = (flags & 0x1) != 0
                        };
                    p += 46 + nameLen + extraLen + commentLen;
                }
                return null;
            }
            catch
            {
                if (saved >= 0) { try { Restore(stream, saved); } catch { } }
                return null;
            }
        }

        // ------------------------------------------------------------------

        private static void ReadAll(Stream stream, byte[] buf)
        {
            int read = 0;
            while (read < buf.Length)
            {
                int n = stream.Read(buf, read, buf.Length - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
        }

        private static void Restore(Stream stream, long position)
        {
            stream.Seek(position, SeekOrigin.Begin);
        }

        private static ushort LE16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));

        private static uint LE32(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    }
}
