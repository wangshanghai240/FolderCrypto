# ===========================================================================
#  FolderCrypto - Regenerate context-menu / overlay icons for dark mode.
#
#  Problem:  the old overlay-lock.ico (dark gray) and unlock.ico (pure black)
#            are nearly invisible on dark Explorer context menus.
#  Fix:      re-render the padlock glyphs (Segoe MDL2 Assets) as WHITE fill +
#            dark outline, so they are visible on BOTH dark and light menus.
#
#  Outputs (multi-size 16/24/32/48/64/128/256, 32bpp ARGB):
#    FolderCrypto.ShellNative\overlay-lock.ico      (source, vcxproj copies to OutDir)
#    FolderCrypto.ShellNative\x64\Release\overlay-lock.ico
#    packages\overlay-lock.ico
#    packages\unlock.ico
#
#  Usage:  powershell -ExecutionPolicy Bypass -File .\gen-icons.ps1
# ===========================================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

Add-Type -AssemblyName System.Drawing
$iconCs = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class GlyphIcon
{
    private static readonly string[] FontCandidates =
        { "Segoe MDL2 Assets", "Segoe Fluent Icons", "Segoe UI Symbol" };

    private static FontFamily PickFamily()
    {
        foreach (var name in FontCandidates)
        {
            try { return new FontFamily(name); } catch { }
        }
        return FontFamily.GenericSansSerif;
    }

    /// <summary>Render a glyph as a white-fill + dark-outline bitmap.</summary>
    public static Bitmap Render(string text, int size, Color fill, Color outline, float outlinePx)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var path = new GraphicsPath();
            using (var family = PickFamily())
            {
                path.AddString(text, family, (int)FontStyle.Regular, size * 1.3f,
                    new PointF(0, 0), StringFormat.GenericDefault);
            }

            // Fit the glyph into the canvas with a margin.
            var bounds = path.GetBounds();
            float margin = size * 0.05f;
            float target = size - 2 * margin;
            float scale = target / Math.Max(bounds.Width, bounds.Height);
            var m = new Matrix();
            m.Translate(-bounds.X, -bounds.Y);
            m.Scale(scale, scale);
            m.Translate((size - bounds.Width * scale) / 2f, (size - bounds.Height * scale) / 2f);
            path.Transform(m);

            using (var outlinePen = new Pen(outline, outlinePx) { LineJoin = LineJoin.Round })
                g.DrawPath(outlinePen, path);
            using (var fillBrush = new SolidBrush(fill))
                g.FillPath(fillBrush, path);

            path.Dispose();
        }
        return bmp;
    }

    public static void SaveIco(string path, string text, int[] sizes,
        Color fill, Color outline, float outlineRatio)
    {
        var images = new List<Bitmap>();
        foreach (int s in sizes)
            images.Add(Render(text, s, fill, outline, Math.Max(1f, s * outlineRatio)));
        WriteIco(path, images);
        foreach (var b in images) b.Dispose();
    }

    private static void WriteIco(string path, List<Bitmap> images)
    {
        var entries = new List<byte[]>();
        var datas = new List<byte[]>();
        int offset = 6 + 16 * images.Count;
        foreach (var bmp in images)
        {
            var data = ToIcoData(bmp);
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((byte)(bmp.Width >= 256 ? 0 : bmp.Width));
                bw.Write((byte)(bmp.Height >= 256 ? 0 : bmp.Height));
                bw.Write((byte)0);          // color count
                bw.Write((byte)0);          // reserved
                bw.Write((ushort)1);        // planes
                bw.Write((ushort)32);       // bit count
                bw.Write((uint)data.Length);
                bw.Write((uint)offset);
                entries.Add(ms.ToArray());
            }
            datas.Add(data);
            offset += data.Length;
        }

        using (var fw = new BinaryWriter(File.Create(path)))
        {
            fw.Write((ushort)0);            // reserved
            fw.Write((ushort)1);            // type: icon
            fw.Write((ushort)images.Count);
            foreach (var e in entries) fw.Write(e);
            foreach (var d in datas) fw.Write(d);
        }
    }

    private static byte[] ToIcoData(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            // BITMAPINFOHEADER (40 bytes)
            bw.Write(40);
            bw.Write(w);
            bw.Write(h * 2);                // XOR + AND planes
            bw.Write((ushort)1);            // planes
            bw.Write((ushort)32);           // bit count
            bw.Write(0);                    // BI_RGB
            bw.Write(0);                    // size image
            bw.Write(0); bw.Write(0);
            bw.Write(0); bw.Write(0);

            var locked = bmp.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = locked.Stride;
                var row = new byte[w * 4];
                for (int y = h - 1; y >= 0; y--)
                {
                    Marshal.Copy(locked.Scan0 + y * stride, row, 0, row.Length);
                    bw.Write(row);
                }
            }
            finally { bmp.UnlockBits(locked); }

            // AND mask: all zeros (alpha channel carries transparency)
            int maskStride = ((w + 31) / 32) * 4;
            var zeros = new byte[maskStride];
            for (int y = 0; y < h; y++) bw.Write(zeros);

            return ms.ToArray();
        }
    }
}
'@

# Compile the icon generator, explicitly referencing System.Drawing.
Add-Type -TypeDefinition $iconCs -ReferencedAssemblies @(
    [System.Drawing.Graphics].Assembly.Location
)

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$white = [System.Drawing.Color]::White
$outline = [System.Drawing.Color]::FromArgb(255, 40, 40, 40)   # dark slate outline

# Locked padlock (encrypt / overlay) - E72E
$lockGlyph = ([char]0xE72E).ToString()
$lockTargets = @(
    'FolderCrypto.ShellNative\overlay-lock.ico',
    'FolderCrypto.ShellNative\x64\Release\overlay-lock.ico',
    'packages\overlay-lock.ico'
)
foreach ($t in $lockTargets) {
    [GlyphIcon]::SaveIco((Join-Path $root $t), $lockGlyph, $sizes, $white, $outline, 0.07)
    Write-Host "  lock  -> $t"
}

# Unlocked padlock (decrypt) - E785
$unlockGlyph = ([char]0xE785).ToString()
$unlockTargets = @(
    'FolderCrypto.ShellNative\unlock.ico',
    'packages\unlock.ico'
)
foreach ($t in $unlockTargets) {
    [GlyphIcon]::SaveIco((Join-Path $root $t), $unlockGlyph, $sizes, $white, $outline, 0.07)
    Write-Host "  unlock-> $t"
}

Write-Host "DONE: dark-mode icons regenerated."
