// ClaudePet.exe - 常駐デスクトップペット本体
// 純Win32 (P/Invoke) + layered window。アイドル時は GetMessage でブロックし CPU 0%。
// 描画は System.Drawing で ARGB ビットマップを合成し UpdateLayeredWindow で反映。
// タイマーはアニメーション中のみ SetTimer し、終了後は必ず KillTimer する。
//
// 状態遷移 (Claude: session 単位 / Codex: session + turn 単位):
//   (なし) --UserPromptSubmit--> Working --permission_prompt--> Waiting
//   Waiting --PostToolUse/UserPromptSubmit--> Working
//   Working/Waiting --root Stop--> Finalizing --(20秒 quiet)--> Celebrating
//   Finalizing --継続イベント--> Working (completion candidate を取消す)
//   SessionEnd --> エントリ削除 (Celebrating / Finalizing 中は消さない)
//
// 見える状態は 3 つだけ: Idle / 作業中… / 終わったよ！。
// 旧「incomplete」表示は廃止した。完了と判定できない停止は何も出さず Idle へ戻る。
// 確認要求 UI・警告音・activity indicator も出さない。
//
// progress と completion は完全に独立している:
//   progress   = structured plan/task の status 件数から出す「依頼全体の推定進捗」。
//                valid total >= 2 のときだけ % を出す (1 工程だけの plan は根拠が弱い)。
//                完了判定には一切使わない。
//   completion = root Stop + CompletionQuietMs の静穏だけで決める。
//                tracker の件数・完了状況は完了の証拠にしない。
//
// 「終わったよ！」の意味は「成果物が正しい」ではなく、
// 「root Stop が来て、その後 20 秒間その作業が再開されなかった」。
//
// Codex は provider+session+turn 単位の別イベント系 (dwData 20〜27) で入ってくる。
// quiet window の長さと deadline 管理だけ共通化し、state 遷移・turn 分離は
// provider ごとに分けたまま (Codex の old-turn 遅延イベントを混ぜない)。
//
// 表示は常にペット1匹。優先度 active (Working/Waiting/Finalizing) >
// 完了通知 (Celebrating)、同率は最新イベントの session。完了通知を出すのは
// 満了時に他の active が無いときだけで、過去の完了で進行中の作業を隠さない。
//
// Z-order: 通常ウィンドウより常に前面 (TOPMOST)。ただし WS_EX_TOPMOST は
// 作成時の一度きりでは不十分で、fullscreen 遷移等で OS が topmost band から
// 外すことがある (実運用で確認)。表示内容が変わった時と明示操作の時だけ
// HWND_TOPMOST + SWP_NOACTIVATE で再保証する (polling も常時 timer も無し)。
// focus は決して奪わない (WS_EX_NOACTIVATE / click-through 維持)。
//
// 通知領域 (system tray) に管理アイコンを 1 つ持つ (Shell_NotifyIcon)。
// 左クリック = 最前面へ復帰 (hidden なら再表示)。右クリック = menu
// (表示 / 隠す / 最前面に戻す / 終了)。「隠す」は visual hide であって
// 監視停止ではない: hooks 受信・進捗・完了判定・session 管理は継続し、
// 再表示時にその時点の最新 state を描く。hidden 中は完了音も鳴らさない。
// taskbar ボタンと Alt+Tab には出さない (WS_EX_TOOLWINDOW 維持)。
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

        public const int WM_NULL = 0x0000;
        public const int WM_DESTROY = 0x0002;
        public const int WM_CLOSE = 0x0010;
        public const int WM_COMMAND = 0x0111;
        public const int WM_TIMER = 0x0113;
        public const int WM_COPYDATA = 0x004A;
        public const int WM_CONTEXTMENU = 0x007B;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONUP = 0x0205;

        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int ULW_ALPHA = 2;
        public const byte AC_SRC_OVER = 0;
        public const byte AC_SRC_ALPHA = 1;

        public const int SWP_NOSIZE = 0x0001;
        public const int SWP_NOMOVE = 0x0002;
        public const int SWP_NOZORDER = 0x0004;
        public const int SWP_NOACTIVATE = 0x0010;
        public const int SWP_SHOWWINDOW = 0x0040;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        // ---- 通知領域 (Shell_NotifyIcon) ----
        public const uint NIM_ADD = 0;
        public const uint NIM_DELETE = 2;
        public const uint NIF_MESSAGE = 0x1;
        public const uint NIF_ICON = 0x2;
        public const uint NIF_TIP = 0x4;

        // ---- tray context menu ----
        public const uint MF_STRING = 0x0;
        public const uint MF_GRAYED = 0x1;
        public const uint MF_SEPARATOR = 0x800;
        public const uint TPM_RIGHTBUTTON = 0x2;

        public const uint SOUND_DEFAULT = 0x00000000;      // 完了音 (既定のビープ)。完了時だけ鳴らす

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

        // NOTIFYICONDATA の V1 (Win2000) レイアウト。szTip までしか使わないので
        // それ以降のフィールドは持たない (cbSize が一致していれば shell は受理する)。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
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

        // ---- 通知領域アイコンと tray menu 用 ----
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        public static extern bool TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

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
        public const int SessionMetadata = 11;  // SessionStart (extra=model identifier)
                                                // 表示用 metadata だけ。進捗・完了判定には影響させない

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

        // Activity(4 / 21) の extra に載る固定 marker。structured tracker
        // (TodoWrite / Task 系 / update_plan) を観測したが status を解析できなかった、
        // という事実だけを伝える。本文は一切含まない。
        // src/Notify.cs / src/CodexNotify.cs の同名定数と一致させること。
        public const string StructuredObserved = "structured-observed";
    }

    internal sealed class Session
    {
        public const int Working = 1;
        public const int Waiting = 2;
        public const int Celebrating = 3;       // 完了通知。約5秒で消える一時表示
        // 4 = StoppedIncomplete / 5 = Indeterminate は incomplete 表示と一緒に廃止した。
        // 完了と言えない停止は何も表示しないので、この 2 状態は不要になった。
        // 値は再利用しない (旧 debug ログとの取り違えを避けるため)。
        public const int Finalizing = 6;        // root Stop 後の quiet window。表示は「作業中…」のまま
        public const int MetadataOnly = 7;      // SessionStart だけ受けた休眠状態。
                                                // 表示しないし active にも数えない

        public string Project;
        public int State;
        public long LastSeq;        // 単調増加のイベント順序
        public DateTime LastAtUtc;  // 古いエントリ掃除用

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

        public const int MaxTaskIds = 256;

        // この依頼/turn で structured task tracker (Task/TodoWrite/update_plan) を
        // 一度でも観測したか。「今 valid な snapshot があるか」ではない点に注意。
        // status の解析に失敗した観測 (StructuredObserved marker) でも true になり、
        // ResetRequest() 以外では false へ戻さない (fail-closed)。
        public bool SawStructuredTasks;

        // root Stop 後の quiet window 満了時刻 (未設定 = completion candidate 無し)。
        // provider 共通。継続イベントで取消し、Stop 再受信で張り直す。
        public DateTime QuietDueUtc;

        // 表示用 model identifier (sanitize 済み)。空 = 不明。
        // Claude は SessionStart、Codex は UserPromptSubmit の metadata 由来。
        // 依頼単位ではなく session 単位なので ResetRequest() では消さない。
        public string ModelId = "";

        public void ResetRequest()
        {
            RequestGen++;
            SnapTotal = -1;
            SnapDone = 0;
            SnapInProg = 0;
            SawStructuredTasks = false;
            QuietDueUtc = DateTime.MinValue;
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

        // 「依頼全体の工程表」の現在地。今実行中の 1 タスクの進み具合ではない。
        // in_progress の工程は 0.5 工程ぶん進んだものとして数える
        // (着手を反映しつつ盛りすぎない、全体進捗の粗い推定)。
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

        // % を出すのに最低限必要な工程数。total=1 の plan は
        // 「今やっている 1 個」でしかなく、依頼全体の進捗としての根拠が弱い。
        public const int MinProgressTotal = 2;

        public int ProgressPercent()
        {
            int done, inProg, total;
            GetProgress(out done, out inProg, out total);
            // tracker 自体は保持したまま、% だけ出さない
            if (total < MinProgressTotal) return -1;
            int pct = (int)Math.Floor((done + 0.5 * inProg) * 100.0 / total);
            return pct > 100 ? 100 : pct;
        }
    }

    internal sealed class PetApp
    {
        private const string WndClassName = "ClaudeDesktopPetWnd";

        private static readonly IntPtr TimerBounce = new IntPtr(1);
        private static readonly IntPtr TimerRevert = new IntPtr(2);
        private static readonly IntPtr TimerQuiet = new IntPtr(4); // root Stop 後の quiet window 用 one-shot

        private const int BounceIntervalMs = 30;
        private const int RevertDelayMs = 3700;        // 完了バウンド後のメッセージ表示継続時間
        // completion = root Stop + この静穏時間。provider 共通の唯一の完了条件。
        // Stop は「終わった宣言」ではなく candidate で、継続イベントが来たら取消す。
        // Claude 2 秒 / Codex 5 秒だった旧 grace は、意味が同じになったのでこれに統一した。
        private const int CompletionQuietMs = 20000;

        private const int MaxSessions = 8;             // 通常同時利用は数セッション。無制限に増やさない
        private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(4);

        private const int AnimNone = 0;
        private const int AnimCelebrate = 1;  // 3回大きくバウンド

        // ---- 通知領域 (tray) ----
        private const int WmTrayIcon = 0x8001;    // WM_APP + 1: tray callback
        private const uint TrayIconId = 1;
        private const int CmdShowPet = 1001;      // tray menu: ヒヨコを表示
        private const int CmdHidePet = 1002;      // tray menu: ヒヨコを隠す
        private const int CmdBringToFront = 1003; // tray menu: 最前面に戻す
        private const int CmdExitPet = 1004;      // tray menu: ClaudePetを終了

        private IntPtr _hwnd;
        private Native.WndProcDelegate _wndProc; // GC防止のためフィールドで保持
        private float _scale = 1f;

        private int _winW;
        private int _winH;
        private int _baseX;
        private int _baseY;

        private int _animMode = AnimNone;
        private int _bounceFrame;

        // 明示 hide (tray の「ヒヨコを隠す」)。visual だけの hide で、hooks 受信・
        // 進捗・完了判定・session 管理は全て継続する。hidden 中は描画と完了音を止める。
        private bool _petVisible = true;
        private bool _trayAdded;
        private IntPtr _trayIconHandle;

        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>();
        private long _seq;
        private string _shownKey = ""; // 直前に描画した (session|state|project|pct|meta|others)。同一なら再描画しない

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

            _winW = S(280); // ピル内テキストの幅で決まる (文字サイズを変えないので不変)
            // 上余白10 + 最大ピル109 (ヘッダ+meta+bar+%+project) + 間隔10 + キャラ〜ラベル74。
            // activity indicator 行の廃止で 1 行分縮んだ (221 -> 204)。
            // キャラは下端基準なので画面上の位置は変わらない。
            _winH = S(204);

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
            AddTrayIcon(); // 失敗しても Pet 本体は通常動作 (fail-soft)

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
                    else if (wParam == TimerQuiet) OnQuietTick();
                    return IntPtr.Zero;

                case WmTrayIcon:
                    OnTrayCallback(lParam);
                    return IntPtr.Zero;

                case Native.WM_COMMAND:
                    // tray context menu の選択 (TrackPopupMenuEx が owner へ post する)
                    OnTrayCommand(unchecked((int)wParam.ToInt64()) & 0xFFFF);
                    return IntPtr.Zero;

                case Native.WM_CLOSE:
                    Native.DestroyWindow(hWnd);
                    return IntPtr.Zero;

                case Native.WM_DESTROY:
                    RemoveTrayIcon(); // menu 経由でも WM_CLOSE 経由でも tray を残さない
                    Native.KillTimer(_hwnd, TimerBounce);
                    Native.KillTimer(_hwnd, TimerRevert);
                    Native.KillTimer(_hwnd, TimerQuiet);
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

            bool metadataTouched = false; // SessionStart は共通 Touch() を使わない

            switch (eventType)
            {
                case PetEvent.PromptSubmit:
                    _recentlyEnded.Remove(sessionId); // 正当な再開
                    s = Upsert(sessionId, s, project, true);
                    s.State = Session.Working;
                    s.ResetRequest(); // 新しい依頼: 前回依頼の進捗と完了候補を破棄
                    break;

                case PetEvent.Activity:
                case PetEvent.TaskCreated:
                case PetEvent.TaskCompleted:
                case PetEvent.TaskSnapshot:
                case PetEvent.TaskRemoved:
                case PetEvent.TaskInProgress:
                    // 完了通知の表示中は蒸し返さない (次の UserPromptSubmit まで待つ)
                    if (s != null && IsTerminal(s.State)) { Touch(s, project); break; }
                    // 終了直後のセッションの遅延イベントは幽霊再作成になるので無視。
                    // SessionStart で model だけ復帰した MetadataOnly も、tombstone が
                    // 残っている間は旧 session 由来の遅延イベントとみなして Working にしない。
                    if ((s == null || s.State == Session.MetadataOnly) && IsTombstoned(sessionId)) return;
                    // PostToolUse 系の cwd はツール実行ディレクトリで揺れることがあるため
                    // project 名は上書きしない (最初に確定した名前を維持)
                    s = Upsert(sessionId, s, project, false);
                    // work continuation = まだ終わっていない。completion candidate は
                    // 延長ではなく取消して Working へ戻す。次に完了できるのは
                    // 新しい root Stop が来てからになる。
                    s.State = Session.Working;
                    s.QuietDueUtc = DateTime.MinValue;
                    // status を読めなかった tracker 観測。進捗値は触らず事実だけ残す
                    if (extra == PetEvent.StructuredObserved) s.SawStructuredTasks = true;
                    ApplyTaskEvent(s, eventType, extra);
                    break;

                case PetEvent.SessionMetadata:
                    // SessionStart。model だけを覚える。UserPromptSubmit より先に来るので
                    // これだけでは「作業中…」を出さないし active にも数えない。
                    // 既存 session へ compact 等で再度来ても state / 進捗 / 完了候補は触らない。
                    // tombstone 中でも model metadata 自体は受け取る (MetadataOnly で作る)。
                    // ただし tombstone は消さない。SessionStart は「新しい作業が始まった」
                    // という信号ではなく、ここで解除すると旧 session 由来の遅延
                    // PostToolUse で Working へ昇格してしまう (async hook の race)。
                    // 解除するのは UserPromptSubmit / PermissionPrompt だけ。
                    bool newMetadataSession = (s == null);
                    s = Upsert(sessionId, s, project, false);
                    if (s.State == 0) s.State = Session.MetadataOnly;
                    s.ModelId = extra;
                    // model 更新だけで表示優先度 (LastSeq) を奪わない。
                    // 新規 MetadataOnly だけ eviction 順序用に LastSeq を持たせ、
                    // LastAtUtc は stale 管理のため常に更新する。
                    if (newMetadataSession) s.LastSeq = _seq;
                    s.LastAtUtc = DateTime.UtcNow;
                    metadataTouched = true;
                    break;

                case PetEvent.PermissionPrompt:
                    _recentlyEnded.Remove(sessionId); // 正当な再開
                    s = Upsert(sessionId, s, project, true);
                    // 完全 auto 運用: 確認 UI は出さない。state は互換のため Waiting のまま
                    // 残すが、描画は「作業中…」と同じ。音もバウンドも無し。
                    // permission 待ち = 作業継続なので completion candidate は取消す。
                    s.State = Session.Waiting;
                    s.QuietDueUtc = DateTime.MinValue;
                    break;

                case PetEvent.TaskComplete:
                    // 判定済みの表示中は蒸し返さない
                    if (s != null && IsTerminal(s.State)) { Touch(s, project); break; }
                    // tombstone 中の MetadataOnly への Stop も旧 session 由来の遅延とみなす
                    if ((s == null || s.State == Session.MetadataOnly) && IsTombstoned(sessionId)) return;
                    s = Upsert(sessionId, s, project, true);
                    // root Stop = completion candidate。tracker の状態は一切見ない。
                    // UI は「作業中…」のまま quiet window を待つ。
                    // 同じ session の 2 回目の Stop は最新 Stop から数え直す。
                    s.State = Session.Finalizing;
                    s.QuietDueUtc = DateTime.UtcNow.AddMilliseconds(CompletionQuietMs);
                    break;

                case PetEvent.SessionEnd:
                    // SessionEnd 単体は完了の根拠にしない。Celebrating 中は即削除しない
                    // (claude -p 終了時の SessionEnd が完了通知を打ち消してしまうため)。
                    // Finalizing 中も残して quiet window を継続させる
                    // (削除すると Stop 済みの作業が完了通知されなくなる)。
                    // tombstone 中の MetadataOnly への SessionEnd は旧 session の遅延分なので
                    // 無視し、resume で覚えた model を消さない。
                    if (s != null && s.State == Session.MetadataOnly && IsTombstoned(sessionId)) return;
                    if (s != null && s.State != Session.Celebrating && s.State != Session.Finalizing)
                    {
                        _sessions.Remove(sessionId);
                        AddTombstone(sessionId);
                    }
                    break;

                default:
                    return;
            }
            if (s != null && !metadataTouched) Touch(s, project);

            ArmQuietTimer();
            // Stop 直後に Celebrating しない。完了を決められるのは FinalizeDue だけ
            AfterEvent();
        }

        // イベント処理後の共通後処理 (prune / 再描画)。
        // 音とバウンドは完了時だけで、それを出せるのは FinalizeDue のみ。
        // 確認要求・警告の音/アニメは完全 auto 運用のため廃止した。
        private void AfterEvent()
        {
            Prune();
            RenderCurrent(false);
        }

        // ---- Codex イベント処理 (provider + session + turn) ------------------
        //
        // Claude 側 (OnEvent) とは意図的に分離したまま。共通なのは quiet window の
        // 長さ (CompletionQuietMs) と deadline 管理だけで、turn 分離・old-turn の
        // 破棄・subagent fail-closed といった Codex 固有の判断はここにしか無い。
        // interrupt では Stop 自体が来ない (Phase F 実測) ため、推測 timeout で
        // 完了させることはしない。
        private void OnCodexEvent(int eventType, string sessionId, string project, string extra, string turnId)
        {
            _seq++;
            string key = CodexKey(sessionId);
            Session s;
            _sessions.TryGetValue(key, out s);

            switch (eventType)
            {
                case PetEvent.CodexPromptSubmit:
                    // 新 turn。interrupt 後に残っていた古い progress / permission /
                    // activity / Stop candidate をここで全て破棄する。
                    _recentlyEnded.Remove(key);
                    s = Upsert(key, s, project, true);
                    s.IsCodex = true;
                    s.State = Session.Working;
                    s.ResetRequest();   // 古い completion candidate もここで破棄される
                    s.TurnId = turnId;
                    s.TurnHasSubagent = false;
                    // model は turn ごとに届く。空なら前 turn の値を残さず不明へ戻す。
                    s.ModelId = extra;
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
                    // work activity が来た = まだ終わっていない。completion candidate を破棄する
                    s.QuietDueUtc = DateTime.MinValue;
                    s.State = Session.Working;
                    // status を読めなかった update_plan 観測。進捗値は触らず事実だけ残す
                    if (extra == PetEvent.StructuredObserved) s.SawStructuredTasks = true;
                    if (eventType == PetEvent.CodexPlanSnapshot)
                    {
                        // tracker を使った事実は subagent 抑制中でも失わない
                        s.SawStructuredTasks = true;
                        // ただし subagent を含む turn では origin を証明できないので
                        // root progress へは適用しない (SubagentStop 後も再開しない)
                        if (!s.TurnHasSubagent) ApplyTaskEvent(s, PetEvent.TaskSnapshot, extra);
                    }
                    break;

                case PetEvent.CodexPermission:
                    if (turnId.Length == 0) return;
                    _recentlyEnded.Remove(key);
                    if (s != null && !TurnMatches(s, turnId)) return;
                    s = Upsert(key, s, project, true);
                    s.IsCodex = true;
                    if (s.TurnId.Length == 0) s.TurnId = turnId;
                    // 確認 UI は出さない (作業中…と同じ描画。音・バウンド無し)。
                    // candidate 取消のため state だけ Waiting を維持する。
                    s.State = Session.Waiting;
                    s.QuietDueUtc = DateTime.MinValue;
                    break;

                case PetEvent.CodexStop:
                    if (turnId.Length == 0) return;
                    if (s == null && IsTombstoned(key)) return;
                    if (s != null && !TurnMatches(s, turnId)) return;
                    if (s != null && IsTerminal(s.State)) { Touch(s, project); break; }
                    s = Upsert(key, s, project, true);
                    s.IsCodex = true;
                    if (s.TurnId.Length == 0) s.TurnId = turnId;
                    // Stop = completion candidate。UI は「作業中…」のまま静穏を待つ。
                    // 同一 turn の 2 回目 Stop なら quiet window を最初から数え直す。
                    s.State = Session.Finalizing;
                    s.QuietDueUtc = DateTime.UtcNow.AddMilliseconds(CompletionQuietMs);
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
                    // subagent の出入りも work continuation。candidate を取消す
                    if (s.State == Session.Finalizing) s.State = Session.Working;
                    s.QuietDueUtc = DateTime.MinValue;
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

            ArmQuietTimer();
            AfterEvent(); // Codex も Stop 直後に Celebrating しない (FinalizeDue が決める)
        }

        // provider 名前空間の分離。Claude の session_id と衝突しない内部 key。
        private static string CodexKey(string sessionId) { return "codex:" + sessionId; }

        private static bool TurnMatches(Session s, string turnId)
        {
            return s.TurnId.Length == 0 || s.TurnId == turnId;
        }

        // 表示上「終わった通知を出している」state。今は完了通知だけ。
        private static bool IsTerminal(int state)
        {
            return state == Session.Celebrating;
        }

        // quiet window の満了時刻は session ごとに持ち、timer は最短期限へ 1 本だけ
        // 張る (常時 timer / polling を増やさない)。provider は問わない。
        private void ArmQuietTimer()
        {
            DateTime next = DateTime.MaxValue;
            foreach (var kv in _sessions)
            {
                Session s = kv.Value;
                if (s.State != Session.Finalizing) continue;
                if (s.QuietDueUtc == DateTime.MinValue) continue;
                if (s.QuietDueUtc < next) next = s.QuietDueUtc;
            }
            Native.KillTimer(_hwnd, TimerQuiet);
            if (next == DateTime.MaxValue) return;
            double ms = (next - DateTime.UtcNow).TotalMilliseconds;
            if (ms < 30) ms = 30;
            Native.SetTimer(_hwnd, TimerQuiet, (uint)ms, IntPtr.Zero);
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
                    s.SawStructuredTasks = true;
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
                    // snapshot を正常に解析できた = tracker を使っている。件数は問わない
                    // (total=0 のリスト全消しでも「tracker なし依頼」へは格下げしない)。
                    s.SawStructuredTasks = true;
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
                // 終了系の session は FinalizeDue / OnRevert がその場で片付けるので、
                // ここに残るのは「動いているはずなのに長時間音沙汰が無い」ものだけ。
                if (now - kv.Value.LastAtUtc <= StaleAfter) continue;
                if (remove == null) remove = new List<string>();
                remove.Add(kv.Key);
            }
            if (remove != null)
            {
                foreach (string k in remove)
                {
                    _sessions.Remove(k);
                    AddTombstone(k);
                }
            }

            // 上限超過時はまず「表示用 metadata しか持たない session」を捨てる。
            // 失うのは model 表示だけで済むが、実作業中の session を
            // metadata のために失うと通知そのものが消えるため。
            while (_sessions.Count > MaxSessions)
            {
                string victim = null;
                long victimSeq = long.MaxValue;
                foreach (var kv in _sessions)
                {
                    if (kv.Value.State != Session.MetadataOnly) continue;
                    if (kv.Value.LastSeq < victimSeq) { victimSeq = kv.Value.LastSeq; victim = kv.Key; }
                }
                if (victim == null)
                {
                    // metadata-only が 1 件も無いときだけ従来どおり最古を捨てる
                    foreach (var kv in _sessions)
                    {
                        if (kv.Value.LastSeq < victimSeq) { victimSeq = kv.Value.LastSeq; victim = kv.Key; }
                    }
                }
                if (victim == null) break;
                _sessions.Remove(victim);
            }
        }

        // 表示priority: active (Working/Finalizing/Waiting) > 完了通知 (Celebrating)。
        // 同率は最新イベントの session。該当なし = null で呼び出し側は Idle を描く。
        // 過去の完了通知が進行中の作業を隠さないことを優先する。
        private string ComputeDisplaySession()
        {
            string bestId = null;
            int bestRank = 0;
            long bestSeq = -1;
            foreach (var kv in _sessions)
            {
                int rank = RankOf(kv.Value.State);
                if (rank <= 0) continue; // metadata-only 等は表示候補にしない
                if (rank > bestRank || (rank == bestRank && kv.Value.LastSeq > bestSeq))
                {
                    bestRank = rank;
                    bestSeq = kv.Value.LastSeq;
                    bestId = kv.Key;
                }
            }
            return bestId;
        }

        // 「他に動いている session」の数え方。作業中と見なせる state だけ。
        // Finalizing (root Stop 後の quiet window) はユーザーから見て「作業中…」
        // のままなので active に数える。完了通知と metadata-only は含めない。
        private static bool IsActive(int state)
        {
            return state == Session.Working || state == Session.Finalizing ||
                   state == Session.Waiting;
        }

        // 現在表示中の session 以外で active な session 数。
        private int OtherActiveCount(string shownId)
        {
            int n = 0;
            foreach (var kv in _sessions)
            {
                if (kv.Key == shownId) continue;
                if (IsActive(kv.Value.State)) n++;
            }
            return n;
        }

        private static int RankOf(int state)
        {
            switch (state)
            {
                // 今動いている作業が最優先 (Waiting / Finalizing も描画は「作業中…」)
                case Session.Working: return 2;
                case Session.Waiting: return 2;
                case Session.Finalizing: return 2;
                // 完了通知は約5秒で消える一時表示。active より下に置き、
                // 過去の「終わったよ！」が進行中の作業を隠さないようにする。
                case Session.Celebrating: return 1;
                default: return 0; // MetadataOnly 等は表示候補にしない
            }
        }

        // ---- 描画 ---------------------------------------------------------

        private void RenderCurrent(bool force)
        {
            // 明示 hide 中は描画しない。state 更新だけが続き、Show 時に
            // force 描画でその時点の最新 state に追いつく。
            if (!_petVisible) return;
            string id = ComputeDisplaySession();
            Session s = (id != null) ? _sessions[id] : null;
            int pct = (s != null) ? s.ProgressPercent() : -1;

            // provider + model の 1 行と、他に動いている session 数。
            // どちらも完了判定には一切影響しない表示だけの情報。
            string meta = (s == null) ? "" : PetRenderer.MetaLine(s.IsCodex, s.ModelId);
            int others = (s == null) ? 0 : OtherActiveCount(id);

            // 別 session の開始/終了で +N だけが変わる場合や、後から model が
            // 届いた場合も再描画させるため、key に含める。
            string key = (s == null) ? "idle"
                : id + "|" + s.State + "|" + (s.Project ?? "") + "|" + pct
                  + "|" + meta + "|" + others;
            if (!force && key == _shownKey) return; // 同一表示なら再描画しない (PostToolUse連発対策)
            _shownKey = key;

            // visible state は 3 つだけ: Idle / 作業中… / 終わったよ！。
            // Waiting も Finalizing (quiet window 中) も「作業中…」として描く。
            Bitmap bmp;
            if (s == null) bmp = PetRenderer.RenderIdle(_winW, _winH, _scale);
            else if (s.State == Session.Celebrating)
                bmp = PetRenderer.RenderCelebrate(_winW, _winH, _scale, s.Project, meta, others);
            else bmp = PetRenderer.RenderWorking(_winW, _winH, _scale, s.Project, pct, meta, others);

            using (bmp) { ApplyBitmap(bmp, _baseX, _baseY); }

            if (_animMode == AnimNone) MoveTo(_baseX, _baseY);
            // 表示内容が実際に変わった時だけ TOPMOST を再保証する (event-driven のみ)
            EnsureTopmost();
        }

        // ---- アニメーション ------------------------------------------------

        private void StartBounce(int mode)
        {
            Native.KillTimer(_hwnd, TimerBounce);
            // 直前の完了通知の revert が残っていると、新しい通知が数百 ms で
            // 消えてしまう。表示時間は通知ごとに数え直す。
            Native.KillTimer(_hwnd, TimerRevert);
            _animMode = mode;
            _bounceFrame = 0;
            Native.SetTimer(_hwnd, TimerBounce, BounceIntervalMs, IntPtr.Zero);
        }

        private void OnBounceTick()
        {
            // アニメは完了の 3 回バウンドだけ (警告系の軽バウンドは廃止)
            const int totalFrames = 44; // 約1.3秒
            const int bounces = 3;
            int amplitude = S(11); // キャラ半分に合わせた跳ね幅

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

        // quiet window 満了: 期限の来た session だけ確定し、残りがあれば
        // 最短期限へ timer を張り直す (one-shot のまま)。
        private void OnQuietTick()
        {
            Native.KillTimer(_hwnd, TimerQuiet);
            FinalizeDue();
            ArmQuietTimer();
        }

        // quiet window の確定処理。completion の根拠は
        // 「root Stop を受けた後 CompletionQuietMs のあいだ作業が再開されなかった」
        // ことだけで、structured tracker の件数・完了状況は一切見ない。
        // これは「成果物が正しい」という意味ではなく、Pet が本文を読まずに
        // 判定できる範囲での「一連の作業の終了」でしかない。
        //
        // 他に active な session があるときは完了通知を出さず静かに片付ける。
        // 完全 auto 運用では進行中の作業の表示を横取りしない方が有益で、
        // 後から通知を再キューすることもしない (音も鳴らさない)。
        private void FinalizeDue()
        {
            DateTime now = DateTime.UtcNow;
            List<string> due = null;
            foreach (var kv in _sessions)
            {
                Session s = kv.Value;
                if (s.State != Session.Finalizing) continue;
                // 期限未設定 = candidate 無し。推測 timeout で確定させない
                if (s.QuietDueUtc == DateTime.MinValue) continue;
                if (s.QuietDueUtc > now.AddMilliseconds(50)) continue;
                if (due == null) due = new List<string>();
                due.Add(kv.Key);
            }
            if (due == null) return;

            // 同時に満了したときは古い方から処理する。先に見る側からは残りが
            // まだ active に見えるので、通知が残るのは最新の作業になる。
            due.Sort(delegate(string a, string b)
            {
                return _sessions[a].LastSeq.CompareTo(_sessions[b].LastSeq);
            });

            bool celebrated = false;
            foreach (string key in due)
            {
                Session s;
                if (!_sessions.TryGetValue(key, out s)) continue;
                s.QuietDueUtc = DateTime.MinValue;
                if (OtherActiveCount(key) > 0)
                {
                    // 進行中の作業があるので完了通知は出さない。エントリはその場で
                    // 片付け、遅延イベントで幽霊復活しないよう tombstone を残す。
                    _sessions.Remove(key);
                    AddTombstone(key);
                    PetDebug("finalize sess=" + key + " -> suppressed (other active)");
                    continue;
                }
                s.State = Session.Celebrating;
                _seq++;
                s.LastSeq = _seq;
                s.LastAtUtc = DateTime.UtcNow;
                celebrated = true;
                PetDebug("finalize sess=" + key + " -> state=" + s.State);
            }

            // 音とバウンドは実際に完了通知を出すときだけ。明示 hide 中は
            // ユーザーが意図的に Pet を消しているので音も鳴らさない (後で再生もしない)。
            if (celebrated && _petVisible) Native.MessageBeep(Native.SOUND_DEFAULT);
            RenderCurrent(false);
            PetDebug("finalize-render shown=" + _shownKey);
            if (celebrated)
            {
                if (_petVisible) StartBounce(AnimCelebrate);
                else
                {
                    // hidden 中でも Celebrating エントリは通常と同じ寿命で片付ける
                    // (再表示した時に過去の完了通知を再生しないため)
                    PetDebug("celebrate-muted (hidden)");
                    Native.SetTimer(_hwnd, TimerRevert, RevertDelayMs, IntPtr.Zero);
                }
            }
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

            RenderCurrent(false); // 残っている作業中セッションか Idle の表示へ戻る
            TrimMemory();
        }

        // 位置だけを動かす (bounce の 30ms tick 用)。Z-order はここでは触らない
        // (TOPMOST の再保証は EnsureTopmost に分離。毎 tick assert しない)。
        private void MoveTo(int x, int y)
        {
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        }

        // ---- Z-order / 表示制御 --------------------------------------------

        // TOPMOST の再保証。WS_EX_TOPMOST は作成時の一度きりでは不十分で、
        // fullscreen 遷移・Win+D・secure desktop 等で OS が topmost band から
        // 外すことがある (実運用で VS Code の背面へ回る現象を確認)。
        // polling はせず、表示内容が実際に変わった時と明示操作の時だけ呼ぶ。
        // SWP_NOACTIVATE で focus は奪わない。他の TOPMOST アプリと
        // 争い続ける実装はしない (押しのけられたら tray の「最前面に戻す」で復帰)。
        private void EnsureTopmost()
        {
            if (!_petVisible) return;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE |
                Native.SWP_SHOWWINDOW);
        }

        private void ShowPet()
        {
            if (!_petVisible)
            {
                _petVisible = true;
                // hidden 中は描画を止めていたので、まず今の最新 state を描いてから見せる
                // (過去の通知の再生ではなく、現在の state をそのまま出す)
                RenderCurrent(true);
                Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            }
            EnsureTopmost();
            PetDebug("show-pet shown=" + _shownKey);
        }

        private void HidePet()
        {
            if (!_petVisible) return;
            _petVisible = false;
            if (_animMode != AnimNone)
            {
                // bounce 途中なら打ち切る。Celebrating の掃除 (OnRevert) は
                // 本来 bounce 完了後に schedule されるので、ここで代わりに張る。
                Native.KillTimer(_hwnd, TimerBounce);
                _animMode = AnimNone;
                Native.SetTimer(_hwnd, TimerRevert, RevertDelayMs, IntPtr.Zero);
            }
            Native.ShowWindow(_hwnd, Native.SW_HIDE);
            PetDebug("hide-pet");
        }

        // tray の「最前面に戻す」/ 左クリック。hidden なら再表示まで行う。
        private void BringToFront()
        {
            if (!_petVisible) { ShowPet(); return; } // ShowPet が EnsureTopmost まで行う
            EnsureTopmost();
            PetDebug("topmost re-assert");
        }

        // ---- 通知領域 (system tray) -----------------------------------------

        private void AddTrayIcon()
        {
            try
            {
                using (Bitmap bmp = PetRenderer.RenderTrayBitmap(32))
                    _trayIconHandle = bmp.GetHicon();
                var nid = new Native.NOTIFYICONDATA();
                nid.cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA));
                nid.hWnd = _hwnd;
                nid.uID = TrayIconId;
                nid.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP;
                nid.uCallbackMessage = WmTrayIcon;
                nid.hIcon = _trayIconHandle;
                nid.szTip = "ClaudePet"; // 静的 tooltip のみ。作業内容は載せない (privacy)
                _trayAdded = Native.Shell_NotifyIcon(Native.NIM_ADD, ref nid);
                PetDebug("tray: add " + (_trayAdded ? "ok" : "failed err=" + Marshal.GetLastWin32Error()));
            }
            catch (Exception ex)
            {
                // tray を作れなくても Pet 本体は通常動作させる (fail-soft)
                _trayAdded = false;
                PetDebug("tray: add exception " + ex.GetType().Name);
            }
        }

        // 二重呼び出しでも安全 (WM_DESTROY と menu 終了経路の両方から呼ばれ得る)
        private void RemoveTrayIcon()
        {
            if (_trayAdded)
            {
                var nid = new Native.NOTIFYICONDATA();
                nid.cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA));
                nid.hWnd = _hwnd;
                nid.uID = TrayIconId;
                Native.Shell_NotifyIcon(Native.NIM_DELETE, ref nid);
                _trayAdded = false;
            }
            if (_trayIconHandle != IntPtr.Zero)
            {
                Native.DestroyIcon(_trayIconHandle);
                _trayIconHandle = IntPtr.Zero;
            }
        }

        private void OnTrayCallback(IntPtr lParam)
        {
            int mouseMsg = unchecked((int)lParam.ToInt64()) & 0xFFFF;
            if (mouseMsg == Native.WM_LBUTTONUP)
            {
                // 左クリック = 「見失った Pet を取り戻す」。hide には使わない
                OnTrayCommand(CmdBringToFront);
            }
            else if (mouseMsg == Native.WM_RBUTTONUP || mouseMsg == Native.WM_CONTEXTMENU)
            {
                ShowTrayMenu();
            }
        }

        private void ShowTrayMenu()
        {
            IntPtr menu = Native.CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            try
            {
                Native.AppendMenu(menu, Native.MF_STRING | (_petVisible ? Native.MF_GRAYED : 0),
                    (uint)CmdShowPet, "ヒヨコを表示");
                Native.AppendMenu(menu, Native.MF_STRING | (_petVisible ? 0 : Native.MF_GRAYED),
                    (uint)CmdHidePet, "ヒヨコを隠す");
                Native.AppendMenu(menu, Native.MF_STRING, (uint)CmdBringToFront, "最前面に戻す");
                Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);
                Native.AppendMenu(menu, Native.MF_STRING, (uint)CmdExitPet, "ClaudePetを終了");

                Native.POINT pt;
                Native.GetCursorPos(out pt);
                // 標準の tray menu パターン。SetForegroundWindow が無いと
                // menu の外をクリックしても閉じない。Pet window は click-through +
                // NOACTIVATE なので、これで keyboard 入力を奪い続けることはない。
                Native.SetForegroundWindow(_hwnd);
                Native.TrackPopupMenuEx(menu, Native.TPM_RIGHTBUTTON, pt.x, pt.y, _hwnd, IntPtr.Zero);
                Native.PostMessage(_hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            }
            finally { Native.DestroyMenu(menu); }
        }

        private void OnTrayCommand(int id)
        {
            PetDebug("tray-cmd=" + id);
            switch (id)
            {
                case CmdShowPet: ShowPet(); break;
                case CmdHidePet: HidePet(); break;
                case CmdBringToFront: BringToFront(); break;
                case CmdExitPet:
                    // WM_DESTROY が tray / timer の掃除まで行う
                    Native.DestroyWindow(_hwnd);
                    break;
            }
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

        private static readonly Color TitleNeutral = Color.FromArgb(255, 100, 92, 84);   // 作業中

        // Working: 吹き出しなしの控えめなピル + 「作業中…」。
        // pct>=0 のときだけ依頼全体の推定進捗 (bar + 全体 推定N%) を追加表示する。
        // Waiting も Finalizing (root Stop 後の quiet window) も同じ描画。
        public static Bitmap RenderWorking(int w, int h, float scale, string project, int pct,
            string meta, int otherActive)
        {
            Bitmap bmp = NewCanvas(w, h);
            using (Graphics g = NewGraphics(bmp))
            {
                DrawChick(g, w, h, scale);
                DrawStatusPill(g, w, h, scale, "作業中…", TitleNeutral, project, pct, meta, otherActive);
            }
            return bmp;
        }

        // 状態ピル: <title> / [meta + +N] / [progress bar + 全体 推定 N%] / [project]。
        private static void DrawStatusPill(Graphics g, int w, int h, float scale, string title, Color titleColor,
            string project, int pct, string meta, int otherActive)
        {
            Color bg = Color.FromArgb(205, 250, 250, 248);
            Color border = Color.FromArgb(255, 210, 205, 196);
            Color textColor = Color.FromArgb(255, 100, 92, 84);
            Color subColor = Color.FromArgb(255, 130, 122, 112);
            Color trackColor = Color.FromArgb(255, 228, 224, 217);
            Color fillColor = Color.FromArgb(255, 232, 163, 66);

            bool hasProject = !string.IsNullOrEmpty(project);
            bool hasMeta = !string.IsNullOrEmpty(meta);
            float metaY = 30f;                       // ヘッダ直下
            float metaH = hasMeta ? 17f : 0f;        // 以降の行をその分だけ下へ
            float barY = 33f + metaH;
            float pctY = 45f + metaH;
            float cursor = ((pct >= 0) ? 66f : 32f) + metaH; // ヘッダ (+meta+bar+%) の下端
            float projY = cursor;
            if (hasProject) cursor += 19f;

            float bx = 24 * scale;
            float bw = w - 48 * scale;
            float bh = (cursor + 7f) * scale;
            // しっぽ無しなのでキャラのすぐ上へ。中身が増減しても頭との距離は一定。
            float by = ChickTopY(h, scale) - 10 * scale - bh;
            if (by < 10 * scale) by = 10 * scale; // 上端はみ出し防止 (念のため)

            using (GraphicsPath path = RoundedRect(bx, by, bw, bh, 12 * scale))
            {
                using (var bgBrush = new SolidBrush(bg)) g.FillPath(bgBrush, path);
                using (var borderPen = new Pen(border, 1.5f * scale)) g.DrawPath(borderPen, path);
            }

            using (var fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Center;

                using (var font = new Font("Yu Gothic UI", 15f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(titleColor))
                    g.DrawString(title, font, brush, w / 2f, by + 8 * scale, fmt);

                DrawMetaLine(g, bx, bw, scale, by + metaY * scale, meta, otherActive, subColor, textColor);

                if (pct >= 0)
                {
                    // progress bar (常時animationなし。イベント時の再描画のみ)
                    float barW = bw - 56 * scale;
                    float barH = 8 * scale;
                    float barX = w / 2f - barW / 2f;
                    float barYAbs = by + barY * scale;
                    using (GraphicsPath track = RoundedRect(barX, barYAbs, barW, barH, barH / 2f))
                    using (var trackBrush = new SolidBrush(trackColor))
                        g.FillPath(trackBrush, track);
                    float ratio = pct / 100f;
                    if (ratio > 1f) ratio = 1f;
                    float fillW = barW * ratio;
                    if (fillW >= barH) // 極端に短いと角丸が破綻するため最小幅まで描かない
                    {
                        using (GraphicsPath fill = RoundedRect(barX, barYAbs, fillW, barH, barH / 2f))
                        using (var fillBrush = new SolidBrush(fillColor))
                            g.FillPath(fillBrush, fill);
                    }

                    using (var font = new Font("Yu Gothic UI", 13.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(textColor))
                        g.DrawString("全体 推定 " + pct + "%", font, brush, w / 2f, by + pctY * scale, fmt);
                }

                if (hasProject)
                {
                    using (var font = new Font("Yu Gothic UI", 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(subColor))
                        g.DrawString(project, font, brush, w / 2f, by + projY * scale, fmt);
                }
            }
        }

        public static Bitmap RenderCelebrate(int w, int h, float scale, string project,
            string meta, int otherActive)
        {
            Bitmap bmp = NewCanvas(w, h);
            using (Graphics g = NewGraphics(bmp))
            {
                DrawChick(g, w, h, scale);
                DrawPill(g, w, h, scale, "終わったよ！", project,
                    Color.FromArgb(238, 255, 255, 255),   // 背景: 白
                    Color.FromArgb(255, 205, 198, 188),   // 枠: グレー
                    Color.FromArgb(255, 70, 62, 54),      // 文字: 濃いグレー
                    true, 19f, meta, otherActive);
            }
            return bmp;
        }

        // provider + model の 1 行。model 不明なら provider だけ。
        public static string MetaLine(bool isCodex, string modelId)
        {
            string provider = isCodex ? "Codex" : "Claude";
            string model = HumanizeModel(modelId == null ? "" : modelId.Trim());
            return (model.Length == 0) ? provider : provider + " · " + model;
        }

        // 存在しないモデル名を作らないことを最優先する。
        // 確実に分かる形式だけ短くし、未知の形式はそのまま出す。
        public static string HumanizeModel(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string low = id.ToLowerInvariant();
            if (low.StartsWith("claude-"))
            {
                string[] parts = low.Substring(7).Split('-');
                string fam = (parts.Length > 0) ? parts[0] : "";
                if (fam == "opus" || fam == "sonnet" || fam == "haiku")
                {
                    string name = char.ToUpperInvariant(fam[0]) + fam.Substring(1);
                    var nums = new List<string>();
                    for (int i = 1; i < parts.Length; i++)
                    {
                        // 20250805 のような日付 suffix はバージョンに含めない
                        if (!IsDigits(parts[i]) || parts[i].Length >= 6) break;
                        nums.Add(parts[i]);
                    }
                    if (nums.Count == 0) return name;
                    return name + " " + string.Join(".", nums.ToArray());
                }
                return id; // 未知の claude-* は変換しない
            }
            if (low.StartsWith("gpt-")) return "GPT" + id.Substring(3);
            return id;
        }

        private static bool IsDigits(string t)
        {
            if (t.Length == 0) return false;
            for (int i = 0; i < t.Length; i++) if (t[i] < '0' || t[i] > '9') return false;
            return true;
        }

        // maxW に収まるまで末尾を削って … を付ける
        private static string FitText(Graphics g, string text, Font font, float maxW)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (g.MeasureString(text, font).Width <= maxW) return text;
            for (int len = text.Length - 1; len > 0; len--)
            {
                string t = text.Substring(0, len) + "…";
                if (g.MeasureString(t, font).Width <= maxW) return t;
            }
            return "…";
        }

        // 左: provider · model (長ければ ellipsis) / 右: +N。
        // +N の幅を先に確保してから左を収めるので重ならない。
        private static void DrawMetaLine(Graphics g, float bx, float bw, float scale, float y,
            string meta, int otherActive, Color metaColor, Color countColor)
        {
            if (string.IsNullOrEmpty(meta)) return;
            float pad = 14 * scale;
            float left = bx + pad;
            float right = bx + bw - pad;
            using (var font = new Font("Yu Gothic UI", 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var countFont = new Font("Yu Gothic UI", 11.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Near;
                float reserved = 0f;
                string count = null;
                if (otherActive > 0)
                {
                    count = "+" + otherActive;
                    reserved = g.MeasureString(count, countFont).Width + 6 * scale;
                }
                string shown = FitText(g, meta, font, (right - left) - reserved);
                using (var brush = new SolidBrush(metaColor))
                    g.DrawString(shown, font, brush, left, y, fmt);
                if (count != null)
                {
                    var rfmt = new StringFormat();
                    rfmt.Alignment = StringAlignment.Far;
                    using (rfmt)
                    using (var brush = new SolidBrush(countColor))
                        g.DrawString(count, countFont, brush, right, y, rfmt);
                }
            }
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

        // 通知領域アイコン用の小さなひよこ。既存 DrawChick と同じ配色を
        // そのまま使い、外部画像ファイルは増やさない。呼び出し側が
        // Bitmap.GetHicon() で HICON 化し、DestroyIcon で解放する。
        public static Bitmap RenderTrayBitmap(int size)
        {
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = NewGraphics(bmp))
            {
                float cx = size / 2f;
                float cy = size / 2f;
                float r = size * 0.42f;

                using (var body = new SolidBrush(BodyColor))
                    g.FillEllipse(body, cx - r, cy - r, r * 2, r * 2);
                using (var edge = new Pen(BodyEdge, Math.Max(1f, size * 0.05f)))
                    g.DrawEllipse(edge, cx - r, cy - r, r * 2, r * 2);

                using (var eye = new SolidBrush(Color.FromArgb(40, 34, 28)))
                {
                    float er = size * 0.075f;
                    g.FillEllipse(eye, cx - r * 0.42f - er, cy - r * 0.28f - er, er * 2, er * 2);
                    g.FillEllipse(eye, cx + r * 0.42f - er, cy - r * 0.28f - er, er * 2, er * 2);
                }

                using (var beak = new SolidBrush(BeakColor))
                {
                    PointF[] tri = new PointF[]
                    {
                        new PointF(cx - size * 0.09f, cy + r * 0.02f),
                        new PointF(cx + size * 0.09f, cy + r * 0.02f),
                        new PointF(cx, cy + r * 0.4f)
                    };
                    g.FillPolygon(beak, tri);
                }
            }
            return bmp;
        }

        // キャラ本体だけの倍率。文字サイズ (ピル・ラベル) には掛けない。
        private const float ChickScale = 0.5f;

        // キャラ頭頂の y (window 座標)。ピル類はこれを基準に下端を合わせるので、
        // 中身の量で高さが変わってもキャラとの距離は一定に保たれる。
        private static float ChickTopY(int h, float scale)
        {
            return h - 56 * scale - 36 * scale * ChickScale;
        }

        private static void DrawChick(Graphics g, int w, int h, float scale)
        {
            // s = キャラ内部の寸法用 (線幅・パーツも比例縮小)。
            // 配置は window 単位 (scale) のままなので、足元は従来と同じ画面位置に残る。
            float s = scale * ChickScale;
            float cx = w / 2f;
            float cy = h - 56 * scale;
            float r = 36 * s;

            // 足
            using (var pen = new Pen(BeakColor, 2.5f * s))
            {
                g.DrawLine(pen, cx - 12 * s, cy + r - 4 * s, cx - 14 * s, cy + r + 8 * s);
                g.DrawLine(pen, cx + 12 * s, cy + r - 4 * s, cx + 14 * s, cy + r + 8 * s);
            }

            // 体
            using (var body = new SolidBrush(BodyColor))
                g.FillEllipse(body, cx - r, cy - r, r * 2, r * 2);
            using (var edge = new Pen(BodyEdge, 2f * s))
                g.DrawEllipse(edge, cx - r, cy - r, r * 2, r * 2);

            // 羽
            using (var wing = new Pen(BodyEdge, 2f * s))
            {
                g.DrawArc(wing, cx - r + 4 * s, cy - 6 * s, 18 * s, 20 * s, 60, 180);
                g.DrawArc(wing, cx + r - 22 * s, cy - 6 * s, 18 * s, 20 * s, -60, 180);
            }

            // 目
            using (var eye = new SolidBrush(Color.FromArgb(40, 34, 28)))
            {
                float er = 4f * s;
                g.FillEllipse(eye, cx - 14 * s - er, cy - 10 * s - er, er * 2, er * 2);
                g.FillEllipse(eye, cx + 14 * s - er, cy - 10 * s - er, er * 2, er * 2);
            }

            // ほっぺ
            using (var cheek = new SolidBrush(CheekColor))
            {
                float chr = 5.5f * s;
                g.FillEllipse(cheek, cx - 24 * s - chr, cy + 2 * s - chr, chr * 2, chr * 2);
                g.FillEllipse(cheek, cx + 24 * s - chr, cy + 2 * s - chr, chr * 2, chr * 2);
            }

            // くちばし
            using (var beak = new SolidBrush(BeakColor))
            {
                PointF[] tri = new PointF[]
                {
                    new PointF(cx - 6 * s, cy - 2 * s),
                    new PointF(cx + 6 * s, cy - 2 * s),
                    new PointF(cx, cy + 8 * s)
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
        private static void DrawPill(Graphics g, int w, int h, float scale, string mainText, string project,
            Color bg, Color border, Color textColor, bool tail, float mainSize,
            string meta, int otherActive)
        {
            float bx = 24 * scale;
            float bw = w - 48 * scale;
            bool hasProject = !string.IsNullOrEmpty(project);
            bool hasMeta = !string.IsNullOrEmpty(meta);
            float metaH = hasMeta ? 17f : 0f;
            float bh = ((hasProject ? 76 : 56) + metaH) * scale;
            float rad = 12 * scale;
            // しっぽの先がキャラの頭上に来るよう下端基準で配置する。
            float tailH = tail ? 10 * scale : 0f;
            float by = ChickTopY(h, scale) - 19 * scale - tailH - bh;
            if (by < 10 * scale) by = 10 * scale; // 上端はみ出し防止 (念のため)

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
                DrawMetaLine(g, bx, bw, scale, by + 38 * scale, meta, otherActive,
                    Color.FromArgb(255, 130, 122, 112), textColor);

                if (hasProject)
                {
                    using (var font = new Font("Yu Gothic UI", 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(Color.FromArgb(255, 130, 122, 112)))
                    {
                        g.DrawString(project, font, brush, w / 2f, by + (42 + metaH) * scale, fmt);
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
