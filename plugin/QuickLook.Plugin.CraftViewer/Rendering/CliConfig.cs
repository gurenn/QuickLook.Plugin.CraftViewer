using System;
using System.Globalization;
using System.IO;
using System.Text;
using QuickLook.Plugin.CraftViewer.Formats;

namespace QuickLook.Plugin.CraftViewer.Rendering
{
    /// <summary>
    /// cli-config.json 读取（设计 §1 取舍#3）：
    /// vectorcraftCliPath / photocraftCliPath / timeoutSeconds（默认 30）。
    /// 文件缺失/损坏/超大的任何情况 → 返回默认值，绝不抛异常。
    /// </summary>
    public sealed class CliConfig
    {
        /// <summary>vectorcraft-cli 全路径；空 = 未配置（优雅降级）。</summary>
        public string VectorcraftCliPath { get; set; }

        /// <summary>photocraft-cli 全路径；空 = 未配置（优雅降级）。</summary>
        public string PhotocraftCliPath { get; set; }

        /// <summary>单次渲染超时（秒），P1-1 默认 30。</summary>
        public int TimeoutSeconds { get; set; } = CraftLimits.CliTimeoutSeconds;

        /// <summary>CLI 渲染输出单边上限（像素），超过则降采样重编码；0 = 不限制。默认 512。</summary>
        public int MaxRenderSize { get; set; } = CraftLimits.DefaultRenderMaxSide;

        /// <summary>CLI 就位时优先实时渲染（true，默认）而非先用内嵌 256px 预览；
        /// false = 内嵌优先（原 P0 行为）。CLI 未配置/失败时自动回落内嵌。</summary>
        public bool PreferRendered { get; set; } = true;

        /// <summary>从插件目录读取 cli-config.json。</summary>
        public static CliConfig Load(string pluginDir)
        {
            var cfg = new CliConfig();
            try
            {
                if (string.IsNullOrWhiteSpace(pluginDir)) return cfg;
                string file = Path.Combine(pluginDir, "cli-config.json");
                if (!File.Exists(file)) return cfg;
                if (new FileInfo(file).Length > 256 * 1024) return cfg; // 配置异常大 → 默认值

                string json = File.ReadAllText(file);
                ParseInto(json, cfg);
            }
            catch (Exception)
            {
                // 任何读取/解析失败 → 默认值（§3 设计）
            }
            return cfg;
        }

        // ------------------------------------------------------------------
        // 扁平 JSON 容错解析（仅顶层字符串/数字键；损坏即停，保留已解析项）
        // ------------------------------------------------------------------

        private static void ParseInto(string json, CliConfig cfg)
        {
            if (string.IsNullOrEmpty(json)) return;
            int i = 0;
            var s = json;

            void SkipWs()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            string ReadString()
            {
                i++; // 开引号
                var sb = new StringBuilder();
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c == '"') { i++; return sb.ToString(); }
                    if (c == '\\')
                    {
                        i++;
                        if (i >= s.Length) break;
                        char e = s[i++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u':
                                if (i + 4 <= s.Length &&
                                    int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber,
                                        CultureInfo.InvariantCulture, out int cp))
                                {
                                    sb.Append((char)cp);
                                    i += 4;
                                }
                                break;
                            default: sb.Append(e); break;
                        }
                        continue;
                    }
                    sb.Append(c);
                    i++;
                }
                return sb.ToString();
            }

            SkipWs();
            if (i >= s.Length || s[i] != '{') return;
            i++;

            while (true)
            {
                SkipWs();
                if (i >= s.Length) return;
                if (s[i] == '}') return;
                if (s[i] == ',') { i++; continue; }
                if (s[i] != '"') { i++; continue; } // 容错：跳过非法字符

                string key = ReadString();
                SkipWs();
                if (i < s.Length && s[i] == ':') i++;
                SkipWs();
                if (i >= s.Length) return;

                if (key == "vectorcraftCliPath" && s[i] == '"')
                    cfg.VectorcraftCliPath = ReadString();
                else if (key == "photocraftCliPath" && s[i] == '"')
                    cfg.PhotocraftCliPath = ReadString();
                else if (key == "timeoutSeconds" && (char.IsDigit(s[i]) || s[i] == '-'))
                {
                    int start = i;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-')) i++;
                    if (int.TryParse(s.Substring(start, i - start), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int t) && t > 0)
                        cfg.TimeoutSeconds = t;
                }
                else if (key == "maxRenderSize" && (char.IsDigit(s[i]) || s[i] == '-'))
                {
                    int start = i;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-')) i++;
                    if (int.TryParse(s.Substring(start, i - start), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int m) && m >= 0)
                        cfg.MaxRenderSize = m; // 0 = 不限制
                }
                else if (key == "preferRendered" && (s[i] == 't' || s[i] == 'f'))
                {
                    bool v = s[i] == 't';
                    cfg.PreferRendered = v;
                    while (i < s.Length && (char.IsLetter(s[i]))) i++;
                }
                else
                {
                    // 跳过其他键值（直到逗号或闭括号；字符串内的逗号在本配置场景可忽略）
                    while (i < s.Length && s[i] != ',' && s[i] != '}') i++;
                }
            }
        }
    }
}
