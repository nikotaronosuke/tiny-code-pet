// ClaudePetNotify.exe - Claude Code Hook Adapter (tiny hook helper)
// Claude Code の Stop hook から起動され、stdin の JSON から cwd を取り出し、
// 常駐ペット (ClaudePet.exe) へ WM_COPYDATA で正規化イベントを送る。
// ペット未起動時は同じフォルダの ClaudePet.exe を起動してから送る。
// 常に exit code 0 で即終了し、Claude Code を絶対にブロックしない。
//
// 使い方:
//   (hookから)  stdin に JSON  -> task_complete イベント送信
//   --test [名前]              -> 手動テスト送信
//   --quit                     -> 常駐ペットを終了させる
//
// プライバシー: cwd 以外の情報 (prompt本文・応答本文など) は一切読み取らない。

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
        private const int EventTaskComplete = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, ref COPYDATASTRUCT lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

                string project;
                if (args.Length > 0 && args[0] == "--test")
                {
                    project = args.Length > 1 ? args[1] : "test-project";
                }
                else
                {
                    string cwd = ReadCwdFromStdin();
                    project = ToProjectName(cwd);
                }

                IntPtr hwnd = EnsurePetRunning();
                if (hwnd == IntPtr.Zero) return 0; // ペットを起動できなくても Claude は妨げない

                SendEvent(hwnd, EventTaskComplete, project);
            }
            catch { }
            return 0;
        }

        // stdin JSON から "cwd" だけを抽出する。完全な JSON parser は使わない (軽量化)。
        private static string ReadCwdFromStdin()
        {
            string json = "";
            try
            {
                using (Stream s = Console.OpenStandardInput())
                using (var reader = new StreamReader(s, Encoding.UTF8))
                {
                    json = reader.ReadToEnd();
                }
            }
            catch { }
            if (string.IsNullOrEmpty(json)) return "";

            Match m = Regex.Match(json, "\"cwd\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
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

        private static IntPtr EnsurePetRunning()
        {
            IntPtr hwnd = FindWindow(WndClassName, null);
            if (hwnd != IntPtr.Zero) return hwnd;

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
                hwnd = FindWindow(WndClassName, null);
                if (hwnd != IntPtr.Zero) return hwnd;
            }
            return IntPtr.Zero;
        }

        private static void SendEvent(IntPtr hwnd, int eventType, string payload)
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
                // SMTO_ABORTIFHUNG | SMTO_BLOCK 相当: 1秒で諦める。Hookを長引かせない。
                SendMessageTimeout(hwnd, WM_COPYDATA, IntPtr.Zero, ref cds, 0x0002, 1000, out result);
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }
    }
}
