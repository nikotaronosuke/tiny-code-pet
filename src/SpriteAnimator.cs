using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

namespace ClaudePet
{
    internal sealed class SpriteRegion
    {
        public int x { get; set; }
        public int y { get; set; }
        public int width { get; set; }
        public int height { get; set; }
    }
    internal sealed class SpriteClip
    {
        public int row { get; set; }
        public int frames { get; set; }
        public int frameMs { get; set; }
        public bool loop { get; set; }
        public int holdMs { get; set; }
        public int cloneFrameMs { get; set; }
        public SpriteRegion[] motionRegions { get; set; }
    }
    internal sealed class SpriteSpec
    {
        public int cellWidth { get; set; }
        public int cellHeight { get; set; }
        public SpriteClip idle { get; set; }
        public SpriteClip working { get; set; }
        public SpriteClip success { get; set; }
    }
    internal sealed class SpriteAnimator : IDisposable
    {
        private readonly Bitmap atlas;
        private readonly SpriteSpec spec;
        private long started;
        private int state = -1;
        public SpriteAnimator()
        {
            Assembly a = Assembly.GetExecutingAssembly();
            using (Stream png = a.GetManifestResourceStream("ninja.png"))
            using (Bitmap source = new Bitmap(png)) atlas = new Bitmap(source);
            using (Stream json = a.GetManifestResourceStream("ninja.json"))
            using (StreamReader reader = new StreamReader(json))
                spec = new JavaScriptSerializer().Deserialize<SpriteSpec>(reader.ReadToEnd());
            Validate(spec.idle); Validate(spec.working); Validate(spec.success);
        }
        private void Validate(SpriteClip clip)
        {
            if (spec.cellWidth <= 0 || spec.cellHeight <= 0 || clip == null || clip.row < 0 ||
                clip.frames < 1 || clip.frameMs < 50 || clip.frames * spec.cellWidth > atlas.Width ||
                (clip.row + 1) * spec.cellHeight > atlas.Height || clip.holdMs < 0 || clip.holdMs > 60000 ||
                (clip.cloneFrameMs != 0 && clip.cloneFrameMs < clip.frameMs)) throw new InvalidDataException("Invalid ninja atlas");
            if (clip.motionRegions != null)
                foreach (SpriteRegion region in clip.motionRegions)
                    if (region == null || region.x < 0 || region.y < 0 || region.width < 1 || region.height < 1 ||
                        region.x + region.width > spec.cellWidth || region.y + region.height > spec.cellHeight)
                        throw new InvalidDataException("Invalid motion region");
        }
        private SpriteClip Clip { get { return state == 0 ? spec.idle : state == 2 ? spec.success : spec.working; } }
        public void Select(int next, long now) { if (state != next) { state = next; started = now; } }
        public int Interval { get { return Clip.frameMs; } }
        public bool NeedsTick(long now) { return Clip.loop || now - started < (long)Clip.frames * Clip.frameMs; }
        public int NextTickMs(long now)
        {
            long elapsed = Math.Max(0, now-started);
            if (Clip.loop)
            {
                long phase = elapsed % (Clip.holdMs + (long)Clip.frames * Clip.frameMs);
                // A single timer wakes after the still interval; no rapid idle ticks.
                if (phase < Clip.holdMs) return (int)(Clip.holdMs-phase);
                elapsed = phase-Clip.holdMs;
            }
            return Math.Max(10, Clip.frameMs-(int)(elapsed % Clip.frameMs));
        }
        public int Frame(long now, int offset)
        {
            int duration = offset > 0 && Clip.cloneFrameMs > 0 ? Clip.cloneFrameMs : Clip.frameMs;
            long elapsed = Math.Max(0, now-started);
            if (!Clip.loop) return (int)Math.Min(elapsed/duration, Clip.frames-1);
            long phase = (elapsed + (long)offset*duration) % (Clip.holdMs + (long)Clip.frames*duration);
            return phase < Clip.holdMs ? 0 : (int)((phase-Clip.holdMs)/duration);
        }
        public void Draw(Graphics g, Rectangle bounds, long now, int offset)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int frame = Frame(now, offset);
            Rectangle source = new Rectangle(frame * spec.cellWidth, Clip.row * spec.cellHeight, spec.cellWidth, spec.cellHeight);
            if (Clip.motionRegions == null)
            {
                g.DrawImage(atlas, bounds, source, GraphicsUnit.Pixel);
                return;
            }
            // Anchor the silhouette to frame zero. Generated pose-to-pose variation
            // cannot shake the head, feet or scarf outside the intended small areas.
            g.DrawImage(atlas, bounds, new Rectangle(0, source.Y, spec.cellWidth, spec.cellHeight), GraphicsUnit.Pixel);
            if (frame == 0) return;
            foreach (SpriteRegion region in Clip.motionRegions)
            {
                GraphicsState saved = g.Save();
                try
                {
                    g.SetClip(new RectangleF(bounds.X + (float)region.x*bounds.Width/spec.cellWidth,
                        bounds.Y + (float)region.y*bounds.Height/spec.cellHeight,
                        (float)region.width*bounds.Width/spec.cellWidth, (float)region.height*bounds.Height/spec.cellHeight), CombineMode.Intersect);
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(atlas, bounds, source, GraphicsUnit.Pixel);
                }
                finally { g.Restore(saved); }
            }
        }
        public void Dispose() { atlas.Dispose(); }
    }
}
