using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace QuickLook.Plugin.CraftViewer.Formats
{
    /// <summary>
    /// vectorcraft 顶层信封扫描结果：只含本插件关心的字段，其余顶层值整段跳过。
    /// </summary>
    public sealed class JsonEnvelope
    {
        public string Format { get; internal set; }
        public bool HasFormat { get; internal set; }

        public long Version { get; internal set; }
        public bool HasVersion { get; internal set; }

        public string Generator { get; internal set; }

        public string PreviewMime { get; internal set; }

        /// <summary>preview.data（base64，已剥掉 data URI 前缀；null 表示无）。</summary>
        public string PreviewDataBase64 { get; internal set; }

        /// <summary>preview.blob [offset, length]（相对 BLOB_MAGIC 之后；null 表示无）。</summary>
        public long? PreviewBlobOffset { get; internal set; }
        public long? PreviewBlobLength { get; internal set; }

        /// <summary>顶层对象是否完整闭合（容错扫描时用于区分"截断"与"损坏"）。</summary>
        public bool Complete { get; internal set; }

        /// <summary>扫描结束位置（顶层 '}' 之后；截断时为输入末尾），供 blob 尾部 marker 搜索起点。</summary>
        public int EndIndex { get; internal set; }
    }

    /// <summary>
    /// pcraft manifest.json 顶层扫描结果。
    /// </summary>
    public sealed class PcraftManifest
    {
        public long FormatVersion { get; internal set; }
        public bool HasFormatVersion { get; internal set; }

        public string Generator { get; internal set; }

        /// <summary>full_composite / composite / thumbnail 字段原值（白名单校验用）。</summary>
        public string FullComposite { get; internal set; }
        public string Composite { get; internal set; }
        public string Thumbnail { get; internal set; }
    }

    /// <summary>
    /// 最小 JSON 顶层信封解析器（设计 §1 取舍#5）。
    /// 只扫描顶层（深度 1）键：遇到关心的键才取值；其余顶层值按深度计数器整段跳过。
    /// 只支持 RFC 8259 子集（字符串转义反转义、数字、true/false/null）；
    /// 不保证对畸形 JSON 的错误定位精度。不引入 Newtonsoft（宿主 13.0.4 版本冲突）。
    /// 上限：扫描 MaxEnvelopeScanBytes 字节、嵌套 MaxJsonDepth 层，超限抛
    /// CraftFormatException(LimitExceeded)；语法错误抛 BadJson；容错模式下静默截断。
    /// </summary>
    public static class MinimalJson
    {
        // ------------------------------------------------------------------
        // 对外入口
        // ------------------------------------------------------------------

        /// <summary>扫描 vectorcraft 顶层信封。tolerant=true 时截断/语法损坏只取已解析前缀。</summary>
        public static JsonEnvelope ScanEnvelope(byte[] utf8, bool tolerant)
        {
            var env = new JsonEnvelope();
            if (utf8 == null || utf8.Length == 0) return env;

            var s = new Scanner(utf8);
            try
            {
                s.SkipWs();
                if (s.Eof || s.Peek != (byte)'{')
                {
                    if (!tolerant) throw Fail("顶层不是 JSON 对象");
                    return env;
                }
                s.Advance();

                while (true)
                {
                    s.SkipWs();
                    s.BudgetCheck();
                    if (s.Eof) break; // 截断：容错返回前缀；严格模式由调用方以 Complete 判定
                    if (s.Peek == (byte)'}') { s.Advance(); env.Complete = true; env.EndIndex = s.Index; break; }
                    if (s.Peek == (byte)',') { s.Advance(); continue; }
                    if (s.Peek != (byte)'"') { if (!tolerant) throw Fail("顶层对象键不是字符串"); break; }

                    string key = s.ReadString(CraftLimits.MaxEnvelopeScanBytes);
                    s.SkipWs();
                    if (s.Eof) break;
                    if (s.Peek != (byte)':') { if (!tolerant) throw Fail("顶层对象缺少 ':'"); break; }
                    s.Advance();
                    s.SkipWs();
                    if (s.Eof) break;

                    switch (key)
                    {
                        case "format":
                            if (s.Peek == (byte)'"')
                            {
                                env.Format = s.ReadString(CraftLimits.MaxEnvelopeScanBytes);
                                env.HasFormat = true;
                            }
                            else s.SkipValue();
                            break;

                        case "version":
                            if (IsNumberStart(s.Peek))
                            {
                                env.Version = s.ReadNumber();
                                env.HasVersion = true;
                            }
                            else s.SkipValue();
                            break;

                        case "generator":
                            if (s.Peek == (byte)'"')
                                env.Generator = s.ReadString(CraftLimits.MaxEnvelopeScanBytes);
                            else s.SkipValue();
                            break;

                        case "preview":
                            ScanPreview(s, env, tolerant);
                            break;

                        default:
                            s.SkipValue();
                            break;
                    }
                }
            }
            catch (CraftFormatException ex) when (tolerant && ex.Category == CraftErrorCategory.BadJson)
            {
                // 容错模式（CanHandle 探测）：语法损坏即以已解析前缀返回
            }

            env.EndIndex = s.Index;
            return env;
        }

        /// <summary>扫描 pcraft manifest.json（严格模式；超 MaxManifestBytes 由调用方负责）。</summary>
        public static PcraftManifest ScanManifest(byte[] utf8)
        {
            var m = new PcraftManifest();
            if (utf8 == null || utf8.Length == 0) throw Fail("manifest.json 为空");

            var s = new Scanner(utf8);
            s.SkipWs();
            if (s.Eof || s.Peek != (byte)'{') throw Fail("manifest.json 顶层不是 JSON 对象");
            s.Advance();

            while (true)
            {
                s.SkipWs();
                s.BudgetCheck();
                if (s.Eof) throw Fail("manifest.json 不完整（JSON 被截断）");
                if (s.Peek == (byte)'}') { s.Advance(); break; }
                if (s.Peek == (byte)',') { s.Advance(); continue; }
                if (s.Peek != (byte)'"') throw Fail("manifest.json 顶层对象键不是字符串");

                string key = s.ReadString(CraftLimits.MaxManifestBytes);
                s.SkipWs();
                if (s.Peek != (byte)':') throw Fail("manifest.json 顶层对象缺少 ':'");
                s.Advance();
                s.SkipWs();

                switch (key)
                {
                    case "format_version":
                        if (IsNumberStart(s.Peek)) { m.FormatVersion = s.ReadNumber(); m.HasFormatVersion = true; }
                        else s.SkipValue();
                        break;
                    case "generator":
                        if (s.Peek == (byte)'"') m.Generator = s.ReadString(CraftLimits.MaxManifestBytes);
                        else s.SkipValue();
                        break;
                    case "full_composite":
                        if (s.Peek == (byte)'"') m.FullComposite = s.ReadString(CraftLimits.MaxManifestBytes);
                        else s.SkipValue();
                        break;
                    case "composite":
                        if (s.Peek == (byte)'"') m.Composite = s.ReadString(CraftLimits.MaxManifestBytes);
                        else s.SkipValue();
                        break;
                    case "thumbnail":
                        if (s.Peek == (byte)'"') m.Thumbnail = s.ReadString(CraftLimits.MaxManifestBytes);
                        else s.SkipValue();
                        break;
                    default:
                        s.SkipValue();
                        break;
                }
            }

            return m;
        }

        // ------------------------------------------------------------------
        // preview 对象：{ "mime": ..., "data": "<base64>", "blob": [offset, length] }
        // ------------------------------------------------------------------

        private static void ScanPreview(Scanner s, JsonEnvelope env, bool tolerant)
        {
            try
            {
                if (s.Eof) return;
                if (s.Peek != (byte)'{') { s.SkipValue(); return; }
                s.Advance();

                while (true)
                {
                    s.SkipWs();
                    s.BudgetCheck();
                    if (s.Eof) return;
                    if (s.Peek == (byte)'}') { s.Advance(); return; }
                    if (s.Peek == (byte)',') { s.Advance(); continue; }
                    if (s.Peek != (byte)'"') { s.SkipValue(); return; }

                    string key = s.ReadString(CraftLimits.MaxEnvelopeScanBytes);
                    s.SkipWs();
                    if (s.Eof) return;
                    if (s.Peek != (byte)':') { s.SkipValue(); return; }
                    s.Advance();
                    s.SkipWs();
                    if (s.Eof) return;

                    switch (key)
                    {
                        case "mime":
                            if (s.Peek == (byte)'"') env.PreviewMime = s.ReadString(CraftLimits.MaxEnvelopeScanBytes);
                            else s.SkipValue();
                            break;

                        case "data":
                            if (s.Peek == (byte)'"')
                                env.PreviewDataBase64 = StripDataUri(s.ReadString(CraftLimits.MaxEnvelopeScanBytes));
                            else s.SkipValue();
                            break;

                        case "blob":
                            if (!s.Eof && s.Peek == (byte)'[')
                            {
                                s.Advance();
                                s.SkipWs();
                                if (!s.Eof && IsNumberStart(s.Peek))
                                {
                                    env.PreviewBlobOffset = s.ReadNumber();
                                    s.SkipWs();
                                    if (!s.Eof && s.Peek == (byte)',')
                                    {
                                        s.Advance();
                                        s.SkipWs();
                                        if (!s.Eof && IsNumberStart(s.Peek))
                                            env.PreviewBlobLength = s.ReadNumber();
                                        else s.SkipValue();
                                    }
                                    else s.SkipValue();
                                    // 吃掉数组余下部分
                                    s.SkipArrayTail();
                                }
                                else s.SkipValue();
                            }
                            else s.SkipValue();
                            break;

                        default:
                            s.SkipValue();
                            break;
                    }
                }
            }
            catch (CraftFormatException ex) when (tolerant && ex.Category == CraftErrorCategory.BadJson)
            {
                // preview 内部损坏：信封其余字段照常返回
            }
        }

        /// <summary>剥掉 "data:image/png;base64," 前缀（如有）。</summary>
        private static string StripDataUri(string s)
        {
            if (s == null) return null;
            int comma = s.IndexOf(',');
            if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
                return s.Substring(comma + 1);
            return s;
        }

        private static bool IsNumberStart(byte b)
        {
            return b == (byte)'-' || (b >= (byte)'0' && b <= (byte)'9');
        }

        private static CraftFormatException Fail(string reason)
        {
            return new CraftFormatException(CraftErrorCategory.BadJson, "[BadJson] " + reason);
        }

        // ------------------------------------------------------------------
        // 内部扫描器（字节级，零 WPF 依赖）
        // ------------------------------------------------------------------

        private sealed class Scanner
        {
            private readonly byte[] _b;
            private int _i;
            private int _depth;

            public Scanner(byte[] bytes) { _b = bytes ?? Array.Empty<byte>(); _i = 0; }

            public int Index => _i;
            public bool Eof => _i >= _b.Length;
            public byte Peek => Eof ? (byte)0 : _b[_i];

            public void Advance() { if (!Eof) _i++; }

            public void BudgetCheck()
            {
                if (_i > CraftLimits.MaxEnvelopeScanBytes)
                    throw new CraftFormatException(CraftErrorCategory.LimitExceeded,
                        "[LimitExceeded] 顶层 JSON 扫描超过 " + CraftLimits.MaxEnvelopeScanBytes / (1024 * 1024) + "MB 上限");
            }

            public void SkipWs()
            {
                while (!Eof)
                {
                    byte c = _b[_i];
                    if (c == 0x20 || c == 0x09 || c == 0x0A || c == 0x0D) _i++;
                    else break;
                }
            }

            /// <summary>跳过一个任意 JSON 值（深度感知，超限抛 LimitExceeded）。</summary>
            public void SkipValue()
            {
                SkipWs();
                BudgetCheck();
                if (Eof) return;
                byte c = Peek;
                switch (c)
                {
                    case (byte)'{':
                        _i++; _depth++;
                        if (_depth > CraftLimits.MaxJsonDepth)
                            throw new CraftFormatException(CraftErrorCategory.LimitExceeded,
                                "[LimitExceeded] JSON 嵌套深度超过 " + CraftLimits.MaxJsonDepth + " 层上限");
                        SkipObject();
                        _depth--;
                        break;

                    case (byte)'[':
                        _i++; _depth++;
                        if (_depth > CraftLimits.MaxJsonDepth)
                            throw new CraftFormatException(CraftErrorCategory.LimitExceeded,
                                "[LimitExceeded] JSON 嵌套深度超过 " + CraftLimits.MaxJsonDepth + " 层上限");
                        SkipArray();
                        _depth--;
                        break;

                    case (byte)'"':
                        SkipString(CraftLimits.MaxEnvelopeScanBytes);
                        break;

                    case (byte)'t': SkipLiteral("true"); break;
                    case (byte)'f': SkipLiteral("false"); break;
                    case (byte)'n': SkipLiteral("null"); break;

                    default:
                        SkipNumberText();
                        break;
                }
            }

            /// <summary>吃掉数组余下部分（当前应位于数组首个元素或 ','/']'）。</summary>
            public void SkipArrayTail()
            {
                while (true)
                {
                    SkipWs();
                    BudgetCheck();
                    if (Eof) return;
                    if (Peek == (byte)']') { Advance(); return; }
                    if (Peek == (byte)',') { Advance(); continue; }
                    SkipValue();
                    SkipWs();
                    if (Eof) return;
                    if (Peek == (byte)']') { Advance(); return; }
                    if (Peek != (byte)',') return;
                    Advance();
                }
            }

            private void SkipObject()
            {
                SkipWs();
                if (Eof) return;
                if (Peek == (byte)'}') { Advance(); return; }
                while (true)
                {
                    BudgetCheck();
                    SkipWs();
                    if (Eof) return;
                    if (Peek == (byte)'}') { Advance(); return; }
                    if (Peek != (byte)'"') return; // 损坏：停止（深度计数由调用方管理）
                    SkipString(CraftLimits.MaxEnvelopeScanBytes);
                    SkipWs();
                    if (Eof) return;
                    if (Peek != (byte)':') return;
                    Advance();
                    SkipValue();
                    SkipWs();
                    if (Eof) return;
                    if (Peek == (byte)',') { Advance(); continue; }
                    if (Peek == (byte)'}') { Advance(); return; }
                    return; // 损坏/截断
                }
            }

            private void SkipArray()
            {
                SkipWs();
                if (Eof) return;
                if (Peek == (byte)']') { Advance(); return; }
                while (true)
                {
                    BudgetCheck();
                    SkipValue();
                    SkipWs();
                    if (Eof) return;
                    if (Peek == (byte)',') { Advance(); continue; }
                    if (Peek == (byte)']') { Advance(); return; }
                    return;
                }
            }

            /// <summary>跳过字符串（当前位于开引号），maxBytes 超限抛 LimitExceeded。</summary>
            public void SkipString(int maxBytes)
            {
                Advance(); // 开引号
                int count = 0;
                while (!Eof)
                {
                    BudgetCheck();
                    byte c = _b[_i];
                    if (c == (byte)'\\') { _i += 2; count += 2; continue; } // 含 \u 时后续 4 个 hex 当普通字节跳过
                    if (c == (byte)'"') { Advance(); return; }
                    Advance();
                    count++;
                    if (count > maxBytes)
                        throw new CraftFormatException(CraftErrorCategory.LimitExceeded,
                            "[LimitExceeded] JSON 字符串超过长度上限");
                }
            }

            private void SkipLiteral(string lit)
            {
                foreach (byte expected in Encoding.ASCII.GetBytes(lit))
                {
                    if (Eof || _b[_i] != expected)
                        throw Fail("JSON 字面量损坏（期望 " + lit + "）");
                    _i++;
                }
            }

            private void SkipNumberText()
            {
                int start = _i;
                if (!Eof && _b[_i] == (byte)'-') _i++;
                while (!Eof && _b[_i] >= (byte)'0' && _b[_i] <= (byte)'9') _i++;
                if (!Eof && _b[_i] == (byte)'.')
                {
                    _i++;
                    while (!Eof && _b[_i] >= (byte)'0' && _b[_i] <= (byte)'9') _i++;
                }
                if (!Eof && (_b[_i] == (byte)'e' || _b[_i] == (byte)'E'))
                {
                    _i++;
                    if (!Eof && (_b[_i] == (byte)'+' || _b[_i] == (byte)'-')) _i++;
                    while (!Eof && _b[_i] >= (byte)'0' && _b[_i] <= (byte)'9') _i++;
                }
                if (_i == start)
                    throw Fail("JSON 语法错误：无法识别的值（位置 " + _i + "）");
            }

            /// <summary>读取字符串并反转义（当前位于开引号）。覆盖 \" \\ \/ \b \f \n \r \t \uXXXX（含代理对拼接）。</summary>
            public string ReadString(int maxBytes)
            {
                Advance(); // 开引号
                var buf = new List<byte>(32);
                int count = 0;
                while (!Eof)
                {
                    BudgetCheck();
                    byte c = _b[_i];
                    if (c == (byte)'"')
                    {
                        Advance();
                        return Encoding.UTF8.GetString(buf.ToArray());
                    }
                    if (c == (byte)'\\')
                    {
                        Advance();
                        if (Eof) break;
                        byte e = _b[_i];
                        Advance();
                        switch (e)
                        {
                            case (byte)'"': buf.Add(0x22); break;
                            case (byte)'\\': buf.Add(0x5C); break;
                            case (byte)'/': buf.Add(0x2F); break;
                            case (byte)'b': buf.Add(0x08); break;
                            case (byte)'f': buf.Add(0x0C); break;
                            case (byte)'n': buf.Add(0x0A); break;
                            case (byte)'r': buf.Add(0x0D); break;
                            case (byte)'t': buf.Add(0x09); break;
                            case (byte)'u':
                                {
                                    int cp = ParseHex4(_i);
                                    if (cp < 0) throw Fail("JSON \\uXXXX 转义损坏");
                                    _i += 4;
                                    // 代理对拼接（RFC 8259 子集内的正确处理）
                                    if (cp >= 0xD800 && cp <= 0xDBFF && _i + 1 < _b.Length &&
                                        _b[_i] == (byte)'\\' && _b[_i + 1] == (byte)'u' && _i + 5 < _b.Length)
                                    {
                                        int lo = ParseHex4(_i + 2);
                                        if (lo >= 0xDC00 && lo <= 0xDFFF)
                                        {
                                            cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                                            _i += 6;
                                        }
                                    }
                                    AppendUtf8(buf, cp);
                                    break;
                                }
                            default:
                                throw Fail("JSON 字符串含不支持的转义（\\x" + e.ToString("X2") + "）");
                        }
                        count++;
                        if (count > maxBytes)
                            throw new CraftFormatException(CraftErrorCategory.LimitExceeded,
                                "[LimitExceeded] JSON 字符串超过长度上限");
                        continue;
                    }
                    buf.Add(c);
                    _i++;
                    count++;
                    if (count > maxBytes)
                        throw new CraftFormatException(CraftErrorCategory.LimitExceeded,
                            "[LimitExceeded] JSON 字符串超过长度上限");
                }
                throw Fail("JSON 字符串未闭合");
            }

            /// <summary>读取数字为 long（不支持小数/指数的 version 语义；出现小数部分按截断整型处理失败）。</summary>
            public long ReadNumber()
            {
                SkipWs();
                int start = _i;
                SkipNumberText();
                var text = Encoding.ASCII.GetString(_b, start, _i - start);
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                    throw Fail("JSON 数字无法按整型解析：" + text);
                return v;
            }

            private int ParseHex4(int pos)
            {
                if (pos + 3 >= _b.Length) return -1;
                int v = 0;
                for (int k = 0; k < 4; k++)
                {
                    int d = HexDigit(_b[pos + k]);
                    if (d < 0) return -1;
                    v = (v << 4) | d;
                }
                return v;
            }

            private static int HexDigit(byte c)
            {
                if (c >= (byte)'0' && c <= (byte)'9') return c - (byte)'0';
                if (c >= (byte)'a' && c <= (byte)'f') return c - (byte)'a' + 10;
                if (c >= (byte)'A' && c <= (byte)'F') return c - (byte)'A' + 10;
                return -1;
            }

            private static void AppendUtf8(List<byte> buf, int cp)
            {
                if (cp < 0 || cp > 0x10FFFF) cp = 0xFFFD;
                if (cp < 0x80)
                {
                    buf.Add((byte)cp);
                }
                else if (cp < 0x800)
                {
                    buf.Add((byte)(0xC0 | (cp >> 6)));
                    buf.Add((byte)(0x80 | (cp & 0x3F)));
                }
                else if (cp < 0x10000)
                {
                    buf.Add((byte)(0xE0 | (cp >> 12)));
                    buf.Add((byte)(0x80 | ((cp >> 6) & 0x3F)));
                    buf.Add((byte)(0x80 | (cp & 0x3F)));
                }
                else
                {
                    buf.Add((byte)(0xF0 | (cp >> 18)));
                    buf.Add((byte)(0x80 | ((cp >> 12) & 0x3F)));
                    buf.Add((byte)(0x80 | ((cp >> 6) & 0x3F)));
                    buf.Add((byte)(0x80 | (cp & 0x3F)));
                }
            }
        }
    }
}
