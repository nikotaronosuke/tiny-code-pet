// ClaudePet.exe - 常駐デスクトップペット本体
// 純Win32 (P/Invoke) + layered window。アイドル時は GetMessage でブロックし CPU 0%。
// 描画は System.Drawing で ARGB ビットマップを合成し UpdateLayeredWindow で反映。
// タイマーはアニメーション中のみ SetTimer し、終了後は必ず KillTimer する。
//
// 状態遷移 (Claude: session_id 単位):
//   (なし) --UserPromptSubmit--> Working --permission_prompt--> Waiting
//   Waiting --PostToolUse/UserPromptSubmit--> Working
//   Working/Waiting --Stop--> Celebrating --(バウンド+約5秒)--> (エントリ削除=Idle)
//   SessionEnd --> エントリ削除
//
// Codex は provider+session+turn 単位の別イベント系 (dwData 20〜27) で入ってくる。
// Claude 側の判定・定数を共通化せず、provider 固有の最小分岐だけを持つ:
//   Stop は即完了ではなく 5 秒 quiet grace の completion candidate
//   (Claude の Finalizing 約2秒とは別理由・別定数。docs/DESIGN_DECISIONS.md 参照)
//
// 表示は常にペット1匹。優先度 Waiting > Celebrating > Working、同率は最新イベントの session。
//
// C# 5 (.NET Framework 4.8 同梱 csc.exe) でビルド可能な構文のみ使用。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClaudePet
{
    internal static class Native
    {
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_LAYERED = 0x80000;
        public const int WS_EX_TRANSPARENT = 0x20;
        public const int WS_EX_TOOLWINDOW = 0x80;
        public const int WS_EX_NOACTIVATE = 0x8000000;
        public const int WS_EX_TOPMOST = 0x8;

        public const int WM_DESTROY = 0x0002;
        public const int WM_CLOSE = 0x0010;
        public const int WM_TIMER = 0x0113;
        public const int WM_COPYDATA = 0x004A;

        public const int SW_SHOWNOACTIVATE = 4;
        public const int ULW_ALPHA = 2;
        public const byte AC_SRC_OVER = 0;
        public const byte AC_SRC_ALPHA = 1;

        public const int SWP_NOSIZE = 0x0001;
        public const int SWP_NOACTIVATE = 0x0010;
        public const int SWP_NOZORDER = 0x0004;

        public const uint SOUND_DEFAULT = 0x00000000;      // 完了音 (既定のビープ)
        public const uint SOUND_EXCLAMATION = 0x00000030;  // 確認待ち音 (Windows 警告音・完了音と別)

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x; public int y; public POINT(int ax, int ay) { x = ax; y = ay; } }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx; public int cy; public SIZE(int w, int h) { cx = w; cy = h; } }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left; public int top; public int right; public int bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll")]
        public static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

        [DllImport("user32.dll")]
        public static extern bool MessageBeep(uint uType);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        public static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);
    }

    // Hook Adapter から届く正規化イベント (WM_COPYDATA の dwData)
    internal static class PetEvent
    {
        public const int TaskComplete = 1;      // Stop
        public const int PromptSubmit = 2;      // UserPromptSubmit
        public const int PermissionPrompt = 3;  // Notification (notification_type=permission_prompt)
        public const int Activity = 4;          // PostToolUse (Waiting解除用)
        public const int SessionEnd = 5;        // SessionEnd
        public const int TaskCreated = 6;       // TaskCreated (extra=task_id)
        public const int TaskCompleted = 7;     // TaskCompleted (extra=task_id)
        public const int TaskSnapshot = 8;      // TodoWrite スナップショット (extra="c/i/t")
        public const int TaskRemoved = 9;       // Task削除/キャンセル (extra=task_id)
        public const int TaskInProgress = 10;   // Task着手 (extra=task_id)

        // --- Codex 専用 (CodexPetNotify.exe)。1〜10 の意味は変更しない ---
        public const int CodexFirst = 20;
        public const int CodexPromptSubmit = 20;   // UserPromptSubmit (新 turn)
        public const int CodexActivity = 21;       // PostToolUse
        public const int CodexPlanSnapshot = 22;   // PostToolUse(update_plan) (extra="c/i/t")
        public const int CodexPermission = 23;     // PermissionRequest
        public const int CodexStop = 24;           // Stop (completion candidate)
        public const int CodexSessionEnd = 25;     // SessionEnd
        public const int CodexSubagentStart = 26;  // SubagentStart
        public const int CodexSubagentStop = 27;   // SubagentStop (root completion にしない)
        public const int CodexLast = 27;
    }

    internal sealed class Session
    {
        public const int Working = 1;
        public const int Waiting = 2;
        public const int Celebrating = 3;
        public const int StoppedIncomplete = 4; // 明確に未完のまま停止 (「途中で止まったよ」)
        public const int Indeterminate = 5;     // 完了とも未完とも断定できない (「終わったか確認してね」)
        public const int Finalizing = 6;        // Stop受信直後のgrace。遅延した完了イベントを待つ内部状態

        public string Project;
        public int State;
        public long LastSeq;        // 単調増加のイベント順序
        public DateTime LastAtUtc;  // 古いエントリ掃除用
        public DateTime LastActivityUtc; // 最後に実tool activityを観測した時刻 (「● 活動中」用)

        // 「依頼 (Request)」= そのセッションで最後に UserPromptSubmit が来てから
        // Stop までの1ターン。新しい依頼が始まったら進捗はリセットする。
        public long RequestGen;

        // 依頼全体の推定進捗 (Claude 自身が認識している Task 群に基づく)。
        // TodoWrite スナップショット (SnapTotal>=0) を優先し、無ければ
        // TaskCreated/TaskCompleted の一意 task_id 集合から計算する。
        public HashSet<string> CreatedIds;    // 遅延生成。上限 MaxTaskIds
        public HashSet<string> CompletedIds;
        public HashSet<string> InProgressIds;
        public int SnapTotal = -1;            // -1 = スナップショット無し
        public int SnapDone;
        public int SnapInProg;

        // --- Codex 専用フィールド (Claude セッションでは常に既定値のまま) ---
        // Claude の session semantics に turn_id は無いため共通化せず、Codex にだけ持たせる。
        public bool IsCodex;
        public string TurnId = "";        // 現在 turn。これ以外の遅延イベントは UI へ反映しない
        public bool TurnHasSubagent;      // この turn で SubagentStart/Stop を観測した
        public DateTime CodexGraceDueUtc; // Codex Stop quiet grace の満了時刻 (未設定=MinValue)

        public const int MaxTaskIds = 256;

        public void ResetRequest()
        {
            RequestGen++;
            SnapTotal = -1;
            SnapDone = 0;
            SnapInProg = 0;
            if (CreatedIds != null) { CreatedIds.Clear(); CompletedIds.Clear(); InProgressIds.Clear(); }
        }

        public void EnsureTaskSets()
        {
            if (CreatedIds == null)
            {
                CreatedIds = new HashSet<string>();
                CompletedIds = new HashSet<string>();
                InProgressIds = new HashSet<string>();
            }
        }

        // 依頼全体の推定進捗。total<=0 なら進捗表示なし。
        // in_progress は 0.5 個分の完了として加点する (作業着手を反映しつつ盛りすぎない)
        public void GetProgress(out int done, out int inProg, out int total)
        {
            if (SnapTotal > 0) { done = SnapDone; inProg = SnapInProg; total = SnapTotal; return; }
            if (SnapTotal < 0 && CreatedIds != null && CreatedIds.Count > 0)
            {
                done = 0; inProg = 0;
                foreach (string id in CompletedIds) { if (CreatedIds.Contains(id)) done++; }
                foreach (string id in InProgressIds)
                { if (CreatedIds.Contains(id) && !CompletedIds.Contains(id)) inProg++; }
                total = CreatedIds.Count;
                return;
            }
            done = 0; inProg = 0; total = 0;
        }

        public int ProgressPercent()
        {
            int done, inProg, total;
            GetProgress(out done, out inProg, out total);
            if (total <= 0) return -1;
            int pct = (int)Math.Floor((done + 0.5 * inProg) * 100.0 / total);
            return pct > 100 ? 100 : pct;
        }

        // 完了ゲート: Todo を使った依頼で未完項目が残っているか
        public bool HasUnfinishedTasks()
        {
            int done, inProg, total;
            GetProgress(out done, out inProg, out total);
            return total > 0 && done < total;
        }
    }

    internal sealed class PetApp
    {
        private const string WndClassName = "ClaudeDesktopPetWnd";

        private static readonly IntPtr TimerBounce = new IntPtr(1);
        private static readonly IntPtr TimerRevert = new IntPtr(2);
        private static readonly IntPtr TimerActivity = new IntPtr(3); // 「● 活動中」消灯用 one-shot
        private static readonly IntPtr TimerFinalize = new IntPtr(4); // Claude: Stop後grace用 one-shot
        private static readonly IntPtr TimerCodexGrace = new IntPtr(5); // Codex: Stop quiet grace用 one-shot

        private const int BounceIntervalMs = 30;
        private const int RevertDelayMs = 3700;        // 完了バウンド後のメッセージ表示継続時間
        private const int RevertDelayHiddenMs = 5000;  // 非表示セッションの完了エントリ掃除
        private const int FinalizeGraceMs = 2000;      // Claude: Stop後、遅延完了イベントを待つ時間
        // Codex: Stop は完了確定ではない。実測で最初の Stop が約1.88秒後に別 hook から
        // continuation され、同一 turn の 2 回目 Stop が来るケースがあるため、
        // Stop を completion candidate とし静穏 5 秒で確定する (Claude の 2 秒とは別物)。
        private const int CodexQuietGraceMs = 5000;
        private static readonly TimeSpan ActivityTtl = TimeSpan.FromSeconds(15); // 「● 活動中」の表示保持

        private const int MaxSessions = 8;             // 通常同時利用は数セッション。無制限に増やさない
        private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(4);
        // 「途中で止まったよ」はユーザーが気付くまで残すが、終了済みセッションが
        // 他のアクティブセッションの表示を塞ぎ続けないよう 10 分で自動的に消す
        private static readonly TimeSpan IncompleteNoticeTtl = TimeSpan.FromMinutes(10);

        private const int AnimNone = 0;
        private const int AnimCelebrate = 1;  // 3回大きくバウンド
        private const int AnimLight = 2;      // 2回控えめにピョコッ

        private IntPtr _hwnd;
        private Native.WndProcDelegate _wndProc; // GC防止のためフィールドで保持
        private float _scale = 1f;

        private int _winW;
        private int _winH;
        private int _baseX;
        private int _baseY;

        private int _animMode = AnimNone;
        private int _bounceFrame;

        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>();
        private long _seq;
        private string _shownKey = ""; // 直前に描画した (session|state|project|progress)。同一なら再描画しない

        // celebration/SessionEnd で削除した直後のセッション。async hook の遅延イベント
        // (数秒遅れの PostToolUse 等) が幽霊セッションとして再作成されるのを防ぐ。
        // PromptSubmit / PermissionPrompt が来たら正当な再開として解除する。
        private readonly Dictionary<string, DateTime> _recentlyEnded = new Dictionary<string, DateTime>();
        private static readonly TimeSpan TombstoneTtl = TimeSpan.FromSeconds(120);
        private const int MaxTombstones = 8;

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "ClaudeDesktopPet_SingleInstance", out createdNew))
            {
                if (!createdNew) return; // 二重起動防止
                new PetApp().Run();
            }
        }

        private void Run()
        {
            EnableDpiAwareness();

            float dpi = 96f;
            try { dpi = Native.GetDpiForSystem(); } catch { }
            _scale = dpi / 96f;

            _winW = S(280);
            _winH = S(252); // 進捗付き Working ピルが収まる高さ (キャラ位置は下端基準で不変)

            Native.RECT work = new Native.RECT();
            Native.SystemParametersInfo(0x0030 /*SPI_GETWORKAREA*/, 0, ref work, 0);
            _baseX = work.right - _winW - S(12);
            _baseY = work.bottom - _winH - S(8);

            _wndProc = WndProc;
            var wc = new Native.WNDCLASSEX();
            wc.cbSize = Marshal.SizeOf(typeof(Native.WNDCLASSEX));
            wc.lpfnWndProc = _wndProc;
            wc.hInstance = Native.GetModuleHandle(null);
            wc.lpszClassName = WndClassName;
            ushort atom = Native.RegisterClassEx(ref wc);
            PetDebug("startup: RegisterClassEx atom=" + atom + " err=" + Marshal.GetLastWin32Error() +
                " dpi=" + _scale + " work=" + _baseX + "," + _baseY + " win=" + _winW + "x" + _winH);

            _hwnd = Native.CreateWindowEx(
                Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
                Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
                WndClassName, "Claude Pet", Native.WS_POPUP,
                _baseX, _baseY, _winW, _winH,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            PetDebug("startup: CreateWindowEx hwnd=0x" + _hwnd.ToInt64().ToString("X") +
                " err=" + Marshal.GetLastWin32Error());
            if (_hwnd == IntPtr.Zero)
            {
                // ウィンドウを作れないまま生き続けると、mutex を握った不可視プロセスが
                // 以後の自動起動を全て弾いてしまう。即終了して次の自動起動に任せる。
                return;
            }

            RenderCurrent(true);
            Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            PetDebug("startup: shown");

            TrimMemory();

            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
        }

        private static void EnableDpiAwareness()
        {
            try
            {
                // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
                if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4)))
                    Native.SetProcessDPIAware();
            }
            catch { try { Native.SetProcessDPIAware(); } catch { } }
        }

        private int S(int v) { return (int)Math.Round(v * _scale); }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Native.WM_COPYDATA:
                    HandleCopyData(lParam);
                    return new IntPtr(1);

                case Native.WM_TIMER:
                    if (wParam == TimerBounce) OnBounceTick();
                    else if (wParam == TimerRevert) OnRevert();
                    else if (wParam == TimerActivity) OnActivityExpire();
                    else if (wParam == TimerFinalize) OnFinalizeTick();
                    else if (wParam == TimerCodexGrace) OnCodexGraceTick();
                    return IntPtr.Zero;

                case Native.WM_CLOSE:
                    Native.DestroyWindow(hWnd);
                    return IntPtr.Zero;

                case Native.WM_DESTROY:
                    Native.KillTimer(_hwnd, TimerBounce);
                    Native.KillTimer(_hwnd, TimerRevert);
                    Native.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
            return Native.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        // ---- イベント処理 -------------------------------------------------

        private void HandleCopyData(IntPtr lParam)
        {
            try
            {
                var cds = (Native.COPYDATASTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.COPYDATASTRUCT));
                int eventType = (int)cds.dwData.ToInt64();

                // Claude (1〜10) は従来どおり3フィールド。Codex (20〜27) だけ
                // 4フィールド目に turn_id を持つ。既存 payload 契約は変更しない。
                bool isCodex = (eventType >= PetEvent.CodexFirst && eventType <= PetEvent.CodexLast);

                string sessionId = "";
                string project = "";
                string extra = "";
                string turnId = "";
                if (cds.cbData > 0 && cds.lpData != IntPtr.Zero)
                {
                    byte[] buf = new byte[cds.cbData];
                    Marshal.Copy(cds.lpData, buf, 0, cds.cbData);
                    string payload = System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0');
                    string[] parts = payload.Split(new char[] { '\n' }, isCodex ? 4 : 3);
                    if (parts.Length >= 2) { sessionId = parts[0]; project = parts[1]; }
                    else project = payload;
                    if (parts.Length >= 3) extra = parts[2];
                    if (isCodex && parts.Length >= 4) turnId = parts[3];
                }
                if (sessionId.Length == 0) sessionId = "(default)";

                if (isCodex) OnCodexEvent(eventType, sessionId, project, extra, turnId);
                else OnEvent(eventType, sessionId, project, extra);
                PetDebug("recv ev=" + eventType + " sess=" + sessionId + " -> shown=" + _shownKey);
            }
            catch (Exception ex) { PetDebug("recv-error " + ex.GetType().Name + " " + ex.Message); }
        }

        // bin\debug.flag が存在するときだけ bin\debug.log へ追記 (通常は完全に無効)。
        // 併走する helper プロセスと衝突しないよう FileShare.ReadWrite の追記ストリームを使う。
        private static void PetDebug(string line)
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                if (!System.IO.File.Exists(System.IO.Path.Combine(dir, "debug.flag"))) return;
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
                    DateTime.Now.ToString("HH:mm:ss.fff") + " [pet] " + line + "\r\n");
                using (var fs = new System.IO.FileStream(System.IO.Path.Combine(dir, "debug.log"),
                    System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite))
                {
                    fs.Write(bytes, 0, bytes.Length);
                }
            }
            catch { }
        }

        private void OnEvent(int eventType, string sessionId, string project, string extra)
        {
            _seq++;
            Session s;
            _sessions.TryGetValue(sessionId, out s);

            bool becameWaiting = false;
            bool becameCelebrating = false;

            switch (eventType)
            {
                case PetEvent.PromptSubmit:
                    _recentlyEnded.Remove(sessionId); // 正当な再開
                    s = Upsert(sessionId, s, project, true);
                    s.State = Session.Working;
                    s.ResetRequest(); // 新しい依頼: 前回依頼の進捗を破棄
                    break;

                case PetEvent.Activity:
                case PetEvent.TaskCreated:
                case PetEvent.TaskCompleted:
                case PetEvent.TaskSnapshot:
                case PetEvent.TaskRemoved:
                case PetEvent.TaskInProgress:
                    // 終了系表示中の残イベントでは状態を戻さない
                    // (「途中で止まったよ」等は次の UserPromptSubmit まで維持する)
                    if (s != null && (s.State == Session.Celebrating ||
                        s.State == Session.StoppedIncomplete || s.State == Session.Indeterminate))
                    { Touch(s, project); break; }
                    // 終了直後のセッションの遅延イベントは幽霊再作成になるので無視
                    if (s == null && IsTombstoned(sessionId)) return;
                    // PostToolUse 系の cwd はツール実行ディレクトリで揺れることがあるため
                    // project 名は上書きしない (最初に確定した名前を維持)
                    s = Upsert(sessionId, s, project, false);
                    if (s.State != Session.Finalizing) s.State = Session.Working;
                    s.LastActivityUtc = DateTime.UtcNow; // 「● 活動中」
                    ApplyTaskEvent(s, eventType, extra);
                    // grace 中に全完了が確定したら celebration へ (「途中で止まったよ」を見せない)
                    if (s.State == Session.Finalizing && !s.HasUnfinishedTasks())
                    {
                        s.State = Session.Celebrating;
                        becameCelebrating = true;
                    }
                    break;

                case PetEvent.PermissionPrompt:
                    _recentlyEnded.Remove(sessionId); // 正当な再開
                    bool wasWaiting = (s != null && s.State == Session.Waiting);
                    s = Upsert(sessionId, s, project, true);
                    s.State = Session.Waiting;
                    becameWaiting = !wasWaiting; // debounce: 既にWaitingなら音もアニメも出さない
                    break;

                case PetEvent.TaskComplete:
                    // debounce: 同一依頼の重複 Stop / 遅延 Stop で二重処理しない
                    if (s != null && (s.State == Session.Celebrating || s.State == Session.Finalizing))
                    { Touch(s, project); break; }
                    if (s == null && IsTombstoned(sessionId)) return;
                    s = Upsert(sessionId, s, project, true);
                    if (s.HasUnfinishedTasks())
                    {
                        // 即「途中で止まったよ」と断定せず、遅延した完了イベントを
                        // grace window (2秒) だけ待つ。UI は Working 表示のまま。
                        s.State = Session.Finalizing;
                        Native.SetTimer(_hwnd, TimerFinalize, FinalizeGraceMs, IntPtr.Zero);
                    }
                    else
                    {
                        s.State = Session.Celebrating;
                        becameCelebrating = true;
                    }
                    break;

                case PetEvent.SessionEnd:
                    // Celebrating 中は即削除しない (claude -p 終了時の SessionEnd が
                    // 完了通知を打ち消してしまうため)。Finalizing 中も判定前なので残す
                    // (削除すると完了/未完の通知が一切出なくなる)。
                    if (s != null && s.State != Session.Celebrating && s.State != Session.Finalizing)
                    {
                        _sessions.Remove(sessionId);
                        AddTombstone(sessionId);
                    }
                    break;

                default:
                    return;
            }
            if (s != null) Touch(s, project);

            AfterEvent(sessionId, becameCelebrating, becameWaiting);
        }

        // イベント処理後の共通後処理 (prune / 音 / 再描画 / アニメ)。
        // Claude・Codex どちらの handler からも同じ意味で呼ぶ。
        private void AfterEvent(string sessionKey, bool becameCelebrating, bool becameWaiting)
        {
            Prune();

            // 音は表示中かどうかに関わらず状態遷移時のみ1回
            if (becameCelebrating) Native.MessageBeep(Native.SOUND_DEFAULT);
            else if (becameWaiting) Native.MessageBeep(Native.SOUND_EXCLAMATION);

            string displayId = ComputeDisplaySession();
            RenderCurrent(false);

            bool displayed = (displayId == sessionKey);
            if (becameCelebrating)
            {
                if (displayed) StartBounce(AnimCelebrate);
                else Native.SetTimer(_hwnd, TimerRevert, RevertDelayHiddenMs, IntPtr.Zero);
            }
            else if (becameWaiting && displayed)
            {
                StartBounce(AnimLight);
            }
        }

        // ---- Codex イベント処理 (provider + session + turn) ------------------
        //
        // Claude 側 (OnEvent) とは意図的に分離している。Codex の Stop は完了確定では
        // なく、interrupt では Stop 自体が来ない (Phase F 実測)。Claude の完了判定を
        // Codex 仕様へ共通化しないことで、Claude の挙動を一切変えずに済ませている。
        private void OnCodexEvent(int eventType, string sessionId, string project, string extra, string turnId)
        {
            _seq++;
            string key = CodexKey(sessionId);
            Session s;
            _sessions.TryGetValue(key, out s);

            bool becameWaiting = false;

            switch (eventType)
            {
                case PetEvent.CodexPromptSubmit:
                    // 新 turn。interrupt 後に残っていた古い progress / permission /
                    // activity / Stop candidate をここで全て破棄する。
                    _recentlyEnded.Remove(key);
                    s = Upsert(key, s, project, true);
                    s.IsCodex = true;
                    s.State = Session.Working;
                    s.ResetRequest();
                    s.TurnId = turnId;
                    s.TurnHasSubagent = false;
                    s.CodexGraceDueUtc = DateTime.MinValue;
                    s.LastActivityUtc = DateTime.MinValue;
                    break;

                case PetEvent.CodexActivity:
                case PetEvent.CodexPlanSnapshot:
                    if (turnId.Length == 0) return; // turn 不明は fail-closed
                    // 終了系表示中の残イベントでは状態を戻さない
                    if (s != null && IsTerminal(s.State)) { Touch(s, project); break; }
                    if (s == null && IsTombstoned(key)) return;
                    if (s != null && !TurnMatches(s, turnId)) return; // 旧 turn の遅延イベント
                    // PostToolUse の cwd は揺れうるため project 名は上書きしない
                    s = Upsert(key, s, project, false);
                    s.IsCodex = true;
                    if (s.TurnId.Length == 0) s.TurnId = turnId; // 初観測 turn を採用
                    s.LastActivityUtc = DateTime.UtcNow;
                    // work activity が来た = まだ終わっていない。completion candidate を破棄する
                    s.CodexGraceDueUtc = DateTime.MinValue;
                    s.State = Session.Working;
                    // subagent を含む turn では update_plan の origin を証明できないため
                    // root progress へ適用しない (SubagentStop 後もその turn 中は再開しない)
                    if (eventType == PetEvent.CodexPlanSnapshot && !s.TurnHasSubagent)
                        ApplyTaskEvent(s, PetEvent.TaskSnapshot, extra);
                    break;

                case PetEvent.CodexPermission:
                    if (turnId.Length == 0) return;
                    _recentlyEnded.Remove(key);
                    if (s != null && !TurnMatches(s, turnId)) return;
                    bool wasWaiting = (s != null && s.State == Session.Waiting);
                    s = Upsert(key, s, project, true);
                    s.IsCodex = true;
                    if (s.TurnId.Length == 0) s.TurnId = turnId;
                    s.State = Session.Waiting;
                    s.CodexGraceDueUtc = DateTime.MinValue;
                    becameWaiting = !wasWaiting; // debounce
                    break;

                case PetEvent.CodexStop:
                    if (turnId.Length == 0) return;
                    if (s == null && IsTombstoned(key)) return;
                    if (s != null && !TurnMatches(s, turnId)) return;
                    if (s != null && IsTerminal(s.State)) { Touch(s, project); break; }
                    s = Upsert(key, s, project, true);
                    s.IsCodex = true;
                    if (s.TurnId.Length == 0) s.TurnId = turnId;
                    // Stop = completion candidate。UI は Working のまま静穏 5 秒待つ。
                    // 同一 turn の 2 回目 Stop なら 5 秒を最初から数え直す。
                    s.State = Session.Finalizing;
                    s.CodexGraceDueUtc = DateTime.UtcNow.AddMilliseconds(CodexQuietGraceMs);
                    break;

                case PetEvent.CodexSubagentStart:
                case PetEvent.CodexSubagentStop:
                    // PreToolUse/PostToolUse schema には agent_id が無く、tool event の
                    // origin を structured metadata だけで証明できない。fail-closed で、
                    // この turn は root progress を信用しない (SubagentStop も完了にしない)。
                    if (turnId.Length == 0) return;
                    if (s == null) return;                 // subagent だけで session を作らない
                    if (!TurnMatches(s, turnId)) return;
                    if (IsTerminal(s.State)) { Touch(s, project); break; }
                    s.TurnHasSubagent = true;
                    s.SnapTotal = -1; s.SnapDone = 0; s.SnapInProg = 0; // 表示済み progress も無効化
                    s.LastActivityUtc = DateTime.UtcNow;
                    break;

                case PetEvent.CodexSessionEnd:
                    // Celebrating / Finalizing 中は消さない (通知そのものが消えるため)。
                    // 削除により permission (Waiting) 状態も解除される。
                    if (s != null && s.State != Session.Celebrating && s.State != Session.Finalizing)
                    {
                        _sessions.Remove(key);
                        AddTombstone(key);
                        s = null;
                    }
                    break;

                default:
                    return;
            }
            if (s != null) Touch(s, project);

            ArmCodexGraceTimer();
            AfterEvent(key, false, becameWaiting); // Codex は Stop 直後に Celebrating しない
        }

        // provider 名前空間の分離。Claude の session_id と衝突しない内部 key。
        private static string CodexKey(string sessionId) { return "codex:" + sessionId; }

        private static bool TurnMatches(Session s, string turnId)
        {
            return s.TurnId.Length == 0 || s.TurnId == turnId;
        }

        private static bool IsTerminal(int state)
        {
            return state == Session.Celebrating ||
                   state == Session.StoppedIncomplete ||
                   state == Session.Indeterminate;
        }

        // Codex の quiet grace は session ごとに満了時刻を持ち、timer は最短期限へ
        // 1 本だけ張る (常時 timer / polling を増やさない)。
        private void ArmCodexGraceTimer()
        {
            DateTime next = DateTime.MaxValue;
            foreach (var kv in _sessions)
            {
                Session s = kv.Value;
                if (!s.IsCodex || s.State != Session.Finalizing) continue;
                if (s.CodexGraceDueUtc == DateTime.MinValue) continue;
                if (s.CodexGraceDueUtc < next) next = s.CodexGraceDueUtc;
            }
            Native.KillTimer(_hwnd, TimerCodexGrace);
            if (next == DateTime.MaxValue) return;
            double ms = (next - DateTime.UtcNow).TotalMilliseconds;
            if (ms < 30) ms = 30;
            Native.SetTimer(_hwnd, TimerCodexGrace, (uint)ms, IntPtr.Zero);
        }

        private void AddTombstone(string sessionId)
        {
            _recentlyEnded[sessionId] = DateTime.UtcNow;
            while (_recentlyEnded.Count > MaxTombstones)
            {
                string oldest = null;
                DateTime oldestAt = DateTime.MaxValue;
                foreach (var kv in _recentlyEnded)
                {
                    if (kv.Value < oldestAt) { oldestAt = kv.Value; oldest = kv.Key; }
                }
                if (oldest == null) break;
                _recentlyEnded.Remove(oldest);
            }
        }

        private bool IsTombstoned(string sessionId)
        {
            DateTime at;
            if (!_recentlyEnded.TryGetValue(sessionId, out at)) return false;
            if (DateTime.UtcNow - at > TombstoneTtl)
            {
                _recentlyEnded.Remove(sessionId);
                return false;
            }
            return true;
        }

        private static void ApplyTaskEvent(Session s, int eventType, string extra)
        {
            switch (eventType)
            {
                case PetEvent.TaskCreated:
                case PetEvent.TaskCompleted:
                case PetEvent.TaskInProgress:
                    if (string.IsNullOrEmpty(extra)) return;
                    s.EnsureTaskSets();
                    if (s.CreatedIds.Count >= Session.MaxTaskIds && !s.CreatedIds.Contains(extra)) return;
                    s.CreatedIds.Add(extra); // 完了/着手イベントが先に来ても total に数える (100% 超え防止)
                    if (eventType == PetEvent.TaskCompleted) s.CompletedIds.Add(extra);
                    else if (eventType == PetEvent.TaskInProgress) s.InProgressIds.Add(extra);
                    break;

                case PetEvent.TaskRemoved:
                    // Task 削除/キャンセルには対応する hook が無い (実測) ため、
                    // PostToolUse(TaskUpdate) 由来のこのイベントで total から除外する。
                    // これを怠ると「依頼完了なのに未完 Task が残る」false incomplete になる。
                    if (string.IsNullOrEmpty(extra) || s.CreatedIds == null) return;
                    s.CreatedIds.Remove(extra);
                    s.CompletedIds.Remove(extra);
                    s.InProgressIds.Remove(extra);
                    break;

                case PetEvent.TaskSnapshot:
                    // extra = "completed/in_progress/total"。TodoWrite の全量スナップショットなので冪等。
                    string[] nums = extra.Split('/');
                    if (nums.Length != 3) return;
                    int done, inProg, total;
                    if (!int.TryParse(nums[0], out done)) return;
                    if (!int.TryParse(nums[1], out inProg)) return;
                    if (!int.TryParse(nums[2], out total)) return;
                    if (total < 0 || done < 0 || inProg < 0 || done + inProg > total) return;
                    s.SnapTotal = total; // total=0 は「リストが空になった」= 進捗表示なし
                    s.SnapDone = done;
                    s.SnapInProg = inProg;
                    break;
            }
        }

        private Session Upsert(string sessionId, Session s, string project, bool projectAuthoritative)
        {
            if (s == null)
            {
                s = new Session();
                _sessions[sessionId] = s;
            }
            if (!string.IsNullOrEmpty(project) &&
                (projectAuthoritative || string.IsNullOrEmpty(s.Project)))
            {
                s.Project = project;
            }
            return s;
        }

        private void Touch(Session s, string project)
        {
            s.LastSeq = _seq;
            s.LastAtUtc = DateTime.UtcNow;
        }

        private void Prune()
        {
            List<string> remove = null;
            DateTime now = DateTime.UtcNow;
            foreach (var kv in _sessions)
            {
                TimeSpan age = now - kv.Value.LastAtUtc;
                bool stale = age > StaleAfter ||
                    ((kv.Value.State == Session.StoppedIncomplete ||
                      kv.Value.State == Session.Indeterminate) && age > IncompleteNoticeTtl);
                if (stale)
                {
                    if (remove == null) remove = new List<string>();
                    remove.Add(kv.Key);
                }
            }
            if (remove != null)
            {
                foreach (string k in remove)
                {
                    _sessions.Remove(k);
                    AddTombstone(k);
                }
            }

            while (_sessions.Count > MaxSessions)
            {
                string oldest = null;
                long oldestSeq = long.MaxValue;
                foreach (var kv in _sessions)
                {
                    if (kv.Value.LastSeq < oldestSeq) { oldestSeq = kv.Value.LastSeq; oldest = kv.Key; }
                }
                if (oldest == null) break;
                _sessions.Remove(oldest);
            }
        }

        // 表示priority: Waiting > Celebrating > Working。同率は最新イベントのsession。
        private string ComputeDisplaySession()
        {
            string bestId = null;
            int bestRank = 0;
            long bestSeq = -1;
            foreach (var kv in _sessions)
            {
                int rank = RankOf(kv.Value.State);
                if (rank > bestRank || (rank == bestRank && kv.Value.LastSeq > bestSeq))
                {
                    bestRank = rank;
                    bestSeq = kv.Value.LastSeq;
                    bestId = kv.Key;
                }
            }
            return bestId;
        }

        private static int RankOf(int state)
        {
            switch (state)
            {
                case Session.Waiting: return 3;
                case Session.StoppedIncomplete: return 3; // Waiting と同格 (要ユーザー確認)。同格は最新優先
                case Session.Indeterminate: return 3;
                case Session.Celebrating: return 2;
                case Session.Working: return 1;
                case Session.Finalizing: return 1; // ユーザーには Working として見せる
                default: return 0;
            }
        }

        // ---- 描画 ---------------------------------------------------------

        private void RenderCurrent(bool force)
        {
            string id = ComputeDisplaySession();
            Session s = (id != null) ? _sessions[id] : null;
            int pct = (s != null) ? s.ProgressPercent() : -1;

            // 「● 活動中」: Working/Finalizing 中で最近実 tool activity を観測した場合のみ
            bool active = false;
            if (s != null && (s.State == Session.Working || s.State == Session.Finalizing))
            {
                TimeSpan since = DateTime.UtcNow - s.LastActivityUtc;
                if (since < ActivityTtl)
                {
                    active = true;
                    // 期限が来たら消灯用に one-shot timer (常時 timer ではない)
                    int remainMs = (int)(ActivityTtl - since).TotalMilliseconds + 200;
                    Native.SetTimer(_hwnd, TimerActivity, (uint)remainMs, IntPtr.Zero);
                }
            }

            string key = (s == null) ? "idle"
                : id + "|" + s.State + "|" + (s.Project ?? "") + "|" + pct + "|" + (active ? 1 : 0);
            if (!force && key == _shownKey) return; // 同一表示なら再描画しない (PostToolUse連発対策)
            _shownKey = key;

            Bitmap bmp;
            if (s == null) bmp = PetRenderer.RenderIdle(_winW, _winH, _scale);
            else if (s.State == Session.Waiting) bmp = PetRenderer.RenderWaiting(_winW, _winH, _scale, s.Project);
            else if (s.State == Session.StoppedIncomplete) bmp = PetRenderer.RenderStopped(_winW, _winH, _scale, s.Project);
            else if (s.State == Session.Indeterminate) bmp = PetRenderer.RenderIndeterminate(_winW, _winH, _scale, s.Project);
            else if (s.State == Session.Celebrating) bmp = PetRenderer.RenderCelebrate(_winW, _winH, _scale, s.Project);
            else bmp = PetRenderer.RenderWorking(_winW, _winH, _scale, s.Project, pct, active);

            using (bmp) { ApplyBitmap(bmp, _baseX, _baseY); }

            if (_animMode == AnimNone) MoveTo(_baseX, _baseY);
        }

        // ---- アニメーション ------------------------------------------------

        private void StartBounce(int mode)
        {
            Native.KillTimer(_hwnd, TimerBounce);
            _animMode = mode;
            _bounceFrame = 0;
            Native.SetTimer(_hwnd, TimerBounce, BounceIntervalMs, IntPtr.Zero);
        }

        private void OnBounceTick()
        {
            int totalFrames = (_animMode == AnimCelebrate) ? 44 : 22; // 約1.3秒 / 約0.65秒
            int bounces = (_animMode == AnimCelebrate) ? 3 : 2;
            int amplitude = (_animMode == AnimCelebrate) ? S(22) : S(10);

            _bounceFrame++;
            if (_bounceFrame >= totalFrames)
            {
                Native.KillTimer(_hwnd, TimerBounce);
                MoveTo(_baseX, _baseY);
                bool wasCelebrate = (_animMode == AnimCelebrate);
                _animMode = AnimNone;
                if (wasCelebrate)
                {
                    // メッセージをしばらく表示してから静止状態へ戻す
                    Native.SetTimer(_hwnd, TimerRevert, RevertDelayMs, IntPtr.Zero);
                }
                return;
            }
            double t = (double)_bounceFrame / totalFrames;
            int offset = (int)(amplitude * Math.Abs(Math.Sin(t * Math.PI * bounces)));
            MoveTo(_baseX, _baseY - offset);
        }

        // Stop後grace満了: Finalizing のままのセッションを最終判定する
        private void OnFinalizeTick()
        {
            Native.KillTimer(_hwnd, TimerFinalize);
            FinalizeDue(false);
        }

        // Codex quiet grace 満了: 期限の来た Codex セッションだけ確定し、
        // 残りがあれば最短期限へ timer を張り直す (one-shot のまま)。
        private void OnCodexGraceTick()
        {
            Native.KillTimer(_hwnd, TimerCodexGrace);
            FinalizeDue(true);
            ArmCodexGraceTimer();
        }

        // Finalizing の確定処理。codex=false なら Claude の 2 秒 grace 満了、
        // codex=true なら Codex の 5 秒 quiet grace 満了分のみを対象にする。
        // 判定基準 (完了 / 未着手あり / in_progress のみ) は既存の完了哲学と同じ。
        private void FinalizeDue(bool codex)
        {
            bool celebrated = false;
            bool alerted = false;
            string celebratedId = null;
            DateTime now = DateTime.UtcNow;
            foreach (var kv in _sessions)
            {
                Session s = kv.Value;
                if (s.State != Session.Finalizing) continue;
                if (s.IsCodex != codex) continue;
                // Codex は session ごとに満了時刻を持つ (再 Stop で伸びる)。
                // 期限未設定 = candidate 無し。推測 timeout で確定させない。
                if (codex && s.CodexGraceDueUtc == DateTime.MinValue) continue;
                if (codex && s.CodexGraceDueUtc > now.AddMilliseconds(50)) continue;
                int done, inProg, total;
                s.GetProgress(out done, out inProg, out total);
                if (total <= 0 || done >= total)
                {
                    s.State = Session.Celebrating; // grace中の遅延イベントで完了が確定していた
                    celebrated = true;
                    celebratedId = kv.Key;
                }
                else if (total - done - inProg > 0)
                {
                    // 一度も着手されていない Task が残っている = 明確に未完
                    s.State = Session.StoppedIncomplete;
                    alerted = true;
                }
                else
                {
                    // 残りは in_progress のみ。status の更新忘れの可能性があり、
                    // 完了とも未完とも断定できない → 「終わったか確認してね」
                    s.State = Session.Indeterminate;
                    alerted = true;
                }
                s.CodexGraceDueUtc = DateTime.MinValue;
                _seq++;
                s.LastSeq = _seq;
                s.LastAtUtc = DateTime.UtcNow;
                PetDebug("finalize" + (codex ? ":codex" : "") + " sess=" + kv.Key +
                    " -> state=" + s.State + " (" + done + "+" + inProg + "ip/" + total + ")");
            }

            if (celebrated) Native.MessageBeep(Native.SOUND_DEFAULT);
            else if (alerted) Native.MessageBeep(Native.SOUND_EXCLAMATION);

            string displayId = ComputeDisplaySession();
            RenderCurrent(false);

            if (celebrated)
            {
                if (displayId == celebratedId) StartBounce(AnimCelebrate);
                else Native.SetTimer(_hwnd, TimerRevert, RevertDelayHiddenMs, IntPtr.Zero);
            }
            else if (alerted && displayId != null && _sessions.ContainsKey(displayId) &&
                (_sessions[displayId].State == Session.StoppedIncomplete ||
                 _sessions[displayId].State == Session.Indeterminate))
            {
                StartBounce(AnimLight);
            }
        }

        // 「● 活動中」の表示期限が切れたら消灯のため再描画する (one-shot)
        private void OnActivityExpire()
        {
            Native.KillTimer(_hwnd, TimerActivity);
            RenderCurrent(false);
        }

        private void OnRevert()
        {
            Native.KillTimer(_hwnd, TimerRevert);
            List<string> done = null;
            foreach (var kv in _sessions)
            {
                if (kv.Value.State == Session.Celebrating)
                {
                    if (done == null) done = new List<string>();
                    done.Add(kv.Key);
                }
            }
            if (done != null)
            {
                foreach (string k in done)
                {
                    _sessions.Remove(k);
                    AddTombstone(k);
                }
            }

            RenderCurrent(false); // 残っている Waiting / Working セッションの表示へ戻る
            TrimMemory();
        }

        private void MoveTo(int x, int y)
        {
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        }

        private void ApplyBitmap(Bitmap bmp, int x, int y)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                oldBitmap = Native.SelectObject(memDc, hBitmap);

                var size = new Native.SIZE(bmp.Width, bmp.Height);
                var srcPos = new Native.POINT(0, 0);
                var topPos = new Native.POINT(x, y);
                var blend = new Native.BLENDFUNCTION();
                blend.BlendOp = Native.AC_SRC_OVER;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;

                Native.UpdateLayeredWindow(_hwnd, screenDc, ref topPos, ref size,
                    memDc, ref srcPos, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) Native.SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) Native.DeleteObject(hBitmap);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1)); }
            catch { }
        }
    }

    // キャラクター描画。画像差し替えは このクラスの実装を置き換えるだけでよい。
    internal static class PetRenderer
    {
        private static readonly Color BodyColor = Color.FromArgb(255, 214, 68);
        private static readonly Color BodyEdge = Color.FromArgb(216, 172, 40);
        private static readonly Color BeakColor = Color.FromArgb(240, 144, 50);
        private static readonly Color CheekColor = Color.FromArgb(90, 255, 150, 120);

        public static Bitmap RenderIdle(int w, int h, float scale)
        {
            Bitmap bmp = NewCanvas(w, h);
            using (Graphics g = NewGraphics(bmp))
            {
                DrawChick(g, w, h, scale);
                DrawLabel(g, w, h, scale, "Claude");
            }
            return bmp;
        }

        // Working: 吹き出しなしの控えめなピル + 「作業中…」。
        // pct>=0 のときだけ依頼全体の推定進捗 (bar + 全体 推定N%) を追加表示する。
        // Task 件数 (3/5 等) は表示しない。active=true で「● 活動中」を添える。
        public static Bitmap RenderWorking(int w, int h, float scale, string project, int pct, bool active)
        {
            Bitmap bmp = NewCanvas(w, h);
            using (Graphics g = NewGraphics(bmp))
            {
                DrawChick(g, w, h, scale);
                DrawWorkingPill(g, w, scale, project, pct, active);
            }
            return bmp;
        }

        // 完了とも未完とも断定できない: 曖昧なまま確認を促す
        public static Bitmap RenderIndeterminate(int w, int h, float scale, string project)
        {
            Bitmap bmp = NewCanvas(w, h);
            using (Graphics g = NewGraphics(bmp))
            {
                DrawChick(g, w, h, scale);
                DrawPill(g, w, scale, "終わったか確認してね", project,
                    Color.FromArgb(242, 255, 248, 228),   // 背景: 淡い黄 (Waiting より薄い)
                    Color.FromArgb(255, 214, 178, 96),    // 枠: 薄いオレンジ
                    Color.FromArgb(255, 150, 110, 40),    // 文字: 茶寄りオレンジ
                    true, 14.5f);
            }
            return bmp;
        }

        // Stop したが Todo 未完: 完了とは言わない控えめな注意表示
        public static Bitmap RenderStopped(int w, int h, float scale, string project)
        {
            Bitmap bmp = NewCanvas(w, h);
            using (Graphics g = NewGraphics(bmp))
            {
                DrawChick(g, w, h, scale);
                DrawPill(g, w, scale, "途中で止まったよ", project,
                    Color.FromArgb(242, 245, 236, 226),   // 背景: 淡いベージュ
                    Color.FromArgb(255, 196, 156, 108),   // 枠: 茶系
                    Color.FromArgb(255, 140, 96, 48),     // 文字: 濃い茶
                    true, 16f);
            }
            return bmp;
        }

        // Working 用ピル: 作業中… / [progress bar + 全体 推定 N%] / [● 活動中] / [project]
        private static void DrawWorkingPill(Graphics g, int w, float scale, string project, int pct, bool active)
        {
            Color bg = Color.FromArgb(205, 250, 250, 248);
            Color border = Color.FromArgb(255, 210, 205, 196);
            Color textColor = Color.FromArgb(255, 100, 92, 84);
            Color subColor = Color.FromArgb(255, 130, 122, 112);
            Color trackColor = Color.FromArgb(255, 228, 224, 217);
            Color fillColor = Color.FromArgb(255, 232, 163, 66);
            Color activeColor = Color.FromArgb(255, 74, 152, 88);

            bool hasProject = !string.IsNullOrEmpty(project);
            float cursor = (pct >= 0) ? 66f : 32f; // ヘッダ (+bar+%) の下端
            float actY = cursor;
            if (active) cursor += 17f;
            float projY = cursor;
            if (hasProject) cursor += 19f;

            float bx = 24 * scale;
            float by = 10 * scale;
            float bw = w - 48 * scale;
            float bh = (cursor + 7f) * scale;

            using (GraphicsPath path = RoundedRect(bx, by, bw, bh, 12 * scale))
            {
                using (var bgBrush = new SolidBrush(bg)) g.FillPath(bgBrush, path);
                using (var borderPen = new Pen(border, 1.5f * scale)) g.DrawPath(borderPen, path);
            }

            using (var fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Center;

                using (var font = new Font("Yu Gothic UI", 15f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(textColor))
                    g.DrawString("作業中…", font, brush, w / 2f, by + 8 * scale, fmt);

                if (pct >= 0)
                {
                    // progress bar (常時animationなし。イベント時の再描画のみ)
                    float barW = bw - 56 * scale;
                    float barH = 8 * scale;
                    float barX = w / 2f - barW / 2f;
                    float barY = by + 33 * scale;
                    using (GraphicsPath track = RoundedRect(barX, barY, barW, barH, barH / 2f))
                    using (var trackBrush = new SolidBrush(trackColor))
                        g.FillPath(trackBrush, track);
                    float ratio = pct / 100f;
                    if (ratio > 1f) ratio = 1f;
                    float fillW = barW * ratio;
                    if (fillW >= barH) // 極端に短いと角丸が破綻するため最小幅まで描かない
                    {
                        using (GraphicsPath fill = RoundedRect(barX, barY, fillW, barH, barH / 2f))
                        using (var fillBrush = new SolidBrush(fillColor))
                            g.FillPath(fillBrush, fill);
                    }

                    using (var font = new Font("Yu Gothic UI", 13.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(textColor))
                        g.DrawString("全体 推定 " + pct + "%", font, brush, w / 2f, by + 45 * scale, fmt);
                }

                if (active)
                {
                    // 進捗率とは無関係の「最近実 tool activity を観測した」表示
                    using (var font = new Font("Yu Gothic UI", 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(activeColor))
                        g.DrawString("● 活動中", font, brush, w / 2f, by + actY * scale, fmt);
                }

                if (hasProject)
                {
                    using (var font = new Font("Yu Gothic UI", 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(subColor))
                        g.DrawString(project, font, brush, w / 2f, by + projY * scale, fmt);
                }
            }
        }

        // Waiting: 警告色の吹き出し + 「確認して！」 (完了と明確に区別)
        public static Bitmap RenderWaiting(int w, int h, float scale, string project)
        {
            Bitmap bmp = NewCanvas(w, h);
            using (Graphics g = NewGraphics(bmp))
            {
                DrawChick(g, w, h, scale);
                DrawPill(g, w, scale, "確認して！", project,
                    Color.FromArgb(242, 255, 243, 214),   // 背景: 淡い黄
                    Color.FromArgb(255, 226, 160, 66),    // 枠: オレンジ
                    Color.FromArgb(255, 168, 96, 24),     // 文字: 濃いオレンジ
                    true, 18f);
            }
            return bmp;
        }

        public static Bitmap RenderCelebrate(int w, int h, float scale, string project)
        {
            Bitmap bmp = NewCanvas(w, h);
            using (Graphics g = NewGraphics(bmp))
            {
                DrawChick(g, w, h, scale);
                DrawPill(g, w, scale, "終わったよ！", project,
                    Color.FromArgb(238, 255, 255, 255),   // 背景: 白
                    Color.FromArgb(255, 205, 198, 188),   // 枠: グレー
                    Color.FromArgb(255, 70, 62, 54),      // 文字: 濃いグレー
                    true, 19f);
            }
            return bmp;
        }

        private static Bitmap NewCanvas(int w, int h)
        {
            return new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        }

        private static Graphics NewGraphics(Bitmap bmp)
        {
            Graphics g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            return g;
        }

        private static void DrawChick(Graphics g, int w, int h, float scale)
        {
            float cx = w / 2f;
            float cy = h - 78 * scale;
            float r = 36 * scale;

            // 足
            using (var pen = new Pen(BeakColor, 2.5f * scale))
            {
                g.DrawLine(pen, cx - 12 * scale, cy + r - 4 * scale, cx - 14 * scale, cy + r + 8 * scale);
                g.DrawLine(pen, cx + 12 * scale, cy + r - 4 * scale, cx + 14 * scale, cy + r + 8 * scale);
            }

            // 体
            using (var body = new SolidBrush(BodyColor))
                g.FillEllipse(body, cx - r, cy - r, r * 2, r * 2);
            using (var edge = new Pen(BodyEdge, 2f * scale))
                g.DrawEllipse(edge, cx - r, cy - r, r * 2, r * 2);

            // 羽
            using (var wing = new Pen(BodyEdge, 2f * scale))
            {
                g.DrawArc(wing, cx - r + 4 * scale, cy - 6 * scale, 18 * scale, 20 * scale, 60, 180);
                g.DrawArc(wing, cx + r - 22 * scale, cy - 6 * scale, 18 * scale, 20 * scale, -60, 180);
            }

            // 目
            using (var eye = new SolidBrush(Color.FromArgb(40, 34, 28)))
            {
                float er = 4f * scale;
                g.FillEllipse(eye, cx - 14 * scale - er, cy - 10 * scale - er, er * 2, er * 2);
                g.FillEllipse(eye, cx + 14 * scale - er, cy - 10 * scale - er, er * 2, er * 2);
            }

            // ほっぺ
            using (var cheek = new SolidBrush(CheekColor))
            {
                float chr = 5.5f * scale;
                g.FillEllipse(cheek, cx - 24 * scale - chr, cy + 2 * scale - chr, chr * 2, chr * 2);
                g.FillEllipse(cheek, cx + 24 * scale - chr, cy + 2 * scale - chr, chr * 2, chr * 2);
            }

            // くちばし
            using (var beak = new SolidBrush(BeakColor))
            {
                PointF[] tri = new PointF[]
                {
                    new PointF(cx - 6 * scale, cy - 2 * scale),
                    new PointF(cx + 6 * scale, cy - 2 * scale),
                    new PointF(cx, cy + 8 * scale)
                };
                g.FillPolygon(beak, tri);
            }
        }

        private static void DrawLabel(Graphics g, int w, int h, float scale, string text)
        {
            using (var font = new Font("Yu Gothic UI", 10.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(Color.FromArgb(150, 90, 84, 76)))
            using (var fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Center;
                g.DrawString(text, font, brush, w / 2f, h - 24 * scale, fmt);
            }
        }

        // キャラ上部の吹き出し/ピル。tail=true で吹き出しのしっぽ付き。
        private static void DrawPill(Graphics g, int w, float scale, string mainText, string project,
            Color bg, Color border, Color textColor, bool tail, float mainSize)
        {
            float bx = 24 * scale;
            float by = 14 * scale;
            float bw = w - 48 * scale;
            bool hasProject = !string.IsNullOrEmpty(project);
            float bh = (hasProject ? 76 : 56) * scale;
            float rad = 12 * scale;

            using (GraphicsPath path = RoundedRect(bx, by, bw, bh, rad))
            {
                if (tail)
                {
                    path.AddPolygon(new PointF[]
                    {
                        new PointF(w / 2f - 8 * scale, by + bh - 1),
                        new PointF(w / 2f + 8 * scale, by + bh - 1),
                        new PointF(w / 2f, by + bh + 10 * scale)
                    });
                }
                using (var bgBrush = new SolidBrush(bg))
                    g.FillPath(bgBrush, path);
                using (var borderPen = new Pen(border, 1.5f * scale))
                    g.DrawPath(borderPen, path);
            }

            using (var fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Center;
                float mainY = by + (hasProject ? 12 : 14) * scale;
                using (var font = new Font("Yu Gothic UI", mainSize * scale, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(textColor))
                {
                    g.DrawString(mainText, font, brush, w / 2f, mainY, fmt);
                }
                if (hasProject)
                {
                    using (var font = new Font("Yu Gothic UI", 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(Color.FromArgb(255, 130, 122, 112)))
                    {
                        g.DrawString(project, font, brush, w / 2f, by + 42 * scale, fmt);
                    }
                }
            }
        }

        private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
        {
            var path = new GraphicsPath();
            float d = r * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
