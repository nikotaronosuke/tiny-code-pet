// CodexPetNotify.exe - Codex Hook Adapter (tiny hook helper)
// Codex の各 Hook から起動され、stdin の JSON から必要最小限の status metadata
// (hook_event_name / session_id / turn_id / cwd / tool_name / plan[].status) だけを
// 取り出し、常駐ペット (ClaudePet.exe) へ WM_COPYDATA で Codex 正規化イベントを
// 送って即終了する。常に exit code 0。Codex を絶対にブロックしない。
//
// Claude 用 ClaudePetNotify.exe とは別プロセス・別 source。既存 Claude 側の
// 契約 (正規化イベント 1〜10 / payload 3 行) を一切変更しないため、共通化ではなく
// 分離を選んでいる (docs/DESIGN_DECISIONS.md 参照)。
//
// Codex 正規化イベント (dwData 20〜27。1〜10 は Claude 専用で意味を変えない):
//   20 = codex_prompt_submit    (UserPromptSubmit)  新 turn 登録 + 進捗リセット
//                                extra = sanitized model identifier (空のこともある)
//   21 = codex_activity         (PostToolUse)       tool activity
//   22 = codex_plan_snapshot    (PostToolUse update_plan) extra="c/i/t"
//   23 = codex_permission       (PermissionRequest) 確認して！
//   24 = codex_stop             (Stop)              completion candidate (5秒 quiet grace)
//   25 = codex_session_end      (SessionEnd)
//   26 = codex_subagent_start   (SubagentStart)     その turn を「subagent 含む」と mark
//   27 = codex_subagent_stop    (SubagentStop)      root completion にはしない
// payload = "session_id\nproject_name\nextra\nturn_id" (UTF-8)
//
// 使い方:
//   (hookから)  stdin に JSON
//   --dry-run   -> 送信せず、正規化結果だけを stdout へ 1 行出力 (install 検証用)
//
// プライバシー: prompt 本文・応答本文・description 本文・plan step 本文・
// tool command / response 本文・transcript は読まない・送らない・保存しない。

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CodexPetNotify
{
    internal static class Program
    {
        private const string WndClassName = "ClaudeDesktopPetWnd";
        private const int WM_COPYDATA = 0x004A;

        private const int EvCodexPromptSubmit = 20;
        private const int EvCodexActivity = 21;
        private const int EvCodexPlanSnapshot = 22;
        private const int EvCodexPermission = 23;
        private const int EvCodexStop = 24;
        private const int EvCodexSessionEnd = 25;
        private const int EvCodexSubagentStart = 26;
        private const int EvCodexSubagentStop = 27;

        // update_plan を観測したが plan の status を解析できなかったときに
        // Activity(21) の extra へ載せる固定 marker。plan step 本文は含まない。
        // src/Pet.cs / src/Notify.cs の同名定数と文字列を一致させること。
        private const string StructuredObserved = "structured-observed";

        // model identifier の長さ上限。payload は行区切り (4 行) なので
        // 改行・control 文字は Sanitize で落とす (落とさないと turn_id が壊れる)。
        private const int MaxModelLen = 40;

        // Codex では UserPromptSubmit / PermissionRequest / Stop を sync hook として
        // 登録するため、ペット自動起動の待ち時間は Claude (3秒) より短くする。
        private const int StartWaitMs = 1500;

        [StructLayout(LayoutKind.Sequential)]
        private struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, ref COPYDATASTRUCT lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        private static int Main(string[] args)
        {
            try
            {
                bool dryRun = (args.Length > 0 && args[0] == "--dry-run");

                string json = ReadStdin();
                string eventName = ExtractString(json, "hook_event_name");
                string sessionId = ExtractString(json, "session_id");
                string turnId = ExtractString(json, "turn_id");
                string project = ToProjectName(ExtractString(json, "cwd"));

                int eventType;
                string extra = "";

                switch (eventName)
                {
                    case "UserPromptSubmit":
                        // Codex はこの hook の共通 input に model を持つ (実測 schema)。
                        // 新 turn ごとに送るので、空なら Pet 側で 「不明」へ戻る。
                        eventType = EvCodexPromptSubmit;
                        extra = SanitizeModel(ExtractModelId(json));
                        break;

                    case "PostToolUse":
                        // 1 tool につき helper 1 回。activity と update_plan progress を
                        // 同じ adapter で処理する (update_plan 専用 hook を足さない)。
                        eventType = EvCodexActivity;
                        if (ExtractString(json, "tool_name") == "update_plan")
                        {
                            string counts = CountPlanStatuses(json);
                            // snapshot を取れなかった場合は fail-closed:
                            // 進捗は捏造せず activity として扱うが、update_plan という
                            // structured tracker を使った事実だけは marker で残す。
                            if (counts != null) { eventType = EvCodexPlanSnapshot; extra = counts; }
                            else extra = StructuredObserved;
                        }
                        break;

                    case "PermissionRequest":
                        eventType = EvCodexPermission;
                        break;

                    case "Stop":
                        eventType = EvCodexStop;
                        break;

                    case "SessionEnd":
                        eventType = EvCodexSessionEnd;
                        break;

                    case "SubagentStart":
                        eventType = EvCodexSubagentStart;
                        break;

                    case "SubagentStop":
                        eventType = EvCodexSubagentStop;
                        break;

                    default:
                        // PreToolUse を含む未知イベントは production では不要
                        if (dryRun) WriteLine("ev=0 (ignored) hook=" + eventName);
                        return 0;
                }

                string payload = sessionId + "\n" + project + "\n" + extra + "\n" + turnId;

                if (dryRun)
                {
                    WriteLine("ev=" + eventType + " sess=" + sessionId + " turn=" + turnId +
                              " proj=" + project + " extra=" + extra);
                    return 0;
                }

                // 頻度の高い低優先イベントではペットを起こさない (未起動なら捨てる)
                bool mayAutoStart = (eventType == EvCodexPromptSubmit ||
                                     eventType == EvCodexPermission ||
                                     eventType == EvCodexStop);

                IntPtr hwnd = FindWindow(WndClassName, null);
                if (hwnd == IntPtr.Zero)
                {
                    if (!mayAutoStart) { DebugLog(eventType, sessionId, turnId, project, extra, "drop:no-pet"); return 0; }
                    hwnd = StartPetAndWait();
                    if (hwnd == IntPtr.Zero) { DebugLog(eventType, sessionId, turnId, project, extra, "drop:start-fail"); return 0; }
                }

                bool ok = SendEvent(hwnd, eventType, payload);
                DebugLog(eventType, sessionId, turnId, project, extra,
                    ok ? "sent" : "SEND-FAIL err=" + Marshal.GetLastWin32Error());
            }
            catch { }
            return 0;
        }

        // winexe なので、リダイレクトされていない親コンソールへは明示 attach が要る。
        private static void WriteLine(string line)
        {
            try
            {
                if (GetStdHandle(-11 /*STD_OUTPUT_HANDLE*/) == IntPtr.Zero) AttachConsole(0xFFFFFFFF);
            }
            catch { }
            try { Console.Out.WriteLine(line); Console.Out.Flush(); }
            catch { }
        }

        // bin\debug.flag が存在するときだけ bin\debug.log へ追記する (通常は完全に無効)。
        private static void DebugLog(int eventType, string sessionId, string turnId, string project, string extra, string note)
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                if (!File.Exists(Path.Combine(dir, "debug.flag"))) return;
                byte[] line = Encoding.UTF8.GetBytes(
                    DateTime.Now.ToString("HH:mm:ss.fff") + " [codex] ev=" + eventType +
                    " sess=" + sessionId + " turn=" + turnId + " proj=" + project +
                    " extra=" + extra + " " + note + "\r\n");
                using (var fs = new FileStream(Path.Combine(dir, "debug.log"),
                    FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.Write(line, 0, line.Length);
                }
            }
            catch { }
        }

        private static string ReadStdin()
        {
            try
            {
                using (Stream s = Console.OpenStandardInput())
                using (var reader = new StreamReader(s, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch { return ""; }
        }

        // JSON から文字列フィールドを1つ抽出する (完全な parser は積まない軽量方式)。
        // 制約: 本文フィールド内に同名の "key":"..." が現れると誤抽出しうるが、
        // 抽出対象は表示・振り分け用途のみで実害が出ない範囲として許容する
        // (Claude 側 ClaudePetNotify と同じ方針)。
        private static string ExtractString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            return Unescape(m.Groups[1].Value);
        }

        // model は "model":"id" の他に "model":{"id":...} 形式もあり得るので
        // 両方を見る。model 以外の JSON 本文は一切読まない。
        private static string ExtractModelId(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            Match m = Regex.Match(json, "\"model\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (m.Success) return Unescape(m.Groups[1].Value);
            int i = json.IndexOf("\"model\"", StringComparison.Ordinal);
            if (i < 0) return "";
            int brace = json.IndexOf('{', i);
            if (brace < 0 || brace - i > 12) return "";
            int end = json.IndexOf('}', brace);
            string region = (end > brace) ? json.Substring(brace, end - brace) : json.Substring(brace);
            Match id = Regex.Match(region, "\"(?:id|display_name)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return id.Success ? Unescape(id.Groups[1].Value) : "";
        }

        // payload の行境界を壊さないよう 改行・control 文字を除去し、長さを制限する。
        private static string SanitizeModel(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (c < 0x20 || c == 0x7F) continue; // CR/LF/TAB を含む control 文字
                sb.Append(c);
                if (sb.Length >= MaxModelLen) break;
            }
            return sb.ToString().Trim();
        }

        private static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    switch (n)
                    {
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case '"': sb.Append('"'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 < s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(i + 1, 4),
                                    System.Globalization.NumberStyles.HexNumber, null, out code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // PostToolUse(update_plan) の tool_input.plan にある "status" 値だけを数えて
        // "completed/in_progress/total" を返す。plan の step 本文は読まない・送らない。
        // 実測 (Phase B) では tool_input.plan は毎回「全量 snapshot」なので冪等。
        // 領域は tool_input のみ。tool_response / tool_use_id 以降は数えない
        // (echo による二重カウント防止)。
        // status を1つも取れなかった場合は null を返し、呼び出し側は snapshot を送らない
        // (Hook snapshot 欠落を1件実測しているため、捏造せず fail-closed)。
        private static string CountPlanStatuses(string json)
        {
            int start = json.IndexOf("\"tool_input\"", StringComparison.Ordinal);
            if (start < 0) return null;
            int end = EarliestAfter(json, start, "\"tool_response\"", "\"tool_use_id\"");
            string region = (end > start) ? json.Substring(start, end - start) : json.Substring(start);

            int total = 0, done = 0, inProg = 0;
            foreach (Match m in Regex.Matches(region, "\"status\"\\s*:\\s*\"(pending|in_progress|completed)\""))
            {
                total++;
                if (m.Groups[1].Value == "completed") done++;
                else if (m.Groups[1].Value == "in_progress") inProg++;
            }
            if (total == 0) return null;
            return done + "/" + inProg + "/" + total;
        }

        private static int EarliestAfter(string json, int start, string a, string b)
        {
            int ia = json.IndexOf(a, start, StringComparison.Ordinal);
            int ib = json.IndexOf(b, start, StringComparison.Ordinal);
            if (ia < 0) return ib;
            if (ib < 0) return ia;
            return (ia < ib) ? ia : ib;
        }

        private static string ToProjectName(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return "";
            try
            {
                string trimmed = cwd.TrimEnd('\\', '/');
                string name = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(name) ? "" : name;
            }
            catch { return ""; }
        }

        private static IntPtr StartPetAndWait()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string petExe = Path.Combine(dir, "ClaudePet.exe");
                if (!File.Exists(petExe)) return IntPtr.Zero;
                // .NET の Process.Start(UseShellExecute=false) は CreateProcess を
                // bInheritHandles=TRUE で呼ぶため、helper の hook stdin/stdout/stderr が
                // そのまま常駐ペットへ継承される。すると helper が終了しても
                // ペットが hook の stdout を掴んだままになり、stdout を EOF まで読む
                // runner がペットの生存中ずっとブロックする (実測で再現)。
                // Codex は UserPromptSubmit / PermissionRequest / Stop を sync hook として
                // 登録し、これらが自動起動対象なので実害が出る。
                // ShellExecute はハンドルを一切渡さないので、これで縁を切る。
                // (自動起動が走るのはペット未起動時だけで、通常は FindWindow が
                //  成功してこの経路自体を通らない)
                var psi = new ProcessStartInfo(petExe);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { return IntPtr.Zero; }

            for (int waited = 0; waited < StartWaitMs; waited += 100)
            {
                Thread.Sleep(100);
                IntPtr hwnd = FindWindow(WndClassName, null);
                if (hwnd != IntPtr.Zero) return hwnd;
            }
            return IntPtr.Zero;
        }

        private static bool SendEvent(IntPtr hwnd, int eventType, string payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(payload ?? "");
            IntPtr mem = Marshal.AllocHGlobal(bytes.Length + 1);
            try
            {
                Marshal.Copy(bytes, 0, mem, bytes.Length);
                Marshal.WriteByte(mem, bytes.Length, 0);

                var cds = new COPYDATASTRUCT();
                cds.dwData = new IntPtr(eventType);
                cds.cbData = bytes.Length;
                cds.lpData = mem;

                IntPtr result;
                // SMTO_ABORTIFHUNG: 1秒で諦める。Hook を長引かせない。
                IntPtr ret = SendMessageTimeout(hwnd, WM_COPYDATA, IntPtr.Zero, ref cds, 0x0002, 1000, out result);
                return ret != IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }
    }
}
