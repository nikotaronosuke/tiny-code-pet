param([Parameter(Mandatory=$true)][string]$Source, [string]$Output)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
public static class NinjaAtlasPreparation {
    public static void Run(string source, string target) {
        using (Bitmap input = new Bitmap(source))
        using (Bitmap clean = new Bitmap(input.Width, input.Height, PixelFormat.Format32bppArgb)) {
            for (int y=0; y<input.Height; y++) for (int x=0; x<input.Width; x++) {
                Color c=input.GetPixel(x,y);
                // Magenta is absent from the character palette. Unmix edge coverage
                // against its dark outlines; clear hidden RGB outside the silhouette.
                int key=Math.Max(0, Math.Min(c.R,c.B)-c.G);
                int alpha=255-key;
                if (key>230) { clean.SetPixel(x,y,Color.Transparent); continue; }
                if (key>12) {
                    // Edge pixels touch the ninja's dark outline. Extending that
                    // outline color prevents blue/purple fringes after key removal.
                    clean.SetPixel(x,y,Color.FromArgb(alpha,16,25,43));
                } else clean.SetPixel(x,y,c);
            }
            using(Bitmap output=new Bitmap(1024,384,PixelFormat.Format32bppArgb)) {
                using(Graphics g=Graphics.FromImage(output)) {
                    g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    g.DrawImage(clean,new Rectangle(0,0,1024,384));
                }
                output.Save(target,ImageFormat.Png);
            }
        }
    }
}
'@
$target = if ($Output) { $Output } else { Join-Path $PSScriptRoot '..\assets\ninja\ninja.png' }
if (Test-Path -LiteralPath $target) { throw 'Target exists; inspect before replacing.' }
[NinjaAtlasPreparation]::Run($Source, $target)
Write-Output 'Ninja atlas prepared: 1024x384, 24 frames.'
