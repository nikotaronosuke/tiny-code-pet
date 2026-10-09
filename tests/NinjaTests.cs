using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using ClaudePet;

internal static class NinjaTests
{
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    private static extern int GetMenuString(IntPtr menu, uint id, System.Text.StringBuilder text, int count, uint flags);
    [DllImport("user32.dll")] private static extern uint GetMenuState(IntPtr menu, uint id, uint flags);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    private static string MenuText(IntPtr menu, uint id)
    {
        var text = new System.Text.StringBuilder(512);
        GetMenuString(menu,id,text,text.Capacity,0);
        return text.ToString();
    }
    private static void RefreshMenu(PetApp app)
    {
        IntPtr menu=(IntPtr)Call(app,"CreateTrayMenu");
        Check(menu!=IntPtr.Zero,"native work menu created");
        Native.DestroyMenu(menu);
    }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    private static int assertions;
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
    private static void Check(bool ok, string name) { assertions++; if (!ok) throw new Exception(name); }
    private static object Call(object app, string name, params object[] args) { return app.GetType().GetMethod(name,Flags).Invoke(app,args); }
    private static void Set(object app, string name, object value) { app.GetType().GetField(name,Flags).SetValue(app,value); }
    private static T Get<T>(object app, string name) { return (T)app.GetType().GetField(name,Flags).GetValue(app); }
    private static void Claude(PetApp a, int ev, string extra) { Call(a,"OnEvent",ev,"fixture-session","fixture-project",extra); }
    private static void Codex(PetApp a, int ev, string extra, string turn) { Call(a,"OnCodexEvent",ev,"fixture-session","fixture-project",extra,turn); }
    private static void ProgressEvent(string json, string tool, string agent, int expected, string extra)
    {
        int actual; string result;
        ClaudePetNotify.Program.NormalizeProgress(json, tool, agent, out actual, out result);
        Check(actual == expected && result == extra, "Claude adapter " + tool + " -> " + expected + " bounded metadata");
    }

    private static void TestClaudeProgressAdapter()
    {
        ProgressEvent("{\"tool_input\":{\"todos\":[{\"status\":\"completed\"},{\"status\":\"pending\"}]}}", "TodoWrite", "", 8, "1/0/2|");
        ProgressEvent("{\"tool_input\":{\"todos\":[]}}", "TodoWrite", "", 8, "0/0/0|");
        ProgressEvent("{\"tool_input\":{\"todos\":[{\"status\":\"unknown\"}]}}", "TodoWrite", "", 4, PetEvent.StructuredObserved);
        ProgressEvent("{\"tool_response\":{\"todos\":[{\"status\":\"completed\"}]}}", "TodoWrite", "", 4, PetEvent.StructuredObserved);
        ProgressEvent("{\"tool_input\":{\"todos\":[", "TodoWrite", "", 4, PetEvent.StructuredObserved);
        ProgressEvent("{\"tool_input\":{\"todos\":[{\"status\":\"completed\"}]}}", "TodoWrite", "child", 4, "");
        foreach (string tool in new string[] { "TaskCreate", "TaskGet", "TaskList" })
        {
            ProgressEvent("{\"tool_response\":\"PRIVATE_FIXTURE\"}", tool, "", 4, PetEvent.StructuredNoCounts);
            ProgressEvent("{}", tool, "child", 4, "");
        }
        ProgressEvent("{}", "Bash", "", 4, "");
        string input = "{\"tool_input\":{\"taskId\":\"fixture-task\",\"status\":\"{STATUS}\"},\"tool_response\":\"PRIVATE_FIXTURE\"}";
        ProgressEvent(input.Replace("{STATUS}","completed"), "TaskUpdate", "", 7, "fixture-task");
        ProgressEvent(input.Replace("{STATUS}","in_progress"), "TaskUpdate", "", 10, "fixture-task");
        ProgressEvent(input.Replace("{STATUS}","deleted"), "TaskUpdate", "", 9, "fixture-task");
        ProgressEvent(input.Replace("{STATUS}","cancelled"), "TaskUpdate", "", 9, "fixture-task");
        ProgressEvent(input.Replace("{STATUS}","pending"), "TaskUpdate", "", 4, PetEvent.StructuredNoCounts);
        ProgressEvent(input.Replace("{STATUS}","unknown"), "TaskUpdate", "", 4, PetEvent.StructuredObserved);
        ProgressEvent(input.Replace("{STATUS}","completed"), "TaskUpdate", "child", 4, "");
        ProgressEvent("{\"tool_input\":{\"task_id\":\"fixture-task\",\"status\":\"completed\"}}", "TaskUpdate", "", 7, "fixture-task");
        ProgressEvent("{\"tool_input\":{\"taskId\":\"fixture-task\",\"subject\":\"PRIVATE_FIXTURE\"}}", "TaskUpdate", "", 4, PetEvent.StructuredNoCounts);
        ProgressEvent("{\"tool_input\":{\"status\":\"completed\"}}", "TaskUpdate", "", 4, PetEvent.StructuredObserved);
        ProgressEvent("{\"tool_input\":{\"taskId\":\"bad\\nline\",\"status\":\"completed\"}}", "TaskUpdate", "", 4, PetEvent.StructuredObserved);
        ProgressEvent("{\"tool_input\":{\"taskId\":\"a\",\"status\":\"pending\",\"status\":\"completed\"}}", "TaskUpdate", "", 4, PetEvent.StructuredObserved);
        ProgressEvent("{\"tool_response\":{\"tool_input\":{\"taskId\":\"fake\",\"status\":\"completed\"}}}", "TaskUpdate", "", 4, PetEvent.StructuredObserved);
        ProgressEvent("{\"tool_input\":{\"metadata\":{\"taskId\":\"fake\",\"status\":\"completed\"}}}", "TaskUpdate", "", 4, PetEvent.StructuredObserved);
        ProgressEvent("{\"tool_input\":{\"taskId\":\"real\",\"metadata\":{\"status\":\"completed\"}}}", "TaskUpdate", "", 4, PetEvent.StructuredNoCounts);
        ProgressEvent("{\"tool_response\":{},\"tool_input\":{\"status\":\"completed\",\"taskId\":\"fixture-task\"}}", "TaskUpdate", "", 7, "fixture-task");
        Check(ClaudePetNotify.Program.ReadMetadataString("{\"tool_response\":{\"tool_name\":\"TodoWrite\"},\"tool_name\":\"Bash\"}", "tool_name")=="Bash", "root tool routing ignores nested metadata");
        Check(ClaudePetNotify.Program.ReadMetadataString("{\"tool_response\":{\"agent_id\":\"fake\"}}", "agent_id")=="", "nested agent cannot suppress root progress");
        Check(ClaudePetNotify.Program.ReadMetadataString("{\"task_id\":\"fixture\",\"task_id\":\"other\"}", "task_id")=="", "duplicate metadata cannot contribute progress");
        int done, active, total;
        Check(!WorkDetails.TryCounts("2147483647/2147483647/2", out done, out active, out total), "overflow counts rejected");
        Check(!WorkDetails.TryCounts("-1/0/2", out done, out active, out total), "negative counts rejected");
        Check(!WorkDetails.TryCounts("1/2", out done, out active, out total), "incomplete counts rejected");
    }

    private static void TestClaudeProgressDiagnostics(PetApp app, Dictionary<string,Session> sessions)
    {
        Check(((string)Call(app,"BuildClaudeDiagnostics")).Contains("Claude Hook：未受信"), "diagnose no Claude hooks");
        Codex(app,20,"","diagnostic-turn"); Codex(app,22,"1/1/4","diagnostic-turn");
        Check(((string)Call(app,"BuildClaudeDiagnostics")).Contains("Claude Hook：未受信"), "Codex does not imply Claude reception");
        Codex(app,25,"","diagnostic-turn");
        Claude(app,11,"");
        Check(sessions["fixture-session"].State==Session.MetadataOnly, "diagnostics do not activate metadata session");
        Claude(app,2,"");
        Session s=sessions["fixture-session"];
        Check(s.ClaudeProgressDiagnostic().Contains("PostToolUse / Task/Todo 未受信"), "request has no activity yet");
        Claude(app,4,"");
        Check(s.ClaudeProgressDiagnostic().Contains("Task/Todo 未受信（通常"), "normal activity distinguished from tracker");
        Claude(app,4,PetEvent.StructuredNoCounts);
        Check(s.ClaudeProgressDiagnostic().Contains("件数情報なし") && s.ProgressPercent()==-1, "read-only tracker does not invent counts");
        Claude(app,4,PetEvent.StructuredObserved);
        Check(s.ClaudeProgressDiagnostic().Contains("解析不可") && s.ProgressPercent()==-1, "parse failure distinguished from absent tracker");
        Claude(app,8,"0/0/0|");
        Check(s.ClaudeProgressDiagnostic().Contains("工程数不足：0") && s.ProgressPercent()==-1, "empty tracker is not parse failure");
        Claude(app,8,"0/1/1|");
        Check(s.ClaudeProgressDiagnostic().Contains("工程数不足：1") && s.ProgressPercent()==-1, "one task never makes percentage");
        Claude(app,8,"1/1/4");
        Check(s.ClaudeProgressDiagnostic().Contains("37%") && s.ProgressPercent()==37, "legacy snapshot restores diagnostics");
        Claude(app,4,PetEvent.StructuredObserved);
        Check(s.ClaudeProgressDiagnostic().Contains("解析不可") && s.ProgressPercent()==37, "parse failure preserves previous snapshot");
        Claude(app,8,"1/0/2|");
        Check(s.ClaudeProgressDiagnostic().Contains("50%"), "valid snapshot clears parse failure");
        Claude(app,8,"bad");
        Check(s.ClaudeProgressDiagnostic().Contains("解析不可") && s.ProgressPercent()==50, "malformed wire snapshot diagnosed without overwriting progress");
        Claude(app,2,"");
        Check(!s.SawStructuredTasks && !s.SawClaudeActivity && !s.ClaudeProgressParseFailed && s.ProgressPercent()==-1, "new request resets diagnostics");
        Claude(app,6,"fixture-task-a"); Claude(app,6,"fixture-task-a");
        Check(s.ClaudeProgressDiagnostic().Contains("工程数不足：1"), "duplicate lifecycle events do not inflate task count");
        Claude(app,6,"fixture-task-b"); Claude(app,10,"fixture-task-a");
        Check(s.ProgressPercent()==25 && s.ClaudeProgressDiagnostic().Contains("25%"), "legacy task events still compute progress");
        Claude(app,7,"fixture-task-a"); Claude(app,7,"fixture-task-a");
        Check(s.ProgressPercent()==50, "duplicate completed events remain idempotent");
        Claude(app,9,"fixture-task-b");
        Check(s.ClaudeProgressDiagnostic().Contains("工程数不足：1") && s.ProgressPercent()==-1, "removed task can reduce total below threshold");
        Claude(app,1,"");
        DateTime deadline=s.QuietDueUtc; long sequence=s.LastSeq;
        string report=(string)Call(app,"BuildClaudeDiagnostics");
        Check(s.State==Session.Finalizing && s.QuietDueUtc==deadline && s.LastSeq==sequence, "opening diagnostics cannot alter completion candidate");
        Check(!report.Contains("fixture-session") && !report.Contains("fixture-task") && !report.Contains("fixture-project"), "diagnostic text excludes identifiers and project");
        s.QuietDueUtc=DateTime.UtcNow.AddSeconds(-1); Call(app,"FinalizeDue");
        Check(s.State==Session.Celebrating, "insufficient progress still completes after quiet window");
        Call(app,"OnRevert");
        Check(sessions.Count==0 && ((string)Call(app,"BuildClaudeDiagnostics")).Contains("保持中の Claude セッションはありません"), "finished request does not present stale diagnosis");
    }

    private static void TestWorkSelection(IntPtr hwnd, string output)
    {
        var app=new PetApp(); Set(app,"_hwnd",hwnd); Set(app,"_petVisible",false);
        var sessions=Get<Dictionary<string,Session>>(app,"_sessions");
        try
        {
            IntPtr menu=(IntPtr)Call(app,"CreateTrayMenu");
            try
            {
                Check((GetMenuState(menu,1006,0)&Native.MF_CHECKED)!=0 &&
                    GetMenuState(menu,2000,0)==uint.MaxValue,"empty menu is automatic with no work commands");
            }
            finally { Native.DestroyMenu(menu); }
            Claude(app,2,""); Claude(app,8,"0/1/2"); Claude(app,12,"fixture-agent");
            Session c=sessions["fixture-session"];
            c.Project="R&D\t日本語\r\n"+new string('あ',40); c.CurrentWork="PRIVATE_FIXTURE";
            Codex(app,20,"","menu-turn"); Codex(app,22,"0/1/1","menu-turn");
            Session d=sessions["codex:fixture-session"];
            Call(app,"OnEvent",11,"metadata-session","metadata-project","");
            menu=(IntPtr)Call(app,"CreateTrayMenu");
            try
            {
                string text=MenuText(menu,2001);
                Check(text.Contains("Claude") && text.Contains("全体 推定 25%") && text.Contains("作業中"),"menu shows observed provider, progress and state");
                Check(text.Contains("R&&D") && text.Contains("…") && !text.Contains("\t") && !text.Contains("\r") && !text.Contains("\n"),"long project is bounded and menu metacharacters are escaped");
                Check(!text.Contains("PRIVATE_FIXTURE") && !text.Contains("fixture-session") && !text.Contains("fixture-agent"),"menu contains no task text or internal identities");
                Check(MenuText(menu,2000).Contains("Codex") && !MenuText(menu,2000).Contains("%"),"one-step work has no invented percentage");
                Check(GetMenuState(menu,2002,0)==uint.MaxValue,"metadata-only sessions excluded");
            }
            finally { Native.DestroyMenu(menu); }
            Check((string)Call(app,"ComputeDisplaySession")=="codex:fixture-session","automatic display follows latest active work");
            long seq=c.LastSeq; long gen=c.RequestGen;
            Call(app,"OnTrayCommand",2001);
            Codex(app,21,"","menu-turn");
            Check((string)Call(app,"ComputeDisplaySession")=="fixture-session","pinned Claude survives newer Codex events");
            Check(c.LastSeq==seq && c.RequestGen==gen && c.ProgressPercent()==25 && c.Subagents.Count==1 &&
                c.QuietDueUtc==DateTime.MinValue,"pin only changes display, not progress, clones or completion");
            Check(Get<Bitmap>(app,"_hud")==null && !Get<bool>(app,"_petVisible"),"pin keeps hidden pet hidden");
            menu=(IntPtr)Call(app,"CreateTrayMenu");
            try
            {
                Check((GetMenuState(menu,2001,0)&Native.MF_CHECKED)!=0 &&
                    (GetMenuState(menu,1006,0)&Native.MF_CHECKED)==0,"native menu marks pinned work");
            }
            finally { Native.DestroyMenu(menu); }
            c.Project="tiny-code-pet"; c.CurrentWork="表示する作業を固定して確認する";
            foreach(float scale in new float[] { 1f, 1.25f, 2f })
            {
                int w=(int)(280*scale), h=(int)(340*scale);
                Set(app,"_winW",w); Set(app,"_winH",h); Set(app,"_scale",scale); Set(app,"_petVisible",true);
                Call(app,"RenderCurrent",true,false);
                Bitmap hud=Get<Bitmap>(app,"_hud");
                Check(hud.Width==w && hud.Height==h && Get<int>(app,"_winW")==w && Get<int>(app,"_winH")==h,
                    "pin preserves existing window dimensions at DPI "+scale);
                using(Bitmap expected=PetRenderer.RenderWorking(w,h,scale,c.Project,c.ProgressPercent(),
                    PetRenderer.MetaLine(false,c.ModelId),1,"工程："+c.CurrentWork,c.ElapsedText()))
                {
                    // Compare the card's background silhouette; elapsed text can cross a second boundary.
                    bool same=true;
                    for(int y=0;y<h && same;y++) for(int x=0;x<w;x++)
                        if((hud.GetPixel(x,y).A>0)!=(expected.GetPixel(x,y).A>0)) { same=false; break; }
                    Check(same,"pin adds no card area at DPI "+scale);
                }
                using(Bitmap frame=(Bitmap)Call(app,"ComposeSpriteFrame"))
                    frame.Save(Path.Combine(output,"pinned-"+(int)(scale*100)+"pct.png"));
                Set(app,"_petVisible",false);
            }
            Call(app,"OnTrayCommand",1006);
            Check((string)Call(app,"ComputeDisplaySession")=="codex:fixture-session","automatic command restores latest active work");
            RefreshMenu(app); Call(app,"OnTrayCommand",2001);
            Claude(app,3,"");
            Check((string)Call(app,"ComputeDisplaySession")=="fixture-session" && c.State==Session.Waiting,"pin survives input wait");
            menu=(IntPtr)Call(app,"CreateTrayMenu");
            try { Check(MenuText(menu,2000).Contains("入力・承認待ち"),"menu shows input wait"); }
            finally { Native.DestroyMenu(menu); }
            Claude(app,1,""); DateTime due=c.QuietDueUtc;
            Check((string)Call(app,"ComputeDisplaySession")=="fixture-session" && (due-DateTime.UtcNow).TotalSeconds>19,"pin preserves Stop plus 20-second quiet period");
            menu=(IntPtr)Call(app,"CreateTrayMenu");
            try { Check(MenuText(menu,2000).Contains("終了通知後の待機中"),"menu shows finalizing without claiming completion"); }
            finally { Native.DestroyMenu(menu); }
            Check(c.QuietDueUtc==due,"menu does not extend quiet deadline");
            c.QuietDueUtc=DateTime.UtcNow.AddSeconds(-1); Call(app,"FinalizeDue");
            Check(!sessions.ContainsKey("fixture-session") && (string)Call(app,"ComputeDisplaySession")=="codex:fixture-session" &&
                Get<object>(app,"_pinnedWork")==null,"pinned completion releases automatically while other active suppresses celebration");
            Call(app,"OnTrayCommand",2000);
            Check(Get<object>(app,"_pinnedWork")==null,"removed menu entry cannot pin another work");
            RefreshMenu(app); Call(app,"OnTrayCommand",2000);
            Codex(app,20,"","next-turn");
            Check((string)Call(app,"PinnedSession")==null,"new Codex turn releases pin");
            Call(app,"OnTrayCommand",2000);
            Check(Get<object>(app,"_pinnedWork")==null,"old open menu cannot pin new Codex turn");
            Claude(app,2,""); RefreshMenu(app); Call(app,"OnTrayCommand",2000);
            Claude(app,2,"");
            Check((string)Call(app,"PinnedSession")==null,"new Claude request releases pin");
            Call(app,"OnTrayCommand",2000);
            Check(Get<object>(app,"_pinnedWork")==null,"old open menu cannot pin new Claude request");
            RefreshMenu(app); Call(app,"OnTrayCommand",2000);
            Claude(app,14,""); Claude(app,2,""); Call(app,"OnTrayCommand",2000);
            Check((string)Call(app,"PinnedSession")==null,"recreated session with same key and generation rejects stale selection");
            RefreshMenu(app); Call(app,"OnTrayCommand",2000);
            sessions["fixture-session"].LastAtUtc=DateTime.UtcNow.AddHours(-5); Call(app,"Prune");
            Check((string)Call(app,"PinnedSession")==null,"pruned work releases pin");
            RefreshMenu(app); Call(app,"OnTrayCommand",2000);
            Codex(app,26,"fixture-child","next-turn");
            menu=(IntPtr)Call(app,"CreateTrayMenu");
            try { Check(!MenuText(menu,2000).Contains("%"),"pinned Codex subagent still suppresses progress"); }
            finally { Native.DestroyMenu(menu); }
            Codex(app,28,"","next-turn");
            Check((string)Call(app,"PinnedSession")==null,"interrupted work releases pin");
            Claude(app,2,""); RefreshMenu(app); Call(app,"OnTrayCommand",2000);
            Claude(app,1,""); c=sessions["fixture-session"]; c.QuietDueUtc=DateTime.UtcNow.AddSeconds(-1);
            Call(app,"FinalizeDue");
            Check(c.State==Session.Celebrating && (string)Call(app,"PinnedSession")==null,"last pinned work celebrates normally after quiet");
            Codex(app,20,"","final-turn");
            Check((string)Call(app,"ComputeDisplaySession")=="codex:fixture-session","active still outranks unpinned celebration");
            for(int i=0;i<12;i++) Call(app,"OnEvent",2,"bounded-"+i,"fixture-project","");
            menu=(IntPtr)Call(app,"CreateTrayMenu");
            try { Check(sessions.Count==8 && GetMenuItemCount(menu)==20 && GetMenuState(menu,2008,0)==uint.MaxValue,"work list remains bounded to eight sessions"); }
            finally { Native.DestroyMenu(menu); }
        }
        finally
        {
            Call(app,"HidePet");
            Get<SpriteAnimator>(app,"_sprite").Dispose();
            Bitmap hud=Get<Bitmap>(app,"_hud"); if(hud!=null) hud.Dispose();
        }
    }

    private static void TestPetControls(string output)
    {
        int style=Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        // A separate, never-shown test window. No real pet, mouse or foreground window is manipulated.
        IntPtr window=Native.CreateWindowEx(style,"STATIC","pet-controls-test",Native.WS_POPUP,40,40,280,340,
            IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        Check(window!=IntPtr.Zero,"isolated placement window");
        var app=new PetApp(); Set(app,"_hwnd",window); Set(app,"_petVisible",false);
        Set(app,"_winW",280); Set(app,"_winH",340); Set(app,"_scale",1f);
        var sessions=Get<Dictionary<string,Session>>(app,"_sessions");
        try
        {
            Check(Get<bool>(app,"_completionSound"),"completion sound defaults to enabled");
            Check(!(bool)Call(app,"ShouldPlayCompletionSound",true),"hidden pet stays silent");
            Set(app,"_petVisible",true);
            Check((bool)Call(app,"ShouldPlayCompletionSound",true) && !(bool)Call(app,"ShouldPlayCompletionSound",false),"sound is eligible only for actual visible completion");
            Call(app,"OnTrayCommand",1009);
            Check(!(bool)Call(app,"ShouldPlayCompletionSound",true),"sound toggle mutes visible completion");
            IntPtr menu=(IntPtr)Call(app,"CreateTrayMenu");
            try { Check((GetMenuState(menu,1009,0)&Native.MF_CHECKED)==0 && MenuText(menu,1009)=="完了音を鳴らす","native sound check reflects mute"); }
            finally { Native.DestroyMenu(menu); }
            Call(app,"OnTrayCommand",1007);
            int movingStyle=Native.GetWindowLong(window,Native.GWL_EXSTYLE);
            Check(Get<bool>(app,"_moveMode") && (movingStyle&Native.WS_EX_TRANSPARENT)==0 &&
                (movingStyle&Native.WS_EX_NOACTIVATE)!=0 && (movingStyle&Native.WS_EX_TOOLWINDOW)!=0,"drag mode changes only click-through behavior");
            Check((IntPtr)Call(app,"WndProc",window,(uint)Native.WM_NCHITTEST,IntPtr.Zero,IntPtr.Zero)==new IntPtr(Native.HTCAPTION),"move mode enables native caption dragging");
            Check((IntPtr)Call(app,"WndProc",window,(uint)Native.WM_MOUSEACTIVATE,IntPtr.Zero,IntPtr.Zero)==new IntPtr(Native.MA_NOACTIVATE),"drag cannot activate pet");
            menu=(IntPtr)Call(app,"CreateTrayMenu");
            try { Check((GetMenuState(menu,1007,0)&Native.MF_CHECKED)!=0 && MenuText(menu,1007).Contains("やめる"),"move mode has a visible menu cancel action"); }
            finally { Native.DestroyMenu(menu); }
            Native.RECT moving=new Native.RECT {left=120,top=140,right=400,bottom=480};
            IntPtr buffer=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.RECT)));
            try
            {
                Marshal.StructureToPtr(moving,buffer,false);
                Call(app,"WndProc",window,(uint)Native.WM_MOVING,IntPtr.Zero,buffer);
                Check(Get<int>(app,"_baseX")==120 && Get<int>(app,"_baseY")==140,"drag updates animation origin before next frame");
            }
            finally { Marshal.FreeHGlobal(buffer); }
            Native.SetWindowPos(window,IntPtr.Zero,90,100,0,0,Native.SWP_NOSIZE|Native.SWP_NOZORDER|Native.SWP_NOACTIVATE);
            // The native loop can restore its initial rectangle on Esc; always take the actual final rectangle.
            Call(app,"WndProc",window,(uint)Native.WM_EXITSIZEMOVE,IntPtr.Zero,IntPtr.Zero);
            Native.RECT actual; Native.GetWindowRect(window,out actual);
            Check(!Get<bool>(app,"_moveMode") && (Native.GetWindowLong(window,Native.GWL_EXSTYLE)&Native.WS_EX_TRANSPARENT)!=0,"drop or cancel restores click-through");
            Check(Get<int>(app,"_baseX")==actual.left && Get<int>(app,"_baseY")==actual.top &&
                actual.right-actual.left==280 && actual.bottom-actual.top==340,"final placement uses native rectangle without resizing");
            Call(app,"SetMoveMode",true); Call(app,"OnTrayCommand",1007);
            Check(!Get<bool>(app,"_moveMode"),"menu can cancel move mode before dragging");
            Call(app,"SetMoveMode",true); Call(app,"HidePet");
            Check(!Get<bool>(app,"_moveMode") && !Get<bool>(app,"_petVisible"),"hide always ends move mode");
            Call(app,"OnTrayCommand",1008);
            Native.GetWindowRect(window,out actual);
            Check(Get<int>(app,"_baseX")==actual.left && Get<int>(app,"_baseY")==actual.top && !IsWindowVisible(window),"reset position works without showing hidden pet");
            Native.RECT primary=new Native.RECT {left=0,top=0,right=1000,bottom=800};
            Point fit=PetApp.FitPosition(300,200,new Rectangle(0,0,280,340),primary);
            Check(fit==new Point(300,200),"free positions are not snapped to corners");
            Check(PetApp.FitPosition(950,790,new Rectangle(0,0,280,340),primary)==new Point(720,460),"card and clones stay inside work area");
            Native.RECT left=new Native.RECT {left=-1200,top=-300,right=0,bottom=700};
            Check(PetApp.FitPosition(-700,-100,new Rectangle(0,0,280,340),left)==new Point(-700,-100),"negative monitor coordinates are supported");
            Check(PetApp.FitPosition(-3000,1800,new Rectangle(0,0,280,340),left)==new Point(-1200,360),"offscreen placement clamps on nearest work area");
            Check(PetApp.FitPosition(20,20,new Rectangle(0,0,280,340),new Native.RECT {right=200,bottom=250})==Point.Empty,"small work areas do not resize the pet");
            Call(app,"MovePetTo",int.MinValue/2,int.MinValue/2);
            Call(app,"WndProc",window,(uint)Native.WM_DISPLAYCHANGE,IntPtr.Zero,IntPtr.Zero);
            Native.GetWindowRect(window,out actual);
            var info=new Native.MONITORINFO(); info.cbSize=Marshal.SizeOf(typeof(Native.MONITORINFO));
            Native.GetMonitorInfo(Native.MonitorFromRect(ref actual,2),ref info);
            Check(PetApp.FitPosition(actual.left,actual.top,new Rectangle(0,0,280,340),info.monitor)==new Point(actual.left,actual.top),"display changes recover an unreachable position");

            Claude(app,2,""); Claude(app,8,"0/1/2"); Claude(app,3,"");
            Session c=sessions["fixture-session"];
            Set(app,"_petVisible",true); Call(app,"RenderCurrent",true,false);
            Check(Get<int>(Get<SpriteAnimator>(app,"_sprite"),"state")==3 && c.ProgressPercent()==25 &&
                c.QuietDueUtc==DateTime.MinValue,"observed input wait selects gesture without altering state or progress");
            Set(app,"_petVisible",false); Claude(app,12,"fixture-child"); Claude(app,3,"");
            Set(app,"_petVisible",true); Call(app,"RenderCurrent",true,false);
            Check(Get<int>(app,"_cloneCount")==1 && Get<int>(app,"_spriteInterval")<=400,"waiting parent retains working clone cadence");
            using(Bitmap preview=(Bitmap)Call(app,"ComposeSpriteFrame")) preview.Save(Path.Combine(output,"waiting-controls.png"));
            Call(app,"OnSpriteTick"); Native.GetWindowRect(window,out actual);
            Check(actual.left==Get<int>(app,"_baseX") && actual.top==Get<int>(app,"_baseY") && !IsWindowVisible(window),"animation keeps chosen position and test window hidden");
            Call(app,"HidePet"); Check(Get<int>(app,"_spriteInterval")==0,"hidden waiting animation stops");
            Claude(app,4,""); Set(app,"_petVisible",true); Call(app,"RenderCurrent",true,false);
            Check(Get<int>(Get<SpriteAnimator>(app,"_sprite"),"state")==1,"observed continuation restores working animation");
            Set(app,"_petVisible",false); Claude(app,1,"");
            DateTime due=c.QuietDueUtc;
            Call(app,"OnTrayCommand",1009); Call(app,"OnTrayCommand",1009);
            Check(c.QuietDueUtc==due && (due-DateTime.UtcNow).TotalSeconds>19,"sound toggling preserves quiet deadline");
            Set(app,"_petVisible",true); Call(app,"RenderCurrent",true,false);
            Check(Get<int>(Get<SpriteAnimator>(app,"_sprite"),"state")==1,"finalizing does not imitate input wait");
            Set(app,"_petVisible",false); c.QuietDueUtc=DateTime.UtcNow.AddSeconds(-1); Call(app,"FinalizeDue");
            Check(c.State==Session.Celebrating && !Get<bool>(app,"_completionSound"),"muted completion still celebrates normally");
            Set(app,"_petVisible",false); Codex(app,20,"","controls-turn"); Codex(app,23,"","controls-turn");
            Set(app,"_petVisible",true); Call(app,"RenderCurrent",true,false);
            Check(Get<int>(Get<SpriteAnimator>(app,"_sprite"),"state")==3,"Codex input wait uses same gesture");
            Set(app,"_petVisible",false); Codex(app,21,"","controls-turn");
            Set(app,"_petVisible",true); Call(app,"RenderCurrent",true,false);
            Check(Get<int>(Get<SpriteAnimator>(app,"_sprite"),"state")==1,"Codex continuation leaves waiting gesture");
        }
        finally
        {
            Call(app,"HidePet"); Get<SpriteAnimator>(app,"_sprite").Dispose();
            Bitmap hud=Get<Bitmap>(app,"_hud"); if(hud!=null) hud.Dispose();
            Native.DestroyWindow(window);
        }
        using(var waiting=new SpriteAnimator()) using(var working=new SpriteAnimator())
        {
            waiting.Select(3,0); working.Select(1,0);
            Check(waiting.Frame(1700,0)==0 && waiting.NextTickMs(0)==1800 && waiting.NextTickMs(0,true)==400,"waiting sleeps between blinks and wakes for active clones");
            using(Bitmap anchor=new Bitmap(128,128))
            {
                using(Graphics g=Graphics.FromImage(anchor)) waiting.Draw(g,new Rectangle(0,0,128,128),0,0);
                int changed=0, outside=0;
                for(int f=1;f<8;f++) using(Bitmap frame=new Bitmap(128,128))
                {
                    using(Graphics g=Graphics.FromImage(frame)) waiting.Draw(g,new Rectangle(0,0,128,128),1800+f*120,0);
                    for(int y=0;y<128;y++) for(int x=0;x<128;x++)
                        if(anchor.GetPixel(x,y)!=frame.GetPixel(x,y))
                        {
                            changed++;
                            if(x<31 || x>=89 || y<53 || y>=73) outside++;
                        }
                    frame.Save(Path.Combine(output,"waiting-blink-"+f+".png"));
                }
                Check(changed>0 && outside==0,"waiting blink changes only eyes, hands and silhouette stay still");
            }
            using(Bitmap a=new Bitmap(128,128)) using(Bitmap b=new Bitmap(128,128))
            {
                using(Graphics g=Graphics.FromImage(a)) waiting.Draw(g,new Rectangle(0,0,128,128),600,1);
                using(Graphics g=Graphics.FromImage(b)) working.Draw(g,new Rectangle(0,0,128,128),600,1);
                bool same=true;
                for(int y=0;y<128 && same;y++) for(int x=0;x<128;x++)
                    if(a.GetPixel(x,y)!=b.GetPixel(x,y)) { same=false; break; }
                Check(same,"waiting parent does not change child artwork or animation");
            }
        }
    }

    private static void TestEdgePlacement(IntPtr window, string output)
    {
        var app=new PetApp(); Set(app,"_hwnd",window); Set(app,"_petVisible",false);
        Set(app,"_winW",280); Set(app,"_winH",340); Set(app,"_scale",1f);
        try
        {
            Native.RECT screen=new Native.RECT {left=0,top=0,right=1000,bottom=800};
            var inset=new Rectangle(24,60,232,218);
            Check(PetApp.FitPosition(-200,-200,inset,screen)==new Point(-24,-60),"top-left transparent margins can leave the display");
            Check(PetApp.FitPosition(1000,800,inset,screen)==new Point(744,522),"right-bottom uses visible edge instead of invisible canvas");
            Check(PetApp.FitPosition(-24,-60,inset,screen)==new Point(-24,-60),"exact edge placement is not pulled inward");
            Check(PetApp.FitPosition(200,250,inset,screen)==new Point(200,250),"interior placement remains free without snapping");
            Native.RECT negative=new Native.RECT {left=-1400,top=-500,right=0,bottom=500};
            Point corner=PetApp.FitPosition(-3000,-3000,inset,negative);
            Check(corner.X+inset.Left==negative.left && corner.Y+inset.Top==negative.top,"visible edges support negative monitor coordinates");
            using(var empty=new Bitmap(12,15))
            {
                Check(SpriteAnimator.MeasureVisibleBounds(empty).IsEmpty,"transparent image has no placement extent");
                empty.SetPixel(2,4,Color.FromArgb(1,0,0,0)); empty.SetPixel(9,12,Color.Black);
                Check(SpriteAnimator.MeasureVisibleBounds(empty)==Rectangle.FromLTRB(2,4,10,13),"alpha scan includes even faint visible border pixels");
            }
            foreach(float scale in new float[] {1f,1.25f,2f})
            {
                int w=(int)(280*scale), h=(int)(340*scale);
                Set(app,"_winW",w); Set(app,"_winH",h); Set(app,"_scale",scale);
                Bitmap old=Get<Bitmap>(app,"_hud"); if(old!=null) old.Dispose();
                Set(app,"_hud",PetRenderer.RenderWorking(w,h,scale,"fixture-project",25,"Claude",0,"工程：端への配置を確認する","経過 00:05"));
                Get<SpriteAnimator>(app,"_sprite").Select(1,0);
                Set(app,"_cloneCount",0); Set(app,"_oldCloneCount",0);
                Rectangle visible=(Rectangle)Call(app,"PlacementBounds");
                Point edge=PetApp.FitPosition(1000,800,visible,screen);
                Check(edge.X+visible.Right==screen.right && edge.Y+visible.Bottom==screen.bottom &&
                    edge.X+w>screen.right && edge.Y+h>screen.bottom,"visible bottom-right reaches edge at DPI "+scale);
                Point top=PetApp.FitPosition(-1000,-1000,visible,screen);
                Check(top.X+visible.Left==0 && top.Y+visible.Top==0,"visible top-left reaches edge at DPI "+scale);
                // The real rendered scene stays within those bounds throughout working motion.
                for(int frame=0;frame<8;frame++)
                    using(Bitmap image=(Bitmap)Call(app,"ComposeSpriteFrameAt",(long)frame*200))
                        Check(visible.Contains(SpriteAnimator.MeasureVisibleBounds(image)),"working animation stays inside edge placement");
                Set(app,"_cloneCount",6); Set(app,"_oldCloneCount",6);
                Rectangle clones=(Rectangle)Call(app,"PlacementBounds");
                Check(clones.Bottom>visible.Bottom && clones.Left<=visible.Left,"visible clone row is retained only when present");
                for(int frame=0;frame<8;frame++)
                    using(Bitmap image=(Bitmap)Call(app,"ComposeSpriteFrameAt",(long)frame*400+1000))
                        Check(clones.Contains(SpriteAnimator.MeasureVisibleBounds(image)),"clone animation stays inside edge placement");
                edge=PetApp.FitPosition(1000,800,clones,screen);
                Check(edge.Y+clones.Bottom==screen.bottom,"clones can also reach bottom edge without invisible padding");
                Set(app,"_oldCloneCount",0);
                long transitionStart=Get<Stopwatch>(app,"_clock").ElapsedMilliseconds;
                Set(app,"_clonesChanged",transitionStart);
                Rectangle arrival=(Rectangle)Call(app,"PlacementBounds");
                for(int tick=0;tick<5;tick++)
                    using(Bitmap image=(Bitmap)Call(app,"ComposeSpriteFrameAt",transitionStart+tick*100))
                        Check(arrival.Contains(SpriteAnimator.MeasureVisibleBounds(image)),"arrival smoke also remains inside placement bounds");
                Set(app,"_cloneCount",0); Set(app,"_oldCloneCount",0);
                Get<SpriteAnimator>(app,"_sprite").Select(2,0);
                Rectangle success=(Rectangle)Call(app,"PlacementBounds");
                for(int frame=0;frame<8;frame++)
                    using(Bitmap image=(Bitmap)Call(app,"ComposeSpriteFrameAt",(long)frame*160))
                        Check(success.Contains(SpriteAnimator.MeasureVisibleBounds(image)),"all success poses fit without animation-time repositioning");
            }
            Set(app,"_scale",1f); Set(app,"_winW",280); Set(app,"_winH",340);
            Get<Bitmap>(app,"_hud").Dispose(); Set(app,"_hud",PetRenderer.RenderIdle(280,340,1f));
            Set(app,"_cloneCount",0); Set(app,"_oldCloneCount",0);
            Get<SpriteAnimator>(app,"_sprite").Select(0,0);
            Rectangle idle=(Rectangle)Call(app,"PlacementBounds");
            Check(idle.Left>24 && idle.Bottom<340,"idle has no reserved working card or absent clone margins");
            var monitor=new Native.MONITORINFO(); monitor.cbSize=Marshal.SizeOf(typeof(Native.MONITORINFO));
            Native.RECT probe=new Native.RECT {right=280,bottom=340};
            Native.GetMonitorInfo(Native.MonitorFromRect(ref probe,2),ref monitor);
            Set(app,"_baseX",monitor.monitor.right); Set(app,"_baseY",monitor.monitor.bottom);
            Call(app,"KeepOnScreen");
            Check(Get<int>(app,"_baseX")+idle.Right==monitor.monitor.right &&
                Get<int>(app,"_baseY")+idle.Bottom==monitor.monitor.bottom,"native placement uses physical display, including taskbar area");
            // A new request near an edge can grow its card; it must be kept visible without resizing.
            Claude(app,2,""); Claude(app,8,"0/1/2");
            Set(app,"_petVisible",true); Call(app,"RenderCurrent",true,false);
            Rectangle grown=(Rectangle)Call(app,"PlacementBounds");
            var actual=new Native.RECT {left=Get<int>(app,"_baseX")+grown.Left,top=Get<int>(app,"_baseY")+grown.Top,
                right=Get<int>(app,"_baseX")+grown.Right,bottom=Get<int>(app,"_baseY")+grown.Bottom};
            Native.GetMonitorInfo(Native.MonitorFromRect(ref actual,2),ref monitor);
            Check(actual.left>=monitor.monitor.left && actual.top>=monitor.monitor.top && actual.right<=monitor.monitor.right &&
                actual.bottom<=monitor.monitor.bottom && Get<Bitmap>(app,"_hud").Size==new Size(280,340),"growing content stays visible and card size is unchanged");
            Set(app,"_moveMode",true); Set(app,"_baseX",123); Set(app,"_baseY",234);
            Call(app,"RenderCurrent",true,false);
            Check(Get<int>(app,"_baseX")==123 && Get<int>(app,"_baseY")==234,"render cannot override an ongoing user drag");
            Set(app,"_moveMode",false);
            using(Bitmap image=(Bitmap)Call(app,"ComposeSpriteFrame")) image.Save(Path.Combine(output,"edge-placement.png"));
        }
        finally
        {
            Call(app,"HidePet"); Get<SpriteAnimator>(app,"_sprite").Dispose();
            Bitmap hud=Get<Bitmap>(app,"_hud"); if(hud!=null) hud.Dispose();
        }
    }

    public static int Main(string[] args)
    {
        try { Run(args[0]); Console.WriteLine("PASS: " + assertions + " assertions; state, sprite, privacy, render and resource checks."); return 0; }
        catch(Exception e) { Console.WriteLine("FAIL: " + e.GetBaseException().Message); return 1; }
    }
    private static void Run(string output)
    {
        Directory.CreateDirectory(output);
        Check(BackgroundWork.Read("{}") == BackgroundWork.Missing,"older Stop metadata stays unknown");
        Check(BackgroundWork.Read("{\"background_tasks\":[]}") == BackgroundWork.Clear,"empty background registry");
        Check(BackgroundWork.Read("{\"background_tasks\":[{\"type\":\"shell\",\"status\":\"running\"}]}") == BackgroundWork.Pending,"background shell blocks completion");
        Check(BackgroundWork.Read("{\"background_tasks\":[{\"type\":\"subagent\"}]}") == BackgroundWork.Pending,"background subagent blocks completion");
        Check(BackgroundWork.Read("{\"background_tasks\":[{\"type\":\"monitor\"}],\"session_crons\":[{\"recurring\":true}]}") == BackgroundWork.Clear,"persistent monitoring does not block forever");
        Check(BackgroundWork.Read("{\"background_tasks\":[{\"type\":\"monitor\"},{\"type\":\"shell\"}]}") == BackgroundWork.Pending,"monitor cannot hide finite work");
        Check(BackgroundWork.Read("{\"background_tasks\":null}") == BackgroundWork.Pending,"null registry is not empty");
        Check(BackgroundWork.Read("{\"background_tasks\":[}") == BackgroundWork.Pending,"malformed registry fails closed");
        Check(BackgroundWork.Read("{\"background_tasks\":[],\"background_tasks\":[]}") == BackgroundWork.Pending,"duplicate metadata fails closed");
        Check(BackgroundWork.Read("{\"tool_response\":{\"background_tasks\":[]}}") == BackgroundWork.Missing,"nested registry cannot authorize completion");
        Check(BackgroundWork.Read("{\"last_assistant_message\":\"fake \\\"background_tasks\\\":[]\",\"background_tasks\":[{\"type\":\"shell\"}]}") == BackgroundWork.Pending,"response text cannot authorize completion");

        string plan = WorkDetails.Snapshot("{\"prompt\":\"ignored\",\"tool_input\":{\"plan\":[{\"step\":\"済\",\"status\":\"completed\"},{\"step\":\"表示を検証する\",\"status\":\"in_progress\"},{\"step\":\"後\",\"status\":\"pending\"}]},\"tool_response\":{\"status\":\"completed\"}}",true);
        Check(plan.StartsWith("1/1/3|") && WorkDetails.Label(plan)=="表示を検証する","active plan label and counts ignore response");
        Check(WorkDetails.Snapshot("{\"tool_response\":{\"tool_input\":{\"plan\":[]}}}",true)==null,"nested plan is not input");
        Check(WorkDetails.Snapshot("{\"tool_input\":{\"plan\":[{\"status\":\"unknown\"}]}}",true)==null,"unknown status cannot inflate progress");
        Check(WorkDetails.Snapshot("{\"tool_input\":{\"plan\":[],\"plan\":[]}}",true)==null,"duplicate plan rejected");
        Check(WorkDetails.Snapshot("{\"tool_input\":{\"plan\":[{\"status\":\"pending\"},]}}",true)==null,"trailing comma rejected");
        string todo=WorkDetails.Snapshot("{\"tool_input\":{\"todos\":[{\"content\":\"check\",\"activeForm\":\"確認しています\",\"status\":\"in_progress\"}]}}",false);
        Check(WorkDetails.Label(todo)=="確認しています","Claude activeForm label");
        Check(WorkDetails.Label(WorkDetails.Snapshot("{\"tool_input\":{\"todos\":[{\"content\":\"確認する\",\"status\":\"in_progress\"}]}}",false))=="確認する","Claude content fallback");
        Check(WorkDetails.Snapshot("{\"tool_input\":{\"todos\":[]}}",false)=="0/0/0|","empty plan clears details");
        Check(WorkDetails.Label("1/1/3")=="" && WorkDetails.Label("1/1/3|!!!")=="","legacy and malformed title safe");
        Check(WorkDetails.Label(WorkDetails.Snapshot("{\"tool_input\":{\"plan\":[{\"step\":\"a\",\"status\":\"in_progress\"},{\"step\":\"b\",\"status\":\"in_progress\"}]}}",true))=="","multiple active steps do not imply a single current task");
        Check(WorkDetails.Clean("a\nb\u202ec")=="a b c" && WorkDetails.Clean(new string('x',200)).Length==120,"bounded single-line labels");
        Check(WorkDetails.Elapsed(3599000,true)=="経過 59:59" && WorkDetails.Elapsed(3600000,true)=="経過 1:00:00","elapsed hour boundary");
        Check(WorkDetails.Elapsed(-1,false)=="観測から 00:00","unknown request start is explicit");
        var roster = new SubagentRoster();
        roster.Apply(true,"a"); roster.Apply(true,"a"); Check(roster.Count==1,"duplicate start");
        roster.Apply(false,"a"); roster.Apply(false,"a"); roster.Apply(true,"a"); Check(roster.Count==0,"stop tombstone");
        roster.Apply(false,"b"); roster.Apply(true,"b"); Check(roster.Count==0,"stop before start");
        roster.Apply(true,""); Check(roster.Count==0,"missing identity");
        roster.Reset(); roster.Apply(true,"a"); Check(roster.Count==1,"new request reset");
        for(int i=0;i<400;i++) roster.Apply(true,"fixture-"+i);
        Check(roster.Count==256,"bounded roster");
        string token=AgentIdentity.Token("{\"agent_id\":\"fixture-agent\"}");
        Check(token.Length==64 && !token.Contains("fixture"),"opaque identity");
        Check(AgentIdentity.Token("{\"tool_response\":{\"agent_id\":\"fake\"}}")=="","nested id excluded");
        Check(AgentIdentity.Token("{\"tool_response\":{\"agent_id\":\"fake\"},\"agent_id\":\"fixture-agent\"}")==token,"top-level identity");
        Check(AgentIdentity.Token("{\"agent_id\":\"bad\\nvalue\"}")=="","control rejection");
        Check(AgentIdentity.Token("{\"text\":\"agent_id\",\"agent_id\":\"fixture-agent\"}")==token,"string values skipped");

        using(var sprite=new SpriteAnimator())
        {
            sprite.Select(0,100); Check(sprite.Frame(100,0)==0,"idle first");
            Check(sprite.Frame(4099,0)==0 && sprite.NextTickMs(100)==4000,"idle holds still for four seconds");
            Check(sprite.Frame(4580,0)==6 && sprite.NextTickMs(4580)==80,"short idle blink");
            Check(sprite.Frame(4740,0)==0 && sprite.NextTickMs(4740)==4000,"idle loop returns to stillness");
            sprite.Select(1,5000); Check(sprite.Frame(5400,0)==2,"working clock");
            sprite.Select(1,5410); Check(sprite.Frame(5400,0)==2,"activity does not restart loop");
            Check(sprite.Frame(5200,1)==1 && sprite.Frame(5400,1)==2,"clone movement is slower");
            using(Bitmap anchor=new Bitmap(128,128))
            {
                using(Graphics g=Graphics.FromImage(anchor)) sprite.Draw(g,new Rectangle(0,0,128,128),5000,0);
                int changed=0, outside=0;
                for(int f=1;f<8;f++) using(Bitmap frame=new Bitmap(128,128))
                {
                    using(Graphics g=Graphics.FromImage(frame)) sprite.Draw(g,new Rectangle(0,0,128,128),5000+f*200,0);
                    for(int y=0;y<128;y++) for(int x=0;x<128;x++)
                        if(anchor.GetPixel(x,y)!=frame.GetPixel(x,y))
                        {
                            changed++;
                            if(x<38 || x>=88 || y<76 || y>=103) outside++;
                        }
                }
                Check(outside==0,"working head feet scarf stay pixel-identical");
                Check(changed>0,"working hand motion remains visible");
            }
            sprite.Select(2,6000); Check(sprite.Frame(9999,0)==7 && !sprite.NeedsTick(9999),"one-shot settles");
            using(Bitmap frame=new Bitmap(128,128))
            {
                using(Graphics g=Graphics.FromImage(frame)) sprite.Draw(g,new Rectangle(0,0,128,128),9999,0);
                Check(frame.GetPixel(0,0).A==0,"real alpha");
                int ink=0; for(int y=0;y<128;y++) for(int x=0;x<128;x++) if(frame.GetPixel(x,y).A>128) ink++;
                Check(ink>3000 && ink<14000,"sprite silhouette");
            }
        }
        TestClaudeProgressAdapter();

        // Message-only test window: never targets or shows the user's running pet.
        IntPtr hwnd=Native.CreateWindowEx(0,"STATIC","ninja-tests",0,0,0,1,1,new IntPtr(-3),IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        Check(hwnd!=IntPtr.Zero,"isolated test window");
        PetApp app=new PetApp(); Set(app,"_hwnd",hwnd); Set(app,"_petVisible",false);
        var sessions=Get<Dictionary<string,Session>>(app,"_sessions");
        try
        {
            TestClaudeProgressDiagnostics(app, sessions);
            TestWorkSelection(hwnd, output);
            TestPetControls(output);
            TestEdgePlacement(hwnd, output);
            Claude(app,12,"a"); Check(sessions.Count==0,"child does not create root");
            Claude(app,2,""); Claude(app,12,"a"); Claude(app,12,"a");
            Session c=sessions["fixture-session"]; Check(c.Subagents.Count==1,"Claude clone");
            Claude(app,13,"a"); Check(c.Subagents.Count==0 && c.State==Session.Working,"child stop not completion");
            Claude(app,1,""); Check(c.State==Session.Finalizing && (c.QuietDueUtc-DateTime.UtcNow).TotalSeconds>19,"20s quiet retained");
            Claude(app,12,"b"); Check(c.State==Session.Working && c.QuietDueUtc==DateTime.MinValue,"child continuation cancels quiet");
            Claude(app,2,""); Check(c.Subagents.Count==0,"request clears clones");
            Claude(app,1,""); c.SnapTotal=8; c.SnapDone=0; c.QuietDueUtc=DateTime.UtcNow.AddSeconds(-1);
            Call(app,"FinalizeDue"); Check(c.State==Session.Celebrating,"completion independent of progress");
            Claude(app,12,"late"); Check(c.Subagents.Count==0,"no clones after completion");
            Call(app,"OnRevert"); Check(sessions.Count==0,"celebration cleanup");

            Claude(app,2,""); c=sessions["fixture-session"];
            c.LastAtUtc=DateTime.UtcNow.AddMinutes(-10);
            Call(app,"FinalizeDue"); Check(c.State==Session.Working,"long silence without Stop never completes");
            Claude(app,1,BackgroundWork.Pending);
            Call(app,"FinalizeDue"); Check(c.State==Session.Working && c.QuietDueUtc==DateTime.MinValue,"background Stop has no completion deadline");
            Claude(app,1,BackgroundWork.Clear); Claude(app,14,"");
            Claude(app,1,BackgroundWork.Clear); Claude(app,4,"");
            Call(app,"FinalizeDue"); Check(!sessions.ContainsKey("fixture-session"),"failure cancels quiet and rejects late work/Stop");
            Claude(app,2,""); Claude(app,1,BackgroundWork.Missing);
            c=sessions["fixture-session"]; Check(c.State==Session.Finalizing,"legacy Stop retains quiet fallback");
            Claude(app,1,BackgroundWork.Pending);
            Check(c.State==Session.Working && c.QuietDueUtc==DateTime.MinValue,"background Stop cancels older clear Stop");
            Claude(app,5,"");

            Codex(app,20,"","turn-a"); Codex(app,22,"1/1/4","turn-a");
            Session d=sessions["codex:fixture-session"]; Check(d.ProgressPercent()==37,"root plan unchanged");
            long started = d.StartedTick;
            Codex(app,22,plan,"turn-a");
            Check(d.CurrentWork=="表示を検証する" && d.ProgressPercent()==50 && d.StartedTick==started,"plan title updates without resetting elapsed");
            Codex(app,22,"2/0/2|","old-turn");
            Check(d.CurrentWork=="表示を検証する","old turn cannot replace current title");
            Codex(app,26,"a","turn-a"); Check(d.CurrentWork=="" && d.Subagents.Count==1 && d.ProgressPercent()==-1,"Codex fail-closed progress");
            Codex(app,26,"b","old-turn"); Check(d.Subagents.Count==1,"old turn start rejected");
            Codex(app,27,"a","old-turn"); Check(d.Subagents.Count==1,"old turn stop rejected");
            Codex(app,25,"","old-turn"); Check(sessions.ContainsKey("codex:fixture-session") && d.Subagents.Count==1,"old session end rejected");
            Codex(app,27,"a","turn-a"); Codex(app,22,"4/0/4","turn-a");
            Check(d.State==Session.Working && d.ProgressPercent()==-1 && d.Subagents.Count==0,"child stop preserves fail-closed");
            Codex(app,20,"","turn-b"); Check(d.CurrentWork=="" && d.ObservedStart && d.StartedTick>=started && !d.TurnHasSubagent && d.Subagents.Count==0,"new Codex turn resets");
            Codex(app,24,"","turn-b"); Codex(app,28,"","turn-a");
            Check(d.State==Session.Finalizing,"old turn interrupt cannot cancel current Stop");
            Codex(app,28,"",""); Check(d.State==Session.Finalizing,"missing interrupt turn ignored");
            Codex(app,28,"","turn-b"); Codex(app,24,"","turn-b"); Codex(app,21,"","turn-b");
            Codex(app,23,"","turn-b"); Codex(app,24,"","turn-b");
            Call(app,"FinalizeDue"); Check(!sessions.ContainsKey("codex:fixture-session"),"interrupt cancels completion and rejects late events");
            Codex(app,20,"","turn-b"); d=sessions["codex:fixture-session"];
            Codex(app,26,"c","turn-a"); Check(d.Subagents.Count==0,"delayed prior turn rejected");
            Claude(app,2,""); Claude(app,12,"a"); Codex(app,26,"a","turn-b");
            Check(sessions.Count==2 && sessions["fixture-session"].Subagents.Count==1 && d.Subagents.Count==1,"provider isolation");
            Claude(app,1,""); c=sessions["fixture-session"]; c.QuietDueUtc=DateTime.UtcNow.AddSeconds(-1);
            Call(app,"FinalizeDue"); Check(!sessions.ContainsKey("fixture-session") && d.State==Session.Working,"other active suppresses completion");

            Set(app,"_winW",280); Set(app,"_winH",340); Set(app,"_scale",1f);
            Set(app,"_hud",PetRenderer.RenderWorking(280,340,1f,"fixture-project",65,"Codex",0,"工程：ビルドと動作検証を行い、仕様を記録する","経過 05:23"));
            Set(app,"_cloneCount",9); Set(app,"_oldCloneCount",9);
            Get<SpriteAnimator>(app,"_sprite").Select(1,0);
            using(Bitmap frame=(Bitmap)Call(app,"ComposeSpriteFrame")) frame.Save(Path.Combine(output,"ninja-working.png"));
            foreach (float scale in new float[] { 1f, 1.25f, 2f })
                using (Bitmap preview=PetRenderer.RenderWorking((int)(280*scale),(int)(340*scale),scale,
                    "fixture-project",65,"Codex · GPT-6-astra",3,"工程："+new string('検',120),"経過 12:34:56"))
                    preview.Save(Path.Combine(output,"details-"+(int)(scale*100)+".png"));
            for(int i=0;i<16;i++)
            {
                using(Bitmap frame=(Bitmap)Call(app,"ComposeSpriteFrameAt",(long)i*200)) frame.Save(Path.Combine(output,"working-"+i+".png"));
            }
            Get<Bitmap>(app,"_hud").Dispose();
            Set(app,"_hud",PetRenderer.RenderCelebrate(560,560,2f,"fixture-project","Codex",0));
            Set(app,"_winW",560); Set(app,"_winH",560); Set(app,"_scale",2f);
            Set(app,"_cloneCount",0); Set(app,"_oldCloneCount",0);
            Get<SpriteAnimator>(app,"_sprite").Select(2,-2000);
            using(Bitmap frame=(Bitmap)Call(app,"ComposeSpriteFrame")) frame.Save(Path.Combine(output,"ninja-success-200pct.png"));
            Get<Bitmap>(app,"_hud").Dispose();
            Set(app,"_hud",PetRenderer.RenderIdle(350,350,1.25f));
            Set(app,"_winW",350); Set(app,"_winH",350); Set(app,"_scale",1.25f);
            Get<SpriteAnimator>(app,"_sprite").Select(0,0);
            using(Bitmap frame=(Bitmap)Call(app,"ComposeSpriteFrame")) frame.Save(Path.Combine(output,"ninja-idle-125pct.png"));
            Get<Bitmap>(app,"_hud").Dispose();
            Set(app,"_hud",PetRenderer.RenderIdle(280,280,1f));
            Set(app,"_winW",280); Set(app,"_winH",280); Set(app,"_scale",1f);
            for(int i=0;i<8;i++)
                using(Bitmap frame=(Bitmap)Call(app,"ComposeSpriteFrameAt",4000L+i*80)) frame.Save(Path.Combine(output,"idle-"+i+".png"));

            // Restart idle clock so this timing assertion does not depend on QA export duration.
            SpriteAnimator idleActor=Get<SpriteAnimator>(app,"_sprite");
            idleActor.Select(1,0); idleActor.Select(0,Get<Stopwatch>(app,"_clock").ElapsedMilliseconds);
            Set(app,"_petVisible",true); Call(app,"ArmSpriteTimer");
            Check(Get<int>(app,"_spriteInterval")>3900,"idle timer sleeps through still interval");
            Call(app,"HidePet"); Check(Get<int>(app,"_spriteInterval")==0,"hidden timer stopped");
            Get<SpriteAnimator>(app,"_sprite").Select(2,-10000); Set(app,"_petVisible",true); Call(app,"ArmSpriteTimer");
            Check(Get<int>(app,"_spriteInterval")==0,"settled success timer stopped");

            sessions.Clear();
            Codex(app,20,"","clock-turn");
            d=sessions["codex:fixture-session"];
            Codex(app,22,plan,"clock-turn");
            Set(app,"_winH",340);
            d.StartedTick=Stopwatch.GetTimestamp()-Stopwatch.Frequency*5;
            Call(app,"RenderCurrent",true,false);
            Bitmap cached=Get<Bitmap>(app,"_hud");
            Call(app,"RenderCurrent",false,false);
            Check(object.ReferenceEquals(cached,Get<Bitmap>(app,"_hud")),"unchanged clock reuses HUD cache");
            d.StartedTick-=Stopwatch.Frequency*3;
            Call(app,"OnSpriteTick");
            Check(!object.ReferenceEquals(cached,Get<Bitmap>(app,"_hud")) && d.ProgressPercent()==50 && d.QuietDueUtc==DateTime.MinValue,"clock advances without changing progress or completion");
            Call(app,"HidePet"); cached=Get<Bitmap>(app,"_hud"); d.StartedTick-=Stopwatch.Frequency*3;
            Call(app,"OnSpriteTick");
            Check(object.ReferenceEquals(cached,Get<Bitmap>(app,"_hud")) && Get<int>(app,"_spriteInterval")==0,"hidden clock does not redraw");
            Set(app,"_petVisible",true); Call(app,"RenderCurrent",false,false);
            Check(!object.ReferenceEquals(cached,Get<Bitmap>(app,"_hud")),"show catches up elapsed time");

            // Resource stability of the actual scene composition path.
            for(int i=0;i<20;i++) using(Bitmap frame=(Bitmap)Call(app,"ComposeSpriteFrame")) { }
            GC.Collect(); GC.WaitForPendingFinalizers();
            int before=GetGuiResources(Process.GetCurrentProcess().Handle,0);
            var timer=Stopwatch.StartNew();
            for(int i=0;i<500;i++) using(Bitmap frame=(Bitmap)Call(app,"ComposeSpriteFrame")) { }
            timer.Stop(); GC.Collect(); GC.WaitForPendingFinalizers();
            int after=GetGuiResources(Process.GetCurrentProcess().Handle,0);
            Check(after<=before+2,"no GDI growth after 500 frames");
            Console.WriteLine("Render check: 500 frames, GDI delta="+(after-before)+", elapsed_ms="+timer.ElapsedMilliseconds);
        }
        finally
        {
            Get<SpriteAnimator>(app,"_sprite").Dispose();
            Bitmap hud=Get<Bitmap>(app,"_hud"); if(hud!=null) hud.Dispose();
            Native.DestroyWindow(hwnd);
        }
    }
}
