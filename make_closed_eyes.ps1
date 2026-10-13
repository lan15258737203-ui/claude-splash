param([string]$In, [string]$Out)
# Closed eyes for the blue-haired maid picture (1536x1024), v2:
# 1) cover each open eye with cheek skin (feathered, hair-safe sampling)
# 2) draw a thin brush-stroke lid curving down + fine lashes at the outer corner (like the reference frame from the video)
# (v2 moved the real lash band down - too heavy; user preferred the thin-line look)
# Geometry is in original pixels; the closed line sits on the lower eyelid so the opening slit starts right on it (see 眼睛.txt).
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
public static class Lids {
    public static void Hide(Bitmap bmp, Bitmap src, double cx, double cy, double rx, double ry, double rot, int sampleDy) {
        int x0 = (int)(cx - rx - 12), x1 = (int)(cx + rx + 12), y0 = (int)(cy - ry - 12), y1 = (int)(cy + ry + 12);
        double a = rot * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a);
        int rr = 0, rg = 0, rb = 0, rn = 0;
        int rcx = (int)(cx - (ry + sampleDy) * sa), rcy = (int)(cy + (ry + sampleDy) * ca);
        for (int oy = -4; oy <= 4; oy++) for (int ox = -10; ox <= 10; ox++) { Color c = src.GetPixel(rcx + ox, rcy + oy); rr += c.R; rg += c.G; rb += c.B; rn++; }
        rr /= rn; rg /= rn; rb /= rn;
        double refL = 0.299 * rr + 0.587 * rg + 0.114 * rb;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++) {
                double dx = x - cx, dy = y - cy;
                double u = (dx * ca + dy * sa) / rx, v = (-dx * sa + dy * ca) / ry;
                double d = Math.Sqrt(u * u + v * v);
                if (d > 1.15) continue;
                double alpha = d < 0.85 ? 1.0 : (1.15 - d) / 0.30;
                double lx = u * rx, sy = ry + sampleDy;
                int sxp = (int)(cx + lx * ca - sy * sa), syp = (int)(cy + lx * sa + sy * ca);
                int r = 0, g = 0, b = 0, n = 0;
                for (int oy = -3; oy <= 3; oy++) for (int ox = -3; ox <= 3; ox++) {
                    Color c = src.GetPixel(Math.Max(0, Math.Min(src.Width - 1, sxp + ox)), Math.Max(0, Math.Min(src.Height - 1, syp + oy)));
                    r += c.R; g += c.G; b += c.B; n++;
                }
                r /= n; g /= n; b /= n;
                double sl = 0.299 * r + 0.587 * g + 0.114 * b;
                if (sl < refL * 0.85) { double k = Math.Min(1, (refL * 0.85 - sl) / (refL * 0.25)); r = (int)(r * (1 - k) + rr * k); g = (int)(g * (1 - k) + rg * k); b = (int)(b * (1 - k) + rb * k); }
                // eyelid shading: slightly darker toward the bottom (where the lashes will sit), lighter at the top of the lid
                double shade = 0.97 - 0.07 * Math.Max(0, Math.Min(1, (v + 0.2) / 1.0));
                Color o = bmp.GetPixel(x, y);
                bmp.SetPixel(x, y, Color.FromArgb(Clamp(o.R * (1 - alpha) + r * shade * alpha), Clamp(o.G * (1 - alpha) + g * shade * alpha), Clamp(o.B * (1 - alpha) + b * shade * alpha)));
            }
    }

    static int Clamp(double v) { return v < 0 ? 0 : v > 255 ? 255 : (int)v; }

    static PointF Q(PointF a, PointF c, PointF b, double t) {
        double u = 1 - t;
        return new PointF((float)(u * u * a.X + 2 * u * t * c.X + t * t * b.X), (float)(u * u * a.Y + 2 * u * t * c.Y + t * t * b.Y));
    }
    static PointF Ctrl(PointF a, PointF mid, PointF b) { return new PointF(2 * mid.X - (a.X + b.X) / 2, 2 * mid.Y - (a.Y + b.Y) / 2); }

    // Take the lash band along the source curve (outer->top->inner) and lay it, flipped, along the destination curve.
    public static void MoveLashes(Bitmap dst, Bitmap src, PointF sO, PointF sT, PointF sI, PointF dO, PointF dB, PointF dI, double below, double above, double squash) {
        PointF sc = Ctrl(sO, sT, sI), dc = Ctrl(dO, dB, dI);
        int W = dst.Width, H = dst.Height;
        var bestA = new double[W * H];
        var bestC = new Color[W * H];
        double len = 0; PointF prev = sO;
        for (int i = 1; i <= 50; i++) { PointF p = Q(sO, sc, sI, i / 50.0); len += Math.Sqrt((p.X - prev.X) * (p.X - prev.X) + (p.Y - prev.Y) * (p.Y - prev.Y)); prev = p; }
        int steps = (int)(len * 3);
        for (int i = 0; i <= steps; i++) {
            double t = i / (double)steps;
            PointF s0 = Q(sO, sc, sI, t), s1 = Q(sO, sc, sI, Math.Min(1, t + 0.002));
            PointF d0 = Q(dO, dc, dI, t), d1 = Q(dO, dc, dI, Math.Min(1, t + 0.002));
            if (t >= 0.998) { s1 = s0; s0 = Q(sO, sc, sI, t - 0.002); d1 = d0; d0 = Q(dO, dc, dI, t - 0.002); }
            double sx = s1.X - s0.X, sy = s1.Y - s0.Y, sl = Math.Sqrt(sx * sx + sy * sy); sx /= sl; sy /= sl;
            double nux = sy, nuy = -sx; if (nuy > 0) { nux = -nux; nuy = -nuy; }        // source normal pointing up (out of the eye)
            double dx = d1.X - d0.X, dy = d1.Y - d0.Y, dl = Math.Sqrt(dx * dx + dy * dy); dx /= dl; dy /= dl;
            double ndx = -dy, ndy = dx; if (ndy < 0) { ndx = -ndx; ndy = -ndy; }       // destination normal pointing down
            PointF sp = Q(sO, sc, sI, t), dp = Q(dO, dc, dI, t);
            double endFade = Math.Min(1, Math.Min(t, 1 - t) / 0.06);
            for (double d = -below; d <= above; d += 0.35) {
                double fx = sp.X + nux * d, fy = sp.Y + nuy * d;
                Color c = Bilinear(src, fx, fy);
                double L = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                double a = Math.Max(0, Math.Min(1, (0.40 - L) / 0.20)) * endFade;   // only the dark lash "ink" moves
                if (a <= 0.01) continue;
                double dd = d * squash;
                double txf = dp.X + ndx * dd, tyf = dp.Y + ndy * dd;
                int bx = (int)Math.Floor(txf), by = (int)Math.Floor(tyf);
                double ffx = txf - bx, ffy = tyf - by;
                // spread over the 4 neighbouring pixels (bilinear) so the moved lashes stay soft like the original art
                for (int q = 0; q < 4; q++) {
                    int tx = bx + (q & 1), ty = by + (q >> 1);
                    if (tx < 0 || ty < 0 || tx >= W || ty >= H) continue;
                    double wgt = ((q & 1) == 1 ? ffx : 1 - ffx) * ((q >> 1) == 1 ? ffy : 1 - ffy);
                    double aw = a * Math.Min(1, wgt * 1.8);
                    int k = ty * W + tx;
                    if (aw > bestA[k]) { bestA[k] = aw; bestC[k] = c; }
                }
            }
        }
        for (int k = 0; k < bestA.Length; k++) {
            if (bestA[k] <= 0) continue;
            int x = k % W, y = k / W;
            double a = bestA[k] * 0.96;
            Color o = dst.GetPixel(x, y), c = bestC[k];
            dst.SetPixel(x, y, Color.FromArgb(Clamp(o.R * (1 - a) + c.R * a), Clamp(o.G * (1 - a) + c.G * a), Clamp(o.B * (1 - a) + c.B * a)));
        }
    }

    static Color Bilinear(Bitmap b, double x, double y) {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        Color c00 = b.GetPixel(x0, y0), c10 = b.GetPixel(x0 + 1, y0), c01 = b.GetPixel(x0, y0 + 1), c11 = b.GetPixel(x0 + 1, y0 + 1);
        Func<int, int, int, int, int> mix = (p, q, r, s) => (int)((p * (1 - fx) + q * fx) * (1 - fy) + (r * (1 - fx) + s * fx) * fy);
        return Color.FromArgb(mix(c00.R, c10.R, c01.R, c11.R), mix(c00.G, c10.G, c01.G, c11.G), mix(c00.B, c10.B, c01.B, c11.B));
    }

    // closed eye like the reference: a thin brush stroke curving down (pointed ends, a bit fuller toward the outer corner)
    // plus a few fine tapered lashes hanging down-and-out at the outer corner; drawn on a layer, softened, then composited
    public static void ClosedLine(Bitmap bmp, PointF O, PointF B, PointF I, float maxW, int lashes, float lashLen) {
        PointF c = Ctrl(O, B, I);
        float outer = O.X < I.X ? -1 : 1;
        int x0 = (int)Math.Min(O.X, I.X) - 40, y0 = (int)Math.Min(Math.Min(O.Y, I.Y), B.Y) - 30;
        int w = (int)Math.Abs(O.X - I.X) + 80, h = 90;
        var layer = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var ink = Color.FromArgb(240, 30, 22, 50);
        using (var g = Graphics.FromImage(layer)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(-x0, -y0);
            int N = 48;
            var top = new PointF[N]; var bot = new PointF[N]; var mid = new PointF[N]; var nrm = new PointF[N];
            for (int i = 0; i < N; i++) {
                double t = i / (double)(N - 1);
                PointF p = Q(O, c, I, t), p2 = Q(O, c, I, Math.Min(1, t + 0.01)), p1 = Q(O, c, I, Math.Max(0, t - 0.01));
                double tx = p2.X - p1.X, ty = p2.Y - p1.Y, tl = Math.Sqrt(tx * tx + ty * ty);
                double nx = -ty / tl, ny = tx / tl; if (ny < 0) { nx = -nx; ny = -ny; }          // normal pointing down
                double wt = maxW * Math.Pow(Math.Sin(Math.PI * t), 0.6) * (1.15 - 0.35 * t);   // pointed ends, fuller on the outer side
                mid[i] = p; nrm[i] = new PointF((float)nx, (float)ny);
                top[i] = new PointF((float)(p.X - nx * wt / 2), (float)(p.Y - ny * wt / 2));
                bot[N - 1 - i] = new PointF((float)(p.X + nx * wt / 2), (float)(p.Y + ny * wt / 2));
            }
            var poly = new PointF[2 * N]; Array.Copy(top, poly, N); Array.Copy(bot, 0, poly, N, N);
            using (var br = new SolidBrush(ink)) g.FillPolygon(br, poly);
            // lashes in the outer ~40%, each a curved taper
            for (int j = 0; j < lashes; j++) {
                double t = 0.05 + j * (0.36 / Math.Max(1, lashes - 1));
                int i = (int)(t * (N - 1));
                PointF bse = mid[i]; PointF n = nrm[i];
                float L = lashLen * (float)(1.0 - 0.12 * j);
                var tip = new PointF(bse.X + outer * L * 0.75f + n.X * L * 0.5f, bse.Y + n.Y * L * 0.75f);
                var ctl = new PointF(bse.X + n.X * L * 0.55f + outer * L * 0.15f, bse.Y + n.Y * L * 0.55f);
                int M = 14; var side1 = new PointF[M]; var side2 = new PointF[M];
                for (int k = 0; k < M; k++) {
                    double u = k / (double)(M - 1);
                    PointF q = Q(bse, ctl, tip, u), q2 = Q(bse, ctl, tip, Math.Min(1, u + 0.02)), q1 = Q(bse, ctl, tip, Math.Max(0, u - 0.02));
                    double dx = q2.X - q1.X, dy = q2.Y - q1.Y, dl = Math.Sqrt(dx * dx + dy * dy);
                    double px = -dy / dl, py = dx / dl, ww = 1.15 * (1 - u);                     // tapers to a point
                    side1[k] = new PointF((float)(q.X + px * ww), (float)(q.Y + py * ww));
                    side2[M - 1 - k] = new PointF((float)(q.X - px * ww), (float)(q.Y - py * ww));
                }
                var lp = new PointF[2 * M]; Array.Copy(side1, lp, M); Array.Copy(side2, 0, lp, M, M);
                using (var br = new SolidBrush(ink)) g.FillPolygon(br, lp);
            }
        }
        // soften like painted line art (one light box blur on the layer), then composite
        var soft = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++) {
                int sa = 0; int cw = 0;
                for (int oy = -1; oy <= 1; oy++) for (int ox = -1; ox <= 1; ox++) { int k = (ox == 0 && oy == 0) ? 4 : 1; sa += layer.GetPixel(x + ox, y + oy).A * k; cw += k; }
                soft.SetPixel(x, y, Color.FromArgb(sa / cw, ink.R, ink.G, ink.B));
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                int bx = x + x0, by = y + y0;
                if (bx < 0 || by < 0 || bx >= bmp.Width || by >= bmp.Height) continue;
                double a = soft.GetPixel(x, y).A / 255.0;
                if (a <= 0) continue;
                Color o = bmp.GetPixel(bx, by);
                bmp.SetPixel(bx, by, Color.FromArgb(Clamp(o.R * (1 - a) + ink.R * a), Clamp(o.G * (1 - a) + ink.G * a), Clamp(o.B * (1 - a) + ink.B * a)));
            }
        layer.Dispose(); soft.Dispose();
    }}
"@
function P($x, $y) { New-Object System.Drawing.PointF $x, $y }
$orig = New-Object System.Drawing.Bitmap $In
$src = New-Object System.Drawing.Bitmap $orig.Width, $orig.Height, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g = [System.Drawing.Graphics]::FromImage($src); $g.DrawImage($orig, 0, 0, $orig.Width, $orig.Height); $g.Dispose(); $orig.Dispose()
$dst = $src.Clone()

# viewer-left eye: hide, then move its own upper lash band (outer wing -> top -> inner) onto the lower-lid curve
[Lids]::Hide($dst, $src, 968, 437, 63, 34, -6, 12)
[Lids]::ClosedLine($dst, (P 924 445), (P 972 461), (P 1020 443), 4.6, 6, 15)

# viewer-right eye
[Lids]::Hide($dst, $src, 1191, 369, 56, 33, -12, 13)
[Lids]::ClosedLine($dst, (P 1232 373), (P 1189 393), (P 1151 381), 4.3, 6, 14)

$dst.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$dst.Dispose(); $src.Dispose()
"saved $Out"
