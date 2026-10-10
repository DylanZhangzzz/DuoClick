# Rebuild the native icon from the same simple geometry as assets/logo.svg.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
public static class DuoIconGenerator {
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    static GraphicsPath Rounded(float x, float y, float w, float h, float r) {
        var p = new GraphicsPath(); float d = r * 2;
        p.AddArc(x,y,d,d,180,90); p.AddArc(x+w-d,y,d,d,270,90);
        p.AddArc(x+w-d,y+h-d,d,d,0,90); p.AddArc(x,y+h-d,d,d,90,90);
        p.CloseFigure(); return p;
    }
    static void Fill(Graphics g, Color c, GraphicsPath path) {
        using(path) using(var brush = new SolidBrush(c)) g.FillPath(brush,path);
    }
    public static Bitmap Draw(int size) {
        using(var big = new Bitmap(size*4,size*4,PixelFormat.Format32bppArgb)) {
            using(var g = Graphics.FromImage(big)) {
                g.SmoothingMode=SmoothingMode.AntiAlias;
                g.ScaleTransform(size*4/64f,size*4/64f);
                Color navy=Color.FromArgb(20,36,59);
                Fill(g,navy,Rounded(2,2,60,60,14));
                using(var pen=new Pen(Color.FromArgb(85,198,255),3)) {
                    pen.StartCap=pen.EndCap=LineCap.Round; pen.LineJoin=LineJoin.Round;
                    g.DrawLines(pen,new[]{new PointF(18,10),new PointF(10,10),new PointF(10,18)});
                    g.DrawLines(pen,new[]{new PointF(46,10),new PointF(54,10),new PointF(54,18)});
                    pen.Color=Color.FromArgb(85,224,196);
                    g.DrawLines(pen,new[]{new PointF(10,46),new PointF(10,54),new PointF(18,54)});
                    g.DrawLines(pen,new[]{new PointF(46,54),new PointF(54,54),new PointF(54,46)});
                }
                Fill(g,Color.FromArgb(245,249,255),Rounded(20,12,24,40,12));
                using(var body=Rounded(22,14,20,36,10)) {
                    var saved=g.Save(); g.SetClip(body);
                    using(var left=new SolidBrush(Color.FromArgb(49,157,236))) g.FillRectangle(left,22,14,8.5f,16);
                    using(var right=new SolidBrush(Color.FromArgb(22,191,165))) g.FillRectangle(right,33.5f,14,8.5f,16);
                    g.Restore(saved);
                }
                Fill(g,navy,Rounded(30.5f,20,3,9,1.5f));
            }
            var small=new Bitmap(size,size,PixelFormat.Format32bppArgb);
            using(var g=Graphics.FromImage(small)) {
                g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                g.DrawImage(big,new Rectangle(0,0,size,size));
            }
            return small;
        }
    }
    public static void Write(string destination,string preview) {
        int[] sizes={16,20,24,32,40,48,64,128,256};
        var payloads=new byte[sizes.Length][];
        for(int i=0;i<sizes.Length;i++) using(var bmp=Draw(sizes[i])) {
            // DIB icon payloads retain alpha and work with .NET Framework Icon.
            IntPtr h=bmp.GetHicon();
            try { using(var icon=Icon.FromHandle(h)) using(var memory=new MemoryStream()) {
                icon.Save(memory); byte[] single=memory.ToArray();
                int length=BitConverter.ToInt32(single,14), offset=BitConverter.ToInt32(single,18);
                payloads[i]=new byte[length]; Buffer.BlockCopy(single,offset,payloads[i],0,length);
            }} finally { DestroyIcon(h); }
        }
        using(var output=new BinaryWriter(File.Create(destination))) {
            output.Write((ushort)0); output.Write((ushort)1); output.Write((ushort)sizes.Length);
            int offset=6+16*sizes.Length;
            for(int i=0;i<sizes.Length;i++) {
                output.Write((byte)(sizes[i]==256?0:sizes[i])); output.Write((byte)(sizes[i]==256?0:sizes[i]));
                output.Write((byte)0); output.Write((byte)0); output.Write((ushort)1); output.Write((ushort)32);
                output.Write(payloads[i].Length); output.Write(offset); offset+=payloads[i].Length;
            }
            foreach(byte[] payload in payloads) output.Write(payload);
        }
        using(var bitmap=Draw(256)) bitmap.Save(preview,ImageFormat.Png);
    }
}
'@
$assetRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets'
[DuoIconGenerator]::Write((Join-Path $assetRoot 'duoclick.ico'), (Join-Path $assetRoot 'logo.png'))
Write-Host 'Generated assets/duoclick.ico and assets/logo.png'
