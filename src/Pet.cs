// ClaudePet.exe - 常駐デスクトップペット本体
// 純Win32 (P/Invoke) + layered window。アイドル時は GetMessage でブロックし CPU 0%。
// 描画は System.Drawing で ARGB ビットマップを合成し UpdateLayeredWindow で反映。
// タイマーはアニメーション中のみ SetTimer し、終了後は必ず KillTimer する。
//
// 状態遷移:  Idle --(WM_COPYDATA: task_complete)--> Celebrating(bounce) --> Message表示 --(約5秒)--> Idle
//
// C# 5 (.NET Framework 4.8 同梱 csc.exe) でビルド可能な構文のみ使用。

using System;
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

        public const uint MB_OK_SOUND = 0x00000000;

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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
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
        public const int TaskComplete = 1;
    }

    internal sealed class PetApp
    {
        private const string WndClassName = "ClaudeDesktopPetWnd";

        // アニメーション用タイマーID
        private static readonly IntPtr TimerBounce = new IntPtr(1);
        private static readonly IntPtr TimerRevert = new IntPtr(2);

        private const int BounceIntervalMs = 30;
        private const int BounceFrames = 44;      // 約1.3秒で3回バウンド
        private const int RevertDelayMs = 3700;   // バウンド後、メッセージ表示継続時間

        private IntPtr _hwnd;
        private Native.WndProcDelegate _wndProc; // GC防止のためフィールドで保持
        private float _scale = 1f;

        // レイアウト (96dpi基準、_scale倍して使用)
        private int _winW;
        private int _winH;
        private int _baseX;
        private int _baseY;

        private int _bounceFrame;

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
            _winH = S(220);

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
            Native.RegisterClassEx(ref wc);

            _hwnd = Native.CreateWindowEx(
                Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
                Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
                WndClassName, "Claude Pet", Native.WS_POPUP,
                _baseX, _baseY, _winW, _winH,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            using (Bitmap idle = PetRenderer.RenderIdle(_winW, _winH, _scale))
            {
                ApplyBitmap(idle, _baseX, _baseY);
            }
            Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);

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

        private void HandleCopyData(IntPtr lParam)
        {
            try
            {
                var cds = (Native.COPYDATASTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.COPYDATASTRUCT));
                if (cds.dwData.ToInt64() != PetEvent.TaskComplete) return;

                string project = "";
                if (cds.cbData > 0 && cds.lpData != IntPtr.Zero)
                {
                    byte[] buf = new byte[cds.cbData];
                    Marshal.Copy(cds.lpData, buf, 0, cds.cbData);
                    project = System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0');
                }
                StartCelebrate(project);
            }
            catch { }
        }

        private void StartCelebrate(string project)
        {
            // 連続通知時は最初からやり直す
            Native.KillTimer(_hwnd, TimerBounce);
            Native.KillTimer(_hwnd, TimerRevert);

            using (Bitmap bmp = PetRenderer.RenderCelebrate(_winW, _winH, _scale, project))
            {
                ApplyBitmap(bmp, _baseX, _baseY);
            }

            Native.MessageBeep(Native.MB_OK_SOUND);

            _bounceFrame = 0;
            Native.SetTimer(_hwnd, TimerBounce, BounceIntervalMs, IntPtr.Zero);
        }

        private void OnBounceTick()
        {
            _bounceFrame++;
            if (_bounceFrame >= BounceFrames)
            {
                Native.KillTimer(_hwnd, TimerBounce);
                MoveTo(_baseX, _baseY);
                // メッセージをしばらく表示してから静止状態へ戻す
                Native.SetTimer(_hwnd, TimerRevert, RevertDelayMs, IntPtr.Zero);
                return;
            }
            double t = (double)_bounceFrame / BounceFrames;
            int offset = (int)(S(22) * Math.Abs(Math.Sin(t * Math.PI * 3.0)));
            MoveTo(_baseX, _baseY - offset);
        }

        private void OnRevert()
        {
            Native.KillTimer(_hwnd, TimerRevert);
            using (Bitmap idle = PetRenderer.RenderIdle(_winW, _winH, _scale))
            {
                ApplyBitmap(idle, _baseX, _baseY);
            }
            MoveTo(_baseX, _baseY);
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
                DrawLabel(g, w, h, scale);
            }
            return bmp;
        }

        public static Bitmap RenderCelebrate(int w, int h, float scale, string project)
        {
            Bitmap bmp = NewCanvas(w, h);
            using (Graphics g = NewGraphics(bmp))
            {
                DrawChick(g, w, h, scale);
                DrawBubble(g, w, h, scale, project);
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

        private static void DrawLabel(Graphics g, int w, int h, float scale)
        {
            using (var font = new Font("Yu Gothic UI", 10.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(Color.FromArgb(150, 90, 84, 76)))
            using (var fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Center;
                g.DrawString("Claude", font, brush, w / 2f, h - 24 * scale, fmt);
            }
        }

        private static void DrawBubble(Graphics g, int w, int h, float scale, string project)
        {
            float bx = 24 * scale;
            float by = 14 * scale;
            float bw = w - 48 * scale;
            float bh = string.IsNullOrEmpty(project) ? 56 * scale : 76 * scale;
            float rad = 12 * scale;

            using (GraphicsPath path = RoundedRect(bx, by, bw, bh, rad))
            {
                // 吹き出しのしっぽ
                path.AddPolygon(new PointF[]
                {
                    new PointF(w / 2f - 8 * scale, by + bh - 1),
                    new PointF(w / 2f + 8 * scale, by + bh - 1),
                    new PointF(w / 2f, by + bh + 10 * scale)
                });
                using (var bg = new SolidBrush(Color.FromArgb(238, 255, 255, 255)))
                    g.FillPath(bg, path);
                using (var border = new Pen(Color.FromArgb(255, 205, 198, 188), 1.5f * scale))
                    g.DrawPath(border, path);
            }

            using (var fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Center;
                using (var font = new Font("Yu Gothic UI", 19f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.FromArgb(255, 70, 62, 54)))
                {
                    g.DrawString("終わったよ！", font, brush, w / 2f, by + 12 * scale, fmt);
                }
                if (!string.IsNullOrEmpty(project))
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
