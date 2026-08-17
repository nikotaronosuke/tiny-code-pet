// ClaudePetNotify.exe - Claude Code Hook Adapter (tiny hook helper)
// Claude Code の各 Hook から起動され、stdin の JSON から必要最小限のフィールド
// (hook_event_name / session_id / cwd / agent_id) だけを取り出し、
// 常駐ペット (ClaudePet.exe) へ WM_COPYDATA で正規化イベントを送って即終了する。
// 常に exit code 0。Claude Code を絶対にブロックしない。
//
// 正規化イベント (dwData):
//   1 = task_complete     (Stop)              ※ agent_id 付き(=subagent)は送らない
//   2 = prompt_submit     (UserPromptSubmit)
//   3 = permission_prompt (Notification: settings 側 matcher=permission_prompt で絞る)
//   4 = activity          (PostToolUse)       ※ Waiting -> Working 解除用
//   5 = session_end       (SessionEnd)
// payload = "session_id\nproject_name" (UTF-8)
//
// 使い方:
//   (hookから)  stdin に JSON
//   --test [名前]                  -> 完了イベントの手動テスト
//   --send <type> <session> <名前> -> 任意イベントの手動テスト
//   --quit                         -> 常駐ペットを終了させる
//
// プライバシー: prompt 本文・応答本文・tool 入出力は一切読み取らない。

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ClaudePetNotify
{
    internal static class Program
    {
        private const string WndClassName = "ClaudeDesktopPetWnd";
        private const int WM_COPYDATA = 0x004A;
        private const int WM_CLOSE = 0x0010;

        private const int EvTaskComplete = 1;
        private const int EvPromptSubmit = 2;
        private const int EvPermissionPrompt = 3;
        private const int EvActivity = 4;
        private const int EvSessionEnd = 5;
        private const int EvTaskCreated = 6;    // TaskCreated hook (extra=task_id)
        private const int EvTaskCompleted = 7;  // TaskCompleted hook / TaskUpdate(completed) (extra=task_id)
        private const int EvTaskSnapshot = 8;   // PostToolUse(TodoWrite) から導出した "c/i/t"
        private const int EvTaskRemoved = 9;    // TaskUpdate(deleted/cancelled)。削除はhookが発火しない (実測)
        private const int EvTaskInProgress = 10; // TaskUpdate(in_progress) (extra=task_id)

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

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern bool GetProcessTimes(IntPtr hProcess, out long creation, out long exit, out long kernel, out long user);

        // 祖先チェーンに claude 本体が2つ以上あれば nested。
        // 1つ目 = この hook を発火させた claude 自身、2つ目以降 = それを tool 内から
        // 起動した別の Claude セッション。PID 再利用による誤判定は
        // 「親の起動時刻 <= 子の起動時刻」検証で排除する。
        private static bool IsNestedClaude()
        {
            try
            {
                var parentOf = new System.Collections.Generic.Dictionary<uint, uint>();
                var nameOf = new System.Collections.Generic.Dictionary<uint, string>();

                IntPtr snap = CreateToolhelp32Snapshot(0x2 /*TH32CS_SNAPPROCESS*/, 0);
                if (snap == new IntPtr(-1)) return false;
                try
                {
                    var pe = new PROCESSENTRY32W();
                    pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
                    if (Process32FirstW(snap, ref pe))
                    {
                        do
                        {
                            parentOf[pe.th32ProcessID] = pe.th32ParentProcessID;
                            nameOf[pe.th32ProcessID] = pe.szExeFile ?? "";
                        } while (Process32NextW(snap, ref pe));
                    }
                }
                finally { CloseHandle(snap); }

                int claudeCount = 0;
                uint pid = (uint)Process.GetCurrentProcess().Id;
                long childStart = StartTimeOf(pid);
                var visited = new System.Collections.Generic.HashSet<uint>();

                for (int hop = 0; hop < 32; hop++)
                {
                    uint ppid;
                    if (!parentOf.TryGetValue(pid, out ppid) || ppid == 0 || !visited.Add(pid)) break;
                    string pname;
                    if (!nameOf.TryGetValue(ppid, out pname)) break;

                    // PID 再利用検出: 「親」の起動が子より後なら本物の祖先ではない
                    long parentStart = StartTimeOf(ppid);
                    if (parentStart == 0 || (childStart != 0 && parentStart > childStart)) break;

                    string n = pname.ToLowerInvariant();
                    if (n == "claude.exe" || n == "claude")
                    {
                        claudeCount++;
                        if (claudeCount >= 2) return true;
                    }
                    pid = ppid;
                    childStart = parentStart;
                }
                return false;
            }
            catch { return false; } // 判定不能時は抑制しない (通知を失うより誤通知の方がまし、ではなく安全側=通常動作)
        }

        private static long StartTimeOf(uint pid)
        {
            IntPtr h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
            if (h == IntPtr.Zero) return 0;
            try
            {
                long c, e, k, u;
                if (GetProcessTimes(h, out c, out e, out k, out u)) return c;
                return 0;
            }
            finally { CloseHandle(h); }
        }

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && args[0] == "--quit")
                {
                    IntPtr h = FindWindow(WndClassName, null);
                    if (h != IntPtr.Zero) PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }

                int eventType;
                string sessionId;
                string project;
                string extra = ""; // task_id または "done/total"

                if (args.Length > 0 && args[0] == "--test")
                {
                    eventType = EvTaskComplete;
                    sessionId = "test-session";
                    project = args.Length > 1 ? args[1] : "test-project";
                }
                else if (args.Length > 2 && args[0] == "--send")
                {
                    int.TryParse(args[1], out eventType);
                    sessionId = args[2];
                    project = args.Length > 3 ? args[3] : "";
                    extra = args.Length > 4 ? args[4] : "";
                }
                else
                {
                    string json = ReadStdin();
                    string eventName = ExtractString(json, "hook_event_name");
                    string agentId = ExtractString(json, "agent_id");
                    sessionId = ExtractString(json, "session_id");
                    project = ToProjectName(ExtractString(json, "cwd"));

                    // Nested child Claude 判定 (プロセス祖先チェーン方式):
                    // この helper の親プロセスは hook を発火させた claude 本体。その祖先に
                    // さらに別の claude が居れば、「別の Claude セッションの tool 内から起動
                    // された子 Claude」と確実に判断できるので、UI へは一切流さない。
                    // 手動起動の独立セッション (VS Code / terminal 直下) は祖先に claude が
                    // 居ないので抑制されない。環境変数は親子で同一値になるため使えない (実測)。
                    if (IsNestedClaude())
                    {
                        DebugLog(0, sessionId, project, "suppressed:nested ev=" + eventName);
                        return 0;
                    }

                    switch (eventName)
                    {
                        case "Stop":
                            if (agentId.Length > 0) return 0; // 内部subagentの完了は通知しない
                            eventType = EvTaskComplete;
                            break;
                        case "UserPromptSubmit":
                            eventType = EvPromptSubmit;
                            break;
                        case "Notification":
                            // settings 側 matcher=permission_prompt で発火を絞っている
                            eventType = EvPermissionPrompt;
                            break;
                        case "PostToolUse":
                            eventType = EvActivity;
                            string toolName = ExtractString(json, "tool_name");
                            if (agentId.Length == 0)
                            {
                                if (toolName == "TodoWrite")
                                {
                                    // TodoWrite (メインセッションのみ) は進捗スナップショット。
                                    // subagent の todo list は agent_id 付きなので混ぜない。
                                    string counts = CountTodoStatuses(json);
                                    if (counts != null) { eventType = EvTaskSnapshot; extra = counts; }
                                }
                                else if (toolName == "TaskUpdate")
                                {
                                    // 削除 (deleted/cancelled) は対応する hook が発火しないため
                                    // ここで検知して total から除外する (false incomplete の主因)。
                                    // completed は TaskCompleted hook の取りこぼし保険 (Set で冪等)。
                                    string tid, status;
                                    ParseTaskUpdate(json, out tid, out status);
                                    if (tid.Length > 0)
                                    {
                                        if (status == "deleted" || status == "cancelled")
                                        { eventType = EvTaskRemoved; extra = tid; }
                                        else if (status == "in_progress")
                                        { eventType = EvTaskInProgress; extra = tid; }
                                        else if (status == "completed")
                                        { eventType = EvTaskCompleted; extra = tid; }
                                    }
                                }
                            }
                            break;
                        case "TaskCreated":
                            if (agentId.Length > 0) return 0; // subagent のタスクは数えない
                            extra = ExtractString(json, "task_id");
                            if (extra.Length == 0) return 0;  // id が無ければ重複判定不能なので捨てる
                            eventType = EvTaskCreated;
                            break;
                        case "TaskCompleted":
                            if (agentId.Length > 0) return 0;
                            extra = ExtractString(json, "task_id");
                            if (extra.Length == 0) return 0;
                            eventType = EvTaskCompleted;
                            break;
                        case "SessionEnd":
                            eventType = EvSessionEnd;
                            break;
                        case "":
                            eventType = EvTaskComplete; // 旧設定 (Stopのみ・event名なし想定) 互換
                            break;
                        default:
                            return 0;
                    }
                }

                // 頻度の高い低優先イベントではペットを起こさない (未起動なら捨てる)
                bool mayAutoStart = (eventType == EvTaskComplete ||
                                     eventType == EvPromptSubmit ||
                                     eventType == EvPermissionPrompt);

                IntPtr hwnd = FindWindow(WndClassName, null);
                if (hwnd == IntPtr.Zero)
                {
                    if (!mayAutoStart) { DebugLog(eventType, sessionId, project, "drop:no-pet"); return 0; }
                    hwnd = StartPetAndWait();
                    if (hwnd == IntPtr.Zero) { DebugLog(eventType, sessionId, project, "drop:start-fail"); return 0; }
                }

                bool ok = SendEvent(hwnd, eventType, sessionId + "\n" + project + "\n" + extra);
                DebugLog(eventType, sessionId, project + " extra=" + extra + " " + EnvSummary(),
                    ok ? "sent" : "SEND-FAIL err=" + Marshal.GetLastWin32Error());
            }
            catch { }
            return 0;
        }

        private static string Short(string s)
        {
            return s.Length > 8 ? s.Substring(0, 8) : s;
        }

        private static string EnvSummary()
        {
            string[] names = { "CLAUDE_CODE_SESSION_ID", "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_PID", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT" };
            var sb = new StringBuilder();
            foreach (string n in names)
            {
                string v = Environment.GetEnvironmentVariable(n);
                sb.Append(n.Replace("CLAUDE_CODE_", "").Replace("CLAUDE", "C")).Append('=')
                  .Append(v == null ? "-" : Short(v)).Append(' ');
            }
            return sb.ToString();
        }

        // bin\debug.flag が存在するときだけ bin\debug.log へイベントを追記する (通常は完全に無効)。
        // 併走プロセスと衝突しないよう FileShare.ReadWrite の追記ストリームを使う。
        private static void DebugLog(int eventType, string sessionId, string project, string note)
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                if (!File.Exists(Path.Combine(dir, "debug.flag"))) return;
                byte[] line = Encoding.UTF8.GetBytes(
                    DateTime.Now.ToString("HH:mm:ss.fff") + " ev=" + eventType +
                    " sess=" + sessionId + " proj=" + project + " " + note + "\r\n");
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

        // JSON から文字列フィールドを1つ抽出する (完全な parser は使わない軽量方式)。
        // 制約: 巨大 text フィールド内に同名の "key":"..." が現れると誤抽出しうるが、
        // 抽出対象は表示・振り分け用途のみで、実害が出ない範囲として許容する。
        private static string ExtractString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            return Unescape(m.Groups[1].Value);
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

        // PostToolUse(TodoWrite) の tool_input 内の "status" 値だけを数えて
        // "completed/in_progress/total" を返す。todo 本文は読まない・送らない。
        // tool_response 以降は数えない (echo による二重カウント防止)。
        // TodoWrite が空リスト (全消去) のときは "0/0/0" を返し、Pet 側で進捗表示を消す。
        private static string CountTodoStatuses(string json)
        {
            int start = json.IndexOf("\"tool_input\"", StringComparison.Ordinal);
            if (start < 0) return null;
            int end = json.IndexOf("\"tool_response\"", start, StringComparison.Ordinal);
            string region = (end > start) ? json.Substring(start, end - start) : json.Substring(start);

            int total = 0, done = 0, inProg = 0;
            foreach (Match m in Regex.Matches(region, "\"status\"\\s*:\\s*\"(pending|in_progress|completed)\""))
            {
                total++;
                if (m.Groups[1].Value == "completed") done++;
                else if (m.Groups[1].Value == "in_progress") inProg++;
            }
            return done + "/" + inProg + "/" + total;
        }

        // TaskUpdate の tool_input から taskId と status だけを取り出す (本文は読まない)
        private static void ParseTaskUpdate(string json, out string taskId, out string status)
        {
            taskId = ""; status = "";
            int start = json.IndexOf("\"tool_input\"", StringComparison.Ordinal);
            if (start < 0) return;
            int end = json.IndexOf("\"tool_response\"", start, StringComparison.Ordinal);
            string region = (end > start) ? json.Substring(start, end - start) : json.Substring(start);
            Match mi = Regex.Match(region, "\"(?:taskId|task_id)\"\\s*:\\s*\"([^\"]{1,64})\"");
            if (mi.Success) taskId = mi.Groups[1].Value;
            Match ms = Regex.Match(region, "\"status\"\\s*:\\s*\"([a-z_]{1,20})\"");
            if (ms.Success) status = ms.Groups[1].Value;
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
                var psi = new ProcessStartInfo(petExe);
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch { return IntPtr.Zero; }

            // 起動待ち: 最大3秒
            for (int i = 0; i < 30; i++)
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
                // SMTO_ABORTIFHUNG: 1秒で諦める。Hookを長引かせない。
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
