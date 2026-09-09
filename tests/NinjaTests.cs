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
    private static int assertions;
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
    private static void Check(bool ok, string name) { assertions++; if (!ok) throw new Exception(name); }
    private static object Call(object app, string name, params object[] args) { return app.GetType().GetMethod(name,Flags).Invoke(app,args); }
    private static void Set(object app, string name, object value) { app.GetType().GetField(name,Flags).SetValue(app,value); }
    private static T Get<T>(object app, string name) { return (T)app.GetType().GetField(name,Flags).GetValue(app); }
    private static void Claude(PetApp a, int ev, string extra) { Call(a,"OnEvent",ev,"fixture-session","fixture-project",extra); }
    private static void Codex(PetApp a, int ev, string extra, string turn) { Call(a,"OnCodexEvent",ev,"fixture-session","fixture-project",extra,turn); }
    public static int Main(string[] args)
    {
        try { Run(args[0]); Console.WriteLine("PASS: " + assertions + " assertions; state, sprite, privacy, render and resource checks."); return 0; }
        catch(Exception e) { Console.WriteLine("FAIL: " + e.GetBaseException().Message); return 1; }
    }
    private static void Run(string output)
    {
        Directory.CreateDirectory(output);
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
        // Message-only test window: never targets or shows the user's running pet.
        IntPtr hwnd=Native.CreateWindowEx(0,"STATIC","ninja-tests",0,0,0,1,1,new IntPtr(-3),IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        Check(hwnd!=IntPtr.Zero,"isolated test window");
        PetApp app=new PetApp(); Set(app,"_hwnd",hwnd); Set(app,"_petVisible",false);
        var sessions=Get<Dictionary<string,Session>>(app,"_sessions");
        try
        {
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

            Codex(app,20,"","turn-a"); Codex(app,22,"1/1/4","turn-a");
            Session d=sessions["codex:fixture-session"]; Check(d.ProgressPercent()==37,"root plan unchanged");
            Codex(app,26,"a","turn-a"); Check(d.Subagents.Count==1 && d.ProgressPercent()==-1,"Codex fail-closed progress");
            Codex(app,26,"b","old-turn"); Check(d.Subagents.Count==1,"old turn start rejected");
            Codex(app,27,"a","old-turn"); Check(d.Subagents.Count==1,"old turn stop rejected");
            Codex(app,25,"","old-turn"); Check(sessions.ContainsKey("codex:fixture-session") && d.Subagents.Count==1,"old session end rejected");
            Codex(app,27,"a","turn-a"); Codex(app,22,"4/0/4","turn-a");
            Check(d.State==Session.Working && d.ProgressPercent()==-1 && d.Subagents.Count==0,"child stop preserves fail-closed");
            Codex(app,20,"","turn-b"); Check(!d.TurnHasSubagent && d.Subagents.Count==0,"new Codex turn resets");
            Codex(app,26,"c","turn-a"); Check(d.Subagents.Count==0,"delayed prior turn rejected");
            Claude(app,2,""); Claude(app,12,"a"); Codex(app,26,"a","turn-b");
            Check(sessions.Count==2 && sessions["fixture-session"].Subagents.Count==1 && d.Subagents.Count==1,"provider isolation");
            Claude(app,1,""); c=sessions["fixture-session"]; c.QuietDueUtc=DateTime.UtcNow.AddSeconds(-1);
            Call(app,"FinalizeDue"); Check(!sessions.ContainsKey("fixture-session") && d.State==Session.Working,"other active suppresses completion");

            Set(app,"_winW",280); Set(app,"_winH",280); Set(app,"_scale",1f);
            Set(app,"_hud",PetRenderer.RenderWorking(280,280,1f,"fixture-project",65,"Claude",0));
            Set(app,"_cloneCount",9); Set(app,"_oldCloneCount",9);
            Get<SpriteAnimator>(app,"_sprite").Select(1,0);
            using(Bitmap frame=(Bitmap)Call(app,"ComposeSpriteFrame")) frame.Save(Path.Combine(output,"ninja-working.png"));
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
