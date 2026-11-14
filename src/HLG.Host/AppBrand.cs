using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

static class AppBrand
{
    public static Icon Icon { get; } = CreateIcon();
    static Icon CreateIcon() {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new SolidBrush(ColorTranslator.FromHtml("#45ACD7"));
        using var shape = new GraphicsPath();
        const int radius = 8;
        shape.AddArc(1, 1, radius, radius, 180, 90); shape.AddArc(23, 1, radius, radius, 270, 90);
        shape.AddArc(23, 23, radius, radius, 0, 90); shape.AddArc(1, 23, radius, radius, 90, 90); shape.CloseFigure();
        graphics.FillPath(background, shape);
        using var pen = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawLine(pen, 16, 7, 16, 25); graphics.DrawLine(pen, 7, 16, 25, 16);
        graphics.DrawLine(pen, 10, 10, 22, 22); graphics.DrawLine(pen, 22, 10, 10, 22);
        var handle = bitmap.GetHicon();
        try { using var borrowed = System.Drawing.Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
}
