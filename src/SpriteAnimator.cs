using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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
        public SpriteClip waiting { get; set; }
        public SpriteClip success { get; set; }
    }
    internal sealed class SpriteAnimator : IDisposable
    {
        private readonly Bitmap atlas;
        private readonly SpriteSpec spec;
        private long started;
        private int state = -1;
        private readonly Dictionary<string, Rectangle> visibleBounds = new Dictionary<string, Rectangle>();
        public SpriteAnimator()
        {
            Assembly a = Assembly.GetExecutingAssembly();
            using (Stream png = a.GetManifestResourceStream("ninja.png"))
            using (Bitmap source = new Bitmap(png)) atlas = new Bitmap(source);
            using (Stream json = a.GetManifestResourceStream("ninja.json"))
            using (StreamReader reader = new StreamReader(json))
                spec = new JavaScriptSerializer().Deserialize<SpriteSpec>(reader.ReadToEnd());
            Validate(spec.idle); Validate(spec.working); Validate(spec.waiting); Validate(spec.success);
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
        private SpriteClip Clip { get { return state == 0 ? spec.idle : state == 2 ? spec.success : state == 3 ? spec.waiting : spec.working; } }
        public void Select(int next, long now) { if (state != next) { state = next; started = now; } }
        public int Interval { get { return Clip.frameMs; } }
        public bool NeedsTick(long now) { return Clip.loop || now - started < (long)Clip.frames * Clip.frameMs; }
        public int NextTickMs(long now, bool withClones = false)
        {
            int interval = MainNextTickMs(now);
            if (state == 3 && withClones)
            {
                // The parent waits, but its observed children may still be working.
                int cadence = spec.working.cloneFrameMs > 0 ? spec.working.cloneFrameMs : spec.working.frameMs;
                interval = Math.Min(interval, cadence - (int)(Math.Max(0, now-started) % cadence));
            }
            return interval;
        }
        private int MainNextTickMs(long now)
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
            SpriteClip clip = ClipFor(offset);
            int duration = offset > 0 && clip.cloneFrameMs > 0 ? clip.cloneFrameMs : clip.frameMs;
            long elapsed = Math.Max(0, now-started);
            if (!clip.loop) return (int)Math.Min(elapsed/duration, clip.frames-1);
            long phase = (elapsed + (long)offset*duration) % (clip.holdMs + (long)clip.frames*duration);
            return phase < clip.holdMs ? 0 : (int)((phase-clip.holdMs)/duration);
        }
        private SpriteClip ClipFor(int offset) { return state == 3 && offset > 0 ? spec.working : Clip; }
        public void Draw(Graphics g, Rectangle bounds, long now, int offset)
        {
            DrawFrame(g, bounds, ClipFor(offset), Frame(now, offset));
        }
        private void DrawFrame(Graphics g, Rectangle bounds, SpriteClip clip, int frame)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Rectangle source = new Rectangle(frame * spec.cellWidth, clip.row * spec.cellHeight, spec.cellWidth, spec.cellHeight);
            if (clip.motionRegions == null)
            {
                g.DrawImage(atlas, bounds, source, GraphicsUnit.Pixel);
                return;
            }
            // Anchor the silhouette to frame zero. Generated pose-to-pose variation
            // cannot shake the head, feet or scarf outside the intended small areas.
            g.DrawImage(atlas, bounds, new Rectangle(0, source.Y, spec.cellWidth, spec.cellHeight), GraphicsUnit.Pixel);
            if (frame == 0) return;
            foreach (SpriteRegion region in clip.motionRegions)
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
        // Cache the full animation silhouette at its actual drawing size, so placing
        // a resting frame at an edge cannot crop a later pose or make it jitter.
        public Rectangle Bounds(Rectangle destination, int offset)
        {
            int kind = state == 3 && offset > 0 ? 1 : state;
            string key = kind + ":" + destination.Width + ":" + destination.Height;
            Rectangle result;
            if (!visibleBounds.TryGetValue(key, out result))
            {
                SpriteClip clip = ClipFor(offset);
                using (var frame = new Bitmap(destination.Width, destination.Height, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(frame))
                {
                    Rectangle full = new Rectangle(0, 0, frame.Width, frame.Height);
                    result = Rectangle.Empty;
                    for (int i = 0; i < clip.frames; i++)
                    {
                        g.Clear(Color.Transparent);
                        DrawFrame(g, full, clip, i);
                        Rectangle ink = MeasureVisibleBounds(frame);
                        if (!ink.IsEmpty) result = result.IsEmpty ? ink : Rectangle.Union(result, ink);
                    }
                }
                visibleBounds[key] = result;
            }
            result.Offset(destination.Location);
            return result;
        }

        internal static Rectangle MeasureVisibleBounds(Bitmap bitmap)
        {
            Rectangle full = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
                byte[] row = new byte[bitmap.Width * 4];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < bitmap.Width; x++)
                        if (row[x * 4 + 3] != 0)
                        {
                            left = Math.Min(left, x); top = Math.Min(top, y);
                            right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                        }
                }
                return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            }
            finally { bitmap.UnlockBits(data); }
        }

        public void Dispose() { atlas.Dispose(); }
    }
}
