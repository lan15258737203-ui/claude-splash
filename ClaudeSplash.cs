// Claude 启动动画：照抖音「Codex 启动动画」的效果——左边打代码，右边霓虹线条描出图案→上色→亮起，最后淡出露出 Claude
// 用法：ClaudeSplash.exe              正常启动（同时打开 Claude）
//       ClaudeSplash.exe --preview    只播动画、不打开 Claude
//       ClaudeSplash.exe --snap 目录   不弹窗，把几个时间点的画面存成 PNG（自检用）
//       ClaudeSplash.exe --wallpaper  不播动画，只给 Claude 窗口贴壁纸
// 测试：环境变量 CLAUDESPLASH_TEST=1（不拦键盘、播完自己退）、CLAUDESPLASH_FPS=文件（记帧率，另外单算睁眼+光晕那一段）、
//       CLAUDESPLASH_FROM=5 + CLAUDESPLASH_TO=9.4（只播这几秒）、CLAUDESPLASH_NOWAVE=1（关掉扩散的光晕，对比用）
// 换图：把图片放到 exe 旁边，命名「图片.png」或「图片.jpg」；想要「睁眼」那一下，再放一张同尺寸的「图片2.png」
// 壁纸：有「图片.png」时，动画播完图不走，压暗后贴在 Claude 窗口上（鼠标能点穿），跟着窗口移动；右下角托盘图标可调浓淡/隐藏/关掉
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;
using WF = System.Windows.Forms;

static class Program
{
    public const string AppId = "Claude_pzs8sxrjxfjjc!Claude";

    [STAThread]
    static void Main(string[] args)
    {
        bool preview = false, wallpaperOnly = false;
        string snapDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--preview") preview = true;
            else if (args[i] == "--wallpaper") wallpaperOnly = true;
            else if (args[i] == "--snap" && i + 1 < args.Length) snapDir = args[++i];
        }
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        if (snapDir != null) { Snap.Run(dir, snapDir); return; }

        bool first;
        using (var mutex = new Mutex(true, "ClaudeSplash.SingleInstance", out first))
        {
            if (!preview) LaunchClaude();   // 先叫 Claude 起来，动画和它同时加载
            if (!first) return;             // 已经在跑（动画或壁纸）：只叫 Claude 出来，不再叠一层
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;   // 动画关了壁纸还要接着跑
            app.Startup += delegate
            {
                if (wallpaperOnly) { if (!Wallpaper.Start(dir, 0.8)) app.Shutdown(); }
                else new Splash(dir, preview).Show();
            };
            app.Run();
        }
    }

    static void LaunchClaude()
    {
        try
        {
            var psi = new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + AppId);
            psi.UseShellExecute = true;
            Process.Start(psi);
        }
        catch { }
    }
}

// ───────────────────────── 窗口：放在 Claude 窗口将要出现的位置，播完停在最后一幕，按任意键（或点一下）才淡出进入 ─────────────────────────
class Splash : Window
{
    [DllImport("user32")] static extern bool IsWindowVisible(IntPtr h);
    delegate IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32")] static extern IntPtr SetWindowsHookEx(int id, KeyboardProc fn, IntPtr mod, uint thread);
    [DllImport("user32")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    readonly Scene scene;
    readonly bool preview;
    readonly string dir;
    readonly Stopwatch clock = new Stopwatch();
    double clockOffset;   // 测试用：从动画的第几秒开始播（CLAUDESPLASH_FROM）
    double Now { get { return clock.Elapsed.TotalSeconds + clockOffset; } }
    readonly DispatcherTimer poll = new DispatcherTimer();
    bool ready, exiting, closed, wallpaperOn, wantEnter;
    double exitAt;
    double lastIdle;
    // 监听全局按键：Claude 打开后会抢走键盘焦点，动画窗口自己收不到按键；动画显示期间按的键只用来「进入」，不会打进 Claude
    readonly KeyboardProc keyProc;
    IntPtr keyHook;

    public Splash(string dir, bool preview)
    {
        this.preview = preview;
        this.dir = dir;
        Rect r = Placement();
        Left = r.X; Top = r.Y; Width = r.Width; Height = r.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;      // 淡出时要透出后面的 Claude
        Background = Brushes.Transparent;
        Opacity = 0;
        Title = "Claude";

        scene = new Scene(r.Width, r.Height, Art.Load(dir));
        Content = scene.Root;
        scene.Update(0, false);

        MouseLeftButtonDown += delegate { RequestEnter(); };   // 点一下也能进入（动画没播完就是跳过）
        KeyDown += delegate { RequestEnter(); };
        keyProc = OnKeyHook;


        poll.Interval = TimeSpan.FromMilliseconds(300);
        poll.Tick += delegate
        {
            if (!ready) ready = preview ? clock.Elapsed.TotalSeconds > 3.5 : ClaudeVisible();
        };
        Loaded += delegate
        {
            clock.Start();
            poll.Start();
            CompositionTarget.Rendering += OnFrame;
            // 测试用：设了环境变量 CLAUDESPLASH_TEST 就不拦键盘（免得测试时吃掉别人正在打的字），播完 1 秒后自己进入
            if (Environment.GetEnvironmentVariable("CLAUDESPLASH_TEST") != null)
            {
                // 再设 CLAUDESPLASH_FROM=5（配 CLAUDESPLASH_TO=9.5）：只播这几秒就退出，量睁眼那一段的帧率用，少占屏幕
                double from, to = Scene.MinEnd + 1;
                if (double.TryParse(Environment.GetEnvironmentVariable("CLAUDESPLASH_FROM"), NumberStyles.Float, CultureInfo.InvariantCulture, out from) && from > 0)
                {
                    clockOffset = from;
                    double.TryParse(Environment.GetEnvironmentVariable("CLAUDESPLASH_TO"), NumberStyles.Float, CultureInfo.InvariantCulture, out to);
                    if (to <= from) to = from + 4;
                    ready = true;
                }
                var auto = new DispatcherTimer { Interval = TimeSpan.FromSeconds(to - clockOffset) };
                auto.Tick += delegate { auto.Stop(); RequestEnter(); };
                auto.Start();
            }
            else keyHook = SetWindowsHookEx(13, keyProc, GetModuleHandle(null), 0);   // 13 = 低级键盘钩子
        };
    }

    void OnFrame(object sender, EventArgs e)
    {
        if (closed) return;
        double t = Now;
        // 不再自动退出：按了键才进入；按的时候 Claude 还没出来，就先显示「正在唤醒」，等它出来（最多等到 25 秒）
        if (!exiting && wantEnter && (ready || t >= Scene.MaxWait)) BeginExit();
        // 动画播完停在最后一幕：全屏透明窗口每画一帧都要整屏重新合成，很吃 CPU。
        // 这时改成每 0.15 秒才更新一次（只剩提示字在呼吸），等按了键再恢复每帧刷新做淡出
        if (!exiting && !wantEnter && t > Scene.MinEnd + 0.5)
        {
            if (t - lastIdle < 0.15) return;
            lastIdle = t;
        }
        scene.Update(t, wantEnter && !ready);

        double alpha = Scene.Ramp(t, 0, 0.35);
        if (exiting)
        {
            double k = (t - exitAt) / Scene.ExitSeconds;
            if (k >= 1)
            {
                closed = true;
                CompositionTarget.Rendering -= OnFrame;
                poll.Stop();
                Close();
                if (!wallpaperOn) Application.Current.Shutdown();
                return;
            }
            alpha *= 1 - k * k * (3 - 2 * k);
        }
        Opacity = alpha;
        if (frames != null) frames.Add(t);
    }

    // 设了环境变量 CLAUDESPLASH_FPS=文件路径 时，退出前把帧率写进去（测卡不卡用）
    readonly List<double> frames = Environment.GetEnvironmentVariable("CLAUDESPLASH_FPS") != null ? new List<double>() : null;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Unhook();
        if (frames == null || frames.Count < 3) return;
        var gaps = new List<double>();
        for (int i = 1; i < frames.Count; i++) gaps.Add(frames[i] - frames[i - 1]);
        gaps.Sort();
        // 睁眼 + 光晕扩散那一段单独再算一遍（这段叠的图层最多）
        int eyeN = 0; double eyeMax = 0;
        for (int i = 1; i < frames.Count; i++)
            if (frames[i - 1] >= Scene.EyeOpenStart && frames[i] <= Scene.EyeOpenEnd + 1.8) { eyeN++; eyeMax = Math.Max(eyeMax, frames[i] - frames[i - 1]); }
        File.WriteAllText(Environment.GetEnvironmentVariable("CLAUDESPLASH_FPS"), string.Format(CultureInfo.InvariantCulture,
            "frames={0} avgfps={1:0.0} median_ms={2:0.0} p95_ms={3:0.0} max_ms={4:0.0} | eye+halo: avgfps={5:0.0} max_ms={6:0.0}",
            frames.Count, (frames.Count - 1) / (frames[frames.Count - 1] - frames[0]), gaps[gaps.Count / 2] * 1000, gaps[(int)(gaps.Count * 0.95)] * 1000, gaps[gaps.Count - 1] * 1000,
            eyeN / (Scene.EyeOpenEnd + 1.8 - Scene.EyeOpenStart), eyeMax * 1000));
    }

    void RequestEnter()
    {
        wantEnter = true;
    }

    IntPtr OnKeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        int msg = wParam.ToInt32();
        // 按下（含 Alt 组合）；刚启动的头 1 秒不算（实测偶尔会收到一个启动瞬间的退格键）
        if (code >= 0 && !exiting && !closed && (msg == 0x100 || msg == 0x104) && clock.Elapsed.TotalSeconds > 1.0)
        {
            string log = Environment.GetEnvironmentVariable("CLAUDESPLASH_KEYLOG");
            if (log != null) try { File.AppendAllText(log, string.Format(CultureInfo.InvariantCulture, "{0:0.00}s vk=0x{1:X2} flags=0x{2:X}\r\n", clock.Elapsed.TotalSeconds, Marshal.ReadInt32(lParam), Marshal.ReadInt32(lParam, 8))); } catch { }
            Dispatcher.BeginInvoke(new Action(RequestEnter));
            return (IntPtr)1;   // 吞掉这个键
        }
        return CallNextHookEx(keyHook, code, wParam, lParam);
    }

    void Unhook()
    {
        if (keyHook != IntPtr.Zero) { UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
    }

    void BeginExit()
    {
        if (exiting) return;
        exiting = true;
        Unhook();   // 开始淡出就不再拦按键
        exitAt = Now;
        // 壁纸默认关（用户 10-12 说不要）；「设置.txt」里写「壁纸=开」才在动画淡出的同时淡入
        if (!preview && Settings.WallpaperOn(dir)) wallpaperOn = Wallpaper.Start(dir, 0.9);
    }

    static bool ClaudeVisible()
    {
        foreach (var p in Process.GetProcessesByName("claude"))
        {
            try
            {
                IntPtr h = p.MainWindowHandle;
                if (h != IntPtr.Zero && IsWindowVisible(h)) return true;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return false;
    }

    // Claude 上次是最大化就铺满工作区，不然就照它上次的位置和大小
    static Rect Placement()
    {
        Rect work = SystemParameters.WorkArea;
        string[] files =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Packages\\Claude_pzs8sxrjxfjjc\\LocalCache\\Roaming\\Claude\\window-state.json",
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\Claude\\window-state.json",
        };
        foreach (string f in files)
        {
            try
            {
                if (!File.Exists(f)) continue;
                string s = File.ReadAllText(f);
                if (Regex.IsMatch(s, "\"isMaximized\"\\s*:\\s*true")) return work;
                double x = Num(s, "x"), y = Num(s, "y"), w = Num(s, "width"), h = Num(s, "height");
                if (w >= 300 && h >= 300) return new Rect(x, y, w, h);
            }
            catch { }
        }
        return work;
    }

    static double Num(string s, string key)
    {
        var m = Regex.Match(s, "\"" + key + "\"\\s*:\\s*(-?[0-9.]+)");
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }
}

// ───────────────────────── 不弹窗，按时间点出几张图，自检用 ─────────────────────────
static class Snap
{
    public static void Run(string dir, string outDir)
    {
        Directory.CreateDirectory(outDir);
        double W = 1707, H = 912;
        var scene = new Scene(W, H, Art.Load(dir));
        double[] ts = { 5.5, 5.85, 6.1, 6.35, 6.6, 6.85, 7.1, 7.35, 7.42, 7.5, 7.65, 7.8, 8.0, 8.3, 8.6, 9.3, 10.5 };
        foreach (double t in ts)
        {
            scene.Update(t, false);
            scene.Root.Measure(new Size(W, H));
            scene.Root.Arrange(new Rect(0, 0, W, H));
            scene.Root.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)W, (int)H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(scene.Root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(System.IO.Path.Combine(outDir, string.Format(CultureInfo.InvariantCulture, "t{0:00.00}.png", t))))
                enc.Save(fs);
        }
    }
}

// ───────────────────────── 画面：全部由时间 t 算出来，同一个 t 永远是同一帧 ─────────────────────────
// 节奏照抖音原视频（窗口出现算 0 秒）：0.8 秒起从上往下描霓虹线稿、脸上浮出三角网格 → 2.4 秒起人物慢慢透出来
// → 4.8 秒全彩（闭着眼；上色的同时把「人物蒙版.png」里人物以外的地方压暗，流星/光弧的光才显眼，人物亮度不变）
// → 5.6~7.35 秒睁眼（1.75 秒，「睁眼帧」13 个开合程度连着过渡；眼睛的光跟着睁开一路变亮）
// → 全开那一下两眼亮起蓝白光（眼珠提亮 + 光晕/光芯/横光），同时从眼睛放出一道光晕沿着人物向四周扩散（约 1 秒扫完）
// → 8.0 秒代码淡出 → 8.6 秒大标题「Claude」+「欢迎主人回家」→ 停住等按任意键，淡出露出 Claude；线条和粒子只用蓝、紫、黄
class Scene
{
    // 时间轴（秒）
    public const double TypeStart = 0.9, CharsPerSec = 170;
    public const double TraceStart = 0.8, TraceEnd = 3.6;
    public const double FillStart = 2.4, FillEnd = 4.8;
    public const double EyeLight = 4.5;   // 「眼睛先亮」模式：眼睛开始亮起的时刻
    // 「睁眼帧」模式：5.6 → 7.35，一口气连贯睁开共 1.75 秒；起步、停住都柔，中间匀速
    public const double EyeOpenStart = 5.6, EyeOpenEnd = 7.35;
    const double EyeMidSlow = 0.3;    // 半睁处放慢多少（0 = 不放慢、中间最快；0.3 = 中间一段匀速；越大越慢，小于 1 就不会停住）
    const int FineFrames = 8;       // 睁眼帧有这么多张以上 = 每张只差一点点，直接相邻两张淡入；张数少才用「从下往上擦」
    const double WipeBand = 0.45;   // 擦出来的交界柔边，占眼睛框高度的比例
    public const double WakeStart = 6.6, WakeEnd = 7.8;                 // 脸上亮一下（有闭眼/睁眼两张图时是睁开的时刻）
    public const double EyeStart = 5.2, EyeHalf = 5.8, EyeHoldEnd = 6.1;   // 逐渐睁眼（只有放了 图片2.png + 眼睛.txt 才用；用户 10-12 说不要了）
    public const double OutroStart = 8.0;                       // 代码开始淡出（1.4 秒淡完）
    public const double TitleStart = 8.6, SubStart = 9.1;       // 大标题「Claude」、副标题「欢迎主人回家」
    public const double MinEnd = 11.6, MaxWait = 25, ExitSeconds = 0.9;
    const string TitleText = "Claude", SubText = "欢 迎 主 人 回 家";

    static readonly string[] Lines =
    {
        "// 深海回声 · 意识苏醒",
        "const tide = abyss('memory');",
        "await wake(tide); // cortex lattice online",
        "const pulse = sync('neural');",
        "pulse.rate = 60; // align photon clock",
        "// 光子汇聚 · 信号接入",
        "const photons = gather('violet');",
        "photons.disperse(0.72);",
        "await photons.focus(); // spectrum locked",
        "// 发丝勾勒 · 轮廓重塑",
        "const strands = weave('hair');",
        "await strands.trace(); // luminous filaments rebuilt",
        "strands.edge = 0.92;",
        "strands.tint = ['azure', 'violet', 'gold'];",
        "// 接入深空 · 建立连接",
        "const bridge = connect('deep-space');",
        "await bridge.listen();",
        "bridge.gain = 1.0;",
        "// 解码记忆 · 重构面容",
        "const visage = await decode(tide); // rebuild topology",
        "visage.mesh({ density: 0.96 });",
        "await visage.resolve();",
        "// 勾勒轮廓 · 保留神韵",
        "const outline = trace('silhouette');",
        "outline.smooth(0.88); // keep the soul intact",
        "await outline.reveal();",
        "// 星尘拖尾 · 漫天流光",
        "const trails = await stardust(128);",
        "trails.velocity = 0.6;",
        "trails.palette = strands.tint;",
        "await trails.scatter();",
        "// 注入色彩 · 点亮画面",
        "const palette = sample(visage, 'night');",
        "await visage.fill(palette); // chroma restored",
        "visage.depth = 0.88;",
        "// 瞳孔点亮 · 光谱同步",
        "const iris = visage.focus('eyes');",
        "await iris.open(0.86); // iris spectrum synchronized",
        "iris.glow = 'azure';",
        "// 唤醒完成 · 连接到你",
        "await handshake('you'); // 200 OK",
        "await claude.launch(); // 欢迎回来",
    };

    public readonly Grid Root = new Grid();
    readonly double W, H;
    readonly Art art;
    readonly EdgeLayer edges;
    readonly Canvas canvas = new Canvas();

    readonly Grid artBox = new Grid();
    readonly ScaleTransform artScale = new ScaleTransform();
    readonly Image edgeImg = new Image(), glowImg = new Image(), fillImg = new Image(), fill2Img;
    readonly Path mesh;
    readonly Ellipse[] eyeGlows = new Ellipse[0];
    readonly Ellipse[] eyeCores = new Ellipse[0];   // 睁眼帧模式：完全睁开时眼珠上的亮光芯
    readonly Ellipse[] eyeFlares = new Ellipse[0];  // 睁眼帧模式：全开那一下横着划过眼睛的一道亮光
    readonly Ellipse[] eyeBlooms = new Ellipse[0];  // 睁眼帧模式：眼睛的光洒到脸上的一大圈淡蓝
    readonly ScaleTransform eyeHaloScale = new ScaleTransform(), eyeFlareScale = new ScaleTransform();
    // 压暗：上色后把人物以外的地方压暗，流星、光弧、线条的光才显得亮；人物保持原来的亮度。
    // DimBack = 有「人物蒙版.png」时人物以外压暗多少（0 = 不压，1 = 全黑）
    // 没有蒙版（换了别的图）才用下面两个：整张图压暗，DimBase = 脸上压多少，DimEdge = 到画面边上再多压多少，两只眼睛处留洞
    const double DimBack = 0.72, DimBase = 0.34, DimEdge = 0.30;
    readonly Image dimImg, dimEyesImg;   // 压暗层；（没有蒙版时）睁眼前把眼睛处的洞补平的那一块，睁开时撤掉
    readonly Image eyeBrightImg;         // 睁开后把眼珠本身提亮的那层（花纹还在，不是糊一团光）
    // 睁眼过程中眼睛的光跟着眼缝走：先是贴着闭眼线的一道扁光，随着睁开变高、变亮、升到眼珠正中
    readonly ScaleTransform eyeCoreScale = new ScaleTransform(), eyeBloomScale = new ScaleTransform();
    readonly TranslateTransform eyeDrop = new TranslateTransform();
    double eyeDropPx;                    // 闭眼线比眼珠中心低多少（artBox 里的长度）
    // 完全睁开那一刻：从两眼之间放出一圈光晕，沿着人物向四周扩散开（先快后慢，边扩边淡）
    const double WaveDelay = 0.02, WaveDur = 1.7;
    const double WaveOutside = 0.07;     // 光晕在人物以外还剩多少（0 = 只照人物）
    // 光晕 = 一组图层（被照亮的人物 + 亮起来的霓虹线稿），整组只露出「光带」那一圈；光带的半径每帧变大 = 往外扩散
    // （三层各套一个遮罩的话每帧要多画三张整屏的中间图，帧率掉到 45；合成一组只要一张）
    readonly Grid waveBox;
    static readonly bool DbgNoWave = Environment.GetEnvironmentVariable("CLAUDESPLASH_NOWAVE") != null;   // 测试用：关掉光晕，对比帧率
    readonly RadialGradientBrush waveBand;   // 「只露出光带那一圈」的遮罩
    double waveR0, waveR1;               // 光带半径从 R0 扩到 R1（artBox 里的长度）
    bool edgesFrozen, arcsWereOn = true;
    readonly LidAnim[] lidAnims;
    readonly GeometryDrawing apertureDrawing;
    readonly Image fill2Full;
    readonly Image eyeColorImg;   // 「眼睛先亮」模式：只在眼睛处提前露出的彩色图
    readonly Image[] eyeFrameImgs; // 「睁眼帧」模式：一张张叠上去的半睁/睁开图
    readonly GradientStop[] wipeSolid, wipeEdge;   // 每张睁眼帧的「从下往上擦出来」遮罩：实心边、柔边的位置
    readonly Ellipse flash = new Ellipse();
    readonly ScaleTransform flashScale = new ScaleTransform();
    readonly Rectangle shade;

    readonly TextBlock code = new TextBlock();
    readonly int maxLines;
    readonly TextBlock status = new TextBlock();
    readonly TextBlock title = new TextBlock(), titleShine = new TextBlock(), sub = new TextBlock(), hint = new TextBlock();
    readonly TranslateTransform titleShift = new TranslateTransform(), subShift = new TranslateTransform();
    readonly ScaleTransform titleScale = new ScaleTransform();
    readonly GradientStop shineA = new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
        shineB = new GradientStop(Color.FromArgb(230, 255, 255, 255), 0), shineC = new GradientStop(Color.FromArgb(0, 255, 255, 255), 0);

    readonly Canvas arcLayer = new Canvas(), meteorLayer = new Canvas();
    readonly ArcDef[] arcs;
    readonly Path[] arcTail, arcHead, arcHalo;
    readonly MeteorDef[] meteors;

    // 代码跟视频一样只用灰色，注释再暗一点
    static readonly Brush CodeBrush = Frozen(0x8E, 0x8E, 0x96), CommentBrush = Frozen(0x6C, 0x6C, 0x7A);

    public Scene(double w, double h, Art art)
    {
        W = w; H = h;
        this.art = art;
        edges = new EdgeLayer(art.Fill, TraceStart, TraceEnd);
        if (!art.IsDefault)
        {
            // 用户的图：线条最密的地方就是人物，「睁眼」的光、背景光、网格都对准那里（眼睛一般在重心偏上）
            art.FocusX = edges.CenterX;
            art.FocusY = Math.Max(0.25, Math.Min(0.75, edges.CenterY - 0.06));
        }

        Root.Background = new SolidColorBrush(Color.FromRgb(5, 5, 9));
        Root.ClipToBounds = true;

        // 图摆放：自带的星芒放右侧居中；横图铺满整个窗口；竖的人物图贴右下角铺满高度（跟视频一样）
        double aw, ah, ax, ay;
        if (art.IsDefault)
        {
            ah = H * 0.84; aw = ah * art.Aspect;
            ax = Math.Max(W * 0.40, W * 0.67 - aw / 2); ay = (H - ah) / 2 - H * 0.01;
        }
        else if (art.Cover)
        {
            double k = Math.Max(W / art.Aspect, H);
            ah = k; aw = k * art.Aspect;
            ax = (W - aw) / 2; ay = (H - ah) * 0.3;   // 上下要裁时少裁头顶，人物的头一般在上半截
        }
        else
        {
            ah = H; aw = ah * art.Aspect;
            if (aw > W * 0.70) { aw = W * 0.70; ah = aw / art.Aspect; }
            ax = W - aw; ay = H - ah;
        }

        // 背景：人物后面一团很暗的蓝紫光
        var bg = new Rectangle { Width = W, Height = H };
        var bgBrush = new RadialGradientBrush
        {
            Center = new Point((ax + aw * art.FocusX) / W, (ay + ah * art.FocusY) / H),
            GradientOrigin = new Point((ax + aw * art.FocusX) / W, (ay + ah * art.FocusY) / H),
            RadiusX = 0.55, RadiusY = 0.75,
        };
        bgBrush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0x16, 0x14, 0x30), 0));
        bgBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0x16, 0x14, 0x30), 1));
        bg.Fill = bgBrush;
        canvas.Children.Add(bg);

        // 图层：上色图 → 线稿光晕 → 线稿 → 脸上的三角网格
        artBox.Width = aw; artBox.Height = ah;
        artBox.RenderTransform = artScale;
        artBox.RenderTransformOrigin = new Point(art.FocusX, art.FocusY);
        foreach (var img in new[] { fillImg, glowImg, edgeImg }) img.Stretch = Stretch.Fill;
        fillImg.Source = art.Fill;
        fillImg.Opacity = 0;
        if (art.Fill2 != null)
        {
            fill2Img = new Image { Source = art.Fill2, Stretch = Stretch.Fill, Opacity = 0 };
        }
        glowImg.Source = edges.Bmp;
        glowImg.Effect = new BlurEffect { Radius = 8, RenderingBias = RenderingBias.Performance };
        edgeImg.Source = edges.Bmp;
        artBox.Children.Add(fillImg);
        if (fill2Img != null) artBox.Children.Add(fill2Img);
        artBox.Children.Add(glowImg);
        artBox.Children.Add(edgeImg);
        if (!art.IsDefault)
        {
            mesh = FaceMesh(aw * art.FocusX, ah * art.FocusY, Math.Min(aw, W) * 0.085);
            var meshLayer = new Canvas { IsHitTestVisible = false };
            meshLayer.Children.Add(mesh);
            artBox.Children.Add(meshLayer);
        }
        if (art.Fill2 != null)
        {
            // 有闭眼/睁眼两张图。有「眼睛.txt」就做「逐渐睁眼」（眼缝从闭眼线慢慢张开，露出睁眼图），没有就直接淡入切换
            var eyeLayer = new Canvas { IsHitTestVisible = false };
            var glowRects = new List<Rect>();   // 发光位置（artBox 内的坐标）
            if (art.Eyes != null && art.Eyes.Count > 0)
            {
                double sx = aw / (art.EyeImgW * (1 - 2 * Art.Crop)), sy = ah / (art.EyeImgH * (1 - 2 * Art.Crop));
                Func<double, double, Point> map = (ox, oy) => new Point((ox - art.EyeImgW * Art.Crop) * sx, (oy - art.EyeImgH * Art.Crop) * sy);
                var lids = new List<LidAnim>();
                foreach (double[] e in art.Eyes)
                {
                    var L = new LidAnim { O = map(e[0], e[1]), I = map(e[2], e[3]), T = map(e[4], e[5]), B = map(e[6], e[7]) };
                    L.Lc = Ctrl(L.O, L.B, L.I);
                    L.Uc = Ctrl(L.O, L.T, L.I);
                    double lw = 3.2 * sx;
                    L.Shadow = new Path { Stroke = new SolidColorBrush(Color.FromArgb(255, 22, 16, 40)), StrokeThickness = lw * 2.6, Opacity = 0, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                    L.Lid = new Path { Stroke = new SolidColorBrush(Color.FromArgb(240, 30, 22, 52)), StrokeThickness = lw, Opacity = 0, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                    L.Lash = new Path { Stroke = new SolidColorBrush(Color.FromArgb(230, 30, 22, 52)), StrokeThickness = lw * 0.55, Opacity = 0, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Triangle };
                    eyeLayer.Children.Add(L.Shadow);
                    eyeLayer.Children.Add(L.Lid);
                    eyeLayer.Children.Add(L.Lash);
                    lids.Add(L);
                    double ew = Math.Abs(L.O.X - L.I.X);
                    glowRects.Add(new Rect((L.T.X + L.B.X) / 2 - ew * 0.5, (L.T.Y + L.B.Y) / 2 - ew * 0.25, ew, ew * 0.5));
                }
                lidAnims = lids.ToArray();
                // 睁眼图只在「眼缝」里露出来：遮罩每帧重算
                apertureDrawing = new GeometryDrawing(Brushes.Black, null, Geometry.Empty);
                fill2Img.OpacityMask = new DrawingBrush(apertureDrawing)
                {
                    Viewbox = new Rect(0, 0, aw, ah), ViewboxUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, aw, ah), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill,
                };
                fill2Img.Opacity = 1;
                // 完全睁开后整张换成睁眼图（眼角、睫毛尖这些眼缝外的细节也补齐）
                fill2Full = new Image { Source = art.Fill2, Stretch = Stretch.Fill, Opacity = 0 };
                artBox.Children.Insert(artBox.Children.IndexOf(fill2Img) + 1, fill2Full);
            }
            else
            {
                foreach (Rect e in EyeFinder.Find(art.Fill, art.Fill2))
                    glowRects.Add(new Rect(e.X * aw, e.Y * ah, e.Width * aw, e.Height * ah));
            }
            // 眼睛的蓝光
            var found = new List<Ellipse>();
            foreach (Rect e in glowRects)
            {
                double gs = Math.Max(e.Width * 1.15, aw * 0.035);   // 只罩住眼睛，别把整只眼糊成一团光
                var rg = new RadialGradientBrush();
                rg.GradientStops.Add(new GradientStop(Color.FromArgb(200, 0xE6, 0xF7, 0xFF), 0));
                rg.GradientStops.Add(new GradientStop(Color.FromArgb(150, 0x7F, 0xD8, 0xFF), 0.2));
                rg.GradientStops.Add(new GradientStop(Color.FromArgb(70, 0x4F, 0x8C, 0xFF), 0.45));
                rg.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0x4F, 0x6C, 0xFF), 1));
                var glow = new Ellipse { Width = gs, Height = gs * 0.8, Fill = rg, Opacity = 0 };
                Canvas.SetLeft(glow, e.X + e.Width / 2 - gs / 2);
                Canvas.SetTop(glow, e.Y + e.Height / 2 - gs * 0.4);
                eyeLayer.Children.Add(glow);
                found.Add(glow);
            }
            eyeGlows = found.ToArray();
            artBox.Children.Add(eyeLayer);
        }
        if (art.Fill2 == null && art.EyeFrames == null && art.Eyes != null && art.Eyes.Count > 0)
        {
            // 照 ChatGPT 版的睁眼：描线时整张脸是暗的；然后两只眼睛先亮起来（只在眼睛处提前露出彩色图 + 一圈蓝光），之后整张图才上色
            double sx = aw / (art.EyeImgW * (1 - 2 * Art.Crop)), sy = ah / (art.EyeImgH * (1 - 2 * Art.Crop));
            Func<double, double, Point> map = (ox, oy) => new Point((ox - art.EyeImgW * Art.Crop) * sx, (oy - art.EyeImgH * Art.Crop) * sy);
            var maskGroup = new DrawingGroup();
            var eyeLayer = new Canvas { IsHitTestVisible = false };
            var halos = new List<Ellipse>();
            foreach (double[] e in art.Eyes)
            {
                Point O = map(e[0], e[1]), I = map(e[2], e[3]), T = map(e[4], e[5]), B = map(e[6], e[7]);
                double ew = Math.Abs(O.X - I.X), eh = Math.Max(B.Y - T.Y, ew * 0.4);
                var c = new Point((O.X + I.X) / 2, (T.Y + B.Y) / 2);
                // 眼睛的柔边遮罩（中间实、边缘渐隐）
                var soft = new RadialGradientBrush();
                soft.GradientStops.Add(new GradientStop(Colors.Black, 0));
                soft.GradientStops.Add(new GradientStop(Colors.Black, 0.55));
                soft.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
                maskGroup.Children.Add(new GeometryDrawing(soft, null, new EllipseGeometry(c, ew * 0.68, eh * 0.85)));
                // 眼睛周围的蓝光
                double hw = ew * 1.6, hh = eh * 2.1;
                var hg = new RadialGradientBrush();
                hg.GradientStops.Add(new GradientStop(Color.FromArgb(105, 0x9C, 0xD8, 0xFF), 0));
                hg.GradientStops.Add(new GradientStop(Color.FromArgb(80, 0x5F, 0xB4, 0xFF), 0.25));
                hg.GradientStops.Add(new GradientStop(Color.FromArgb(38, 0x5A, 0x86, 0xFF), 0.6));
                hg.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0x5A, 0x6C, 0xFF), 1));
                var halo = new Ellipse { Width = hw, Height = hh, Fill = hg, Opacity = 0 };
                Canvas.SetLeft(halo, c.X - hw / 2); Canvas.SetTop(halo, c.Y - hh / 2);
                eyeLayer.Children.Add(halo);
                halos.Add(halo);
            }
            // 遮罩画布要跟 artBox 一样大，眼睛才对得上位置：放一块透明的底撑开范围
            maskGroup.Children.Insert(0, new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, aw, ah))));
            eyeColorImg = new Image
            {
                Source = art.Fill, Stretch = Stretch.Fill, Opacity = 0,
                OpacityMask = new DrawingBrush(maskGroup) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, aw, ah) },
            };
            artBox.Children.Insert(artBox.Children.IndexOf(fillImg) + 1, eyeColorImg);   // 在线稿下面，线条还盖在眼睛上
            eyeGlows = halos.ToArray();
            artBox.Children.Add(eyeLayer);
        }
        var eyeSpots = new List<double[]>();   // 每只眼：光心 x、y，眼宽，眼高（artBox 里的坐标），压暗层留洞用
        if (art.EyeFrames != null)
        {
            // 睁眼帧：这几张图除了眼睛跟原图一模一样，所以直接整张叠上去淡入，看起来就只有眼睛在动
            int n = art.EyeFrames.Count;
            eyeFrameImgs = new Image[n];
            int at = artBox.Children.IndexOf(fillImg) + 1;
            for (int i = 0; i < n; i++)
            {
                // 睁眼前只露左上角 1 个像素（这几张图在那里跟闭眼图一模一样，看不出来），让显卡先把图载好；之前用整张 1% 透明叠 12 层，会透出一成睁眼画面
                eyeFrameImgs[i] = new Image { Source = art.EyeFrames[i], Stretch = Stretch.Fill, Opacity = 0.01, Clip = new RectangleGeometry(new Rect(0, 0, 1, 1)) };
                artBox.Children.Insert(at + i, eyeFrameImgs[i]);
            }
            if (art.Eyes != null && art.Eyes.Count > 0)
            {
                double sx = aw / (art.EyeImgW * (1 - 2 * Art.Crop)), sy = ah / (art.EyeImgH * (1 - 2 * Art.Crop));
                Func<double, double, Point> map = (ox, oy) => new Point((ox - art.EyeImgW * Art.Crop) * sx, (oy - art.EyeImgH * Art.Crop) * sy);
                // 每张帧图只在两只眼睛的框里露出来，框里从下往上渐变：交界线往上走 = 眼皮往上抬
                // 方框 = 闭眼图和睁眼图所有有一点点不一样的地方（再每边多扩 3 格），框边落在两张图完全相同处，才不会出现接缝
                var eyeRects = new List<Rect>();
                if (n < FineFrames)
                foreach (Rect e in EyeFinder.Find(art.Fill, art.EyeFrames[n - 1], 12, 30, 3))
                    eyeRects.Add(new Rect(e.X * aw, e.Y * ah, e.Width * aw, e.Height * ah));
                if (n < FineFrames) { wipeSolid = new GradientStop[n]; wipeEdge = new GradientStop[n]; }
                for (int i = 0; n < FineFrames && i < n; i++)
                {
                    var g = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };   // 从框底往框顶
                    wipeSolid[i] = new GradientStop(Colors.Black, 0);
                    wipeEdge[i] = new GradientStop(Color.FromArgb(0, 0, 0, 0), 0);
                    g.GradientStops.Add(new GradientStop(Colors.Black, 0));
                    g.GradientStops.Add(wipeSolid[i]);
                    g.GradientStops.Add(wipeEdge[i]);
                    g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
                    var dg = new DrawingGroup();
                    dg.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, aw, ah))));   // 撑开到整张图大小
                    foreach (Rect r in eyeRects) dg.Children.Add(new GeometryDrawing(g, null, new RectangleGeometry(r)));
                    eyeFrameImgs[i].OpacityMask = new DrawingBrush(dg) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, aw, ah) };
                    eyeFrameImgs[i].Opacity = 1;
                }
                var eyeLayer = new Canvas { IsHitTestVisible = false };
                var halos = new List<Ellipse>();
                var cores = new List<Ellipse>();
                var flares = new List<Ellipse>();
                var blooms = new List<Ellipse>();
                var eyesPx = new List<double[]>();   // 同样四个数，换成位图像素（提亮眼珠那层用）
                // 光晕、光芯：先缩放（睁眼时由扁变圆）再往下挪（从闭眼线升到眼珠正中）
                var haloMove = new TransformGroup { Children = { eyeHaloScale, eyeDrop } };
                var coreMove = new TransformGroup { Children = { eyeCoreScale, eyeDrop } };
                BitmapSource openBmp = art.EyeFrames[n - 1];
                double bx = openBmp.PixelWidth / (art.EyeImgW * (1 - 2 * Art.Crop)), by = openBmp.PixelHeight / (art.EyeImgH * (1 - 2 * Art.Crop));
                for (int ei = 0; ei < art.Eyes.Count; ei++)
                {
                    double[] e = art.Eyes[ei];
                    Point O = map(e[0], e[1]), I = map(e[2], e[3]), T = map(e[4], e[5]), B = map(e[6], e[7]);
                    double ew = Math.Abs(O.X - I.X), eh = Math.Max(B.Y - T.Y, ew * 0.4);
                    // 光打在眼珠（瞳孔）上；「眼睛.txt」没写瞳孔位置就打在眼眶正中
                    var c = art.Pupils != null && art.Pupils.Count == art.Eyes.Count
                        ? map(art.Pupils[ei][0], art.Pupils[ei][1])
                        : new Point((O.X + I.X) / 2, (T.Y + B.Y) / 2);
                    eyeSpots.Add(new[] { c.X, c.Y, ew, eh });
                    eyesPx.Add(new[] { c.X / sx * bx, c.Y / sy * by, ew / sx * bx, eh / sy * by });
                    // 洒到脸上的光：很大很淡的一圈，垫在最下面
                    double bw = ew * 3.6, bh = eh * 5.2;
                    var bl = new RadialGradientBrush();
                    bl.GradientStops.Add(new GradientStop(Color.FromArgb(100, 0x8C, 0xCC, 0xFF), 0));
                    bl.GradientStops.Add(new GradientStop(Color.FromArgb(50, 0x6A, 0xA8, 0xFF), 0.4));
                    bl.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0x5A, 0x7C, 0xFF), 1));
                    var bloom = new Ellipse { Width = bw, Height = bh, Fill = bl, Opacity = 0, RenderTransform = eyeBloomScale, RenderTransformOrigin = new Point(0.5, 0.5) };
                    Canvas.SetLeft(bloom, c.X - bw / 2); Canvas.SetTop(bloom, c.Y - bh / 2);
                    eyeLayer.Children.Add(bloom);
                    blooms.Add(bloom);
                    // 光晕：比以前更亮更大一圈，全开那一下还会往外胀一下（eyeHaloScale）
                    double hw = ew * 1.75, hh = eh * 2.3;
                    var hg = new RadialGradientBrush();
                    hg.GradientStops.Add(new GradientStop(Color.FromArgb(205, 0xC8, 0xEC, 0xFF), 0));
                    hg.GradientStops.Add(new GradientStop(Color.FromArgb(150, 0x74, 0xC8, 0xFF), 0.25));
                    hg.GradientStops.Add(new GradientStop(Color.FromArgb(68, 0x5A, 0x8C, 0xFF), 0.6));
                    hg.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0x5A, 0x6C, 0xFF), 1));
                    var halo = new Ellipse { Width = hw, Height = hh, Fill = hg, Opacity = 0, RenderTransform = haloMove, RenderTransformOrigin = new Point(0.5, 0.5) };
                    Canvas.SetLeft(halo, c.X - hw / 2); Canvas.SetTop(halo, c.Y - hh / 2);
                    eyeLayer.Children.Add(halo);
                    halos.Add(halo);
                    // 亮光芯：小而亮，罩在眼珠上
                    double cw = ew * 0.8, ch = eh * 1.05;
                    var cg = new RadialGradientBrush();
                    cg.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0xF6, 0xFC, 0xFF), 0));
                    cg.GradientStops.Add(new GradientStop(Color.FromArgb(185, 0xA0, 0xDC, 0xFF), 0.32));
                    cg.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0x6A, 0xB4, 0xFF), 1));
                    var coreE = new Ellipse { Width = cw, Height = ch, Fill = cg, Opacity = 0, RenderTransform = coreMove, RenderTransformOrigin = new Point(0.5, 0.5) };
                    Canvas.SetLeft(coreE, c.X - cw / 2); Canvas.SetTop(coreE, c.Y - ch / 2);
                    eyeLayer.Children.Add(coreE);
                    cores.Add(coreE);
                    // 横光：全开那一下一道细长的白光从眼珠往两边划开，马上收掉（不一直罩着，眼睛细节不糊）
                    double fw = ew * 2.6, fh = Math.Max(eh * 0.2, 3);
                    var fg = new RadialGradientBrush();
                    fg.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0xFF, 0xFF, 0xFF), 0));
                    fg.GradientStops.Add(new GradientStop(Color.FromArgb(210, 0xD4, 0xF0, 0xFF), 0.12));
                    fg.GradientStops.Add(new GradientStop(Color.FromArgb(95, 0x86, 0xCC, 0xFF), 0.42));
                    fg.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0x6A, 0xB4, 0xFF), 1));
                    var flare = new Ellipse { Width = fw, Height = fh, Fill = fg, Opacity = 0, RenderTransform = eyeFlareScale, RenderTransformOrigin = new Point(0.5, 0.5) };
                    Canvas.SetLeft(flare, c.X - fw / 2); Canvas.SetTop(flare, c.Y - fh / 2);
                    eyeLayer.Children.Add(flare);
                    flares.Add(flare);
                }
                eyeGlows = halos.ToArray();
                eyeCores = cores.ToArray();
                eyeFlares = flares.ToArray();
                eyeBlooms = blooms.ToArray();
                eyeBrightImg = new Image { Source = MakeEyeBright(openBmp, eyesPx), Stretch = Stretch.Fill, Opacity = 0, IsHitTestVisible = false };

                // 扩散的光晕：圆心在两只眼珠正中间，从刚好套住两只眼睛的大小，扩到盖过整张图
                double wx = 0, wy = 0, ehAvg = 0;
                foreach (double[] s in eyeSpots) { wx += s[0]; wy += s[1]; ehAvg += s[3]; }
                wx /= eyeSpots.Count; wy /= eyeSpots.Count; ehAvg /= eyeSpots.Count;
                eyeDropPx = ehAvg * 0.2;
                double gap = 0;
                foreach (double[] s in eyeSpots) gap = Math.Max(gap, Math.Sqrt((s[0] - wx) * (s[0] - wx) + (s[1] - wy) * (s[1] - wy)));
                waveR0 = Math.Max(gap * 1.16, ehAvg);   // 光带最亮处在半径的 0.86：一开始正好压在两只眼珠上
                foreach (Point corner in new[] { new Point(0, 0), new Point(aw, 0), new Point(0, ah), new Point(aw, ah) })
                    waveR1 = Math.Max(waveR1, Math.Sqrt((corner.X - wx) * (corner.X - wx) + (corner.Y - wy) * (corner.Y - wy)));
                // 「只露出光带那一圈」：宽、柔、没有硬边；圆心在两眼之间，半径每帧改
                waveBand = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = new Point(wx, wy), GradientOrigin = new Point(wx, wy), RadiusX = waveR0, RadiusY = waveR0 };
                waveBand.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
                waveBand.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.45));
                waveBand.GradientStops.Add(new GradientStop(Color.FromArgb(150, 0, 0, 0), 0.73));
                waveBand.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.86));
                waveBand.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
                waveBox = new Grid { Width = aw, Height = ah, IsHitTestVisible = false, Opacity = 0, Visibility = Visibility.Collapsed, OpacityMask = waveBand };
                // （试过给这一组加 BitmapCache 按原图分辨率缓存：线稿每帧都在变，缓存每帧重做，反而慢 2 帧，不用）
                waveBox.Children.Add(new Image { Source = MakeLit(openBmp, art.Mask), Stretch = Stretch.Fill });   // 被光照亮的人物
                waveBox.Children.Add(new Image { Source = edges.Bmp, Stretch = Stretch.Fill });                    // 霓虹线稿跟着亮一下
                artBox.Children.Add(waveBox);
                artBox.Children.Add(eyeLayer);
            }
        }
        if (!art.IsDefault)
        {
            // 压暗层盖在上色图（和睁眼帧）上面、线稿和眼睛的光下面
            // 有「人物蒙版.png」：只压暗人物以外的地方，人物保持原来的亮度；没有蒙版：整张压暗、两只眼睛处留洞
            BitmapSource hole, plug = null;
            if (art.Mask != null) hole = MakeDimFromMask(art.Mask);
            else MakeDim(aw, ah, art.FocusX, art.FocusY, eyeSpots, out hole, out plug);
            dimImg = new Image { Source = hole, Stretch = Stretch.Fill, Opacity = 0, IsHitTestVisible = false };
            int di = artBox.Children.IndexOf(glowImg);
            artBox.Children.Insert(di++, dimImg);
            if (plug != null)
            {
                dimEyesImg = new Image { Source = plug, Stretch = Stretch.Fill, Opacity = 0, IsHitTestVisible = false };
                artBox.Children.Insert(di++, dimEyesImg);
            }
            if (eyeBrightImg != null) artBox.Children.Insert(di, eyeBrightImg);
        }
        UIElement artNode = artBox;
        if (!art.IsDefault && !art.Cover)
        {
            // 人物图左边、上边渐隐，融进黑底
            artBox.OpacityMask = Fade(new Point(0, 0), new Point(1, 0), 0.25);
            var outer = new Grid { Width = aw, Height = ah, OpacityMask = Fade(new Point(0, 0), new Point(0, 1), ah < H * 0.98 ? 0.22 : 0.08) };
            outer.Children.Add(artBox);
            artNode = outer;
        }
        Canvas.SetLeft(artNode, ax); Canvas.SetTop(artNode, ay);
        canvas.Children.Add(artNode);

        if (art.Cover)
        {
            // 铺满的图上色后左边可能偏亮：压一道黑渐变，代码才看得清
            var sb = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            sb.GradientStops.Add(new GradientStop(Color.FromArgb(200, 5, 5, 9), 0));
            sb.GradientStops.Add(new GradientStop(Color.FromArgb(110, 5, 5, 9), 0.32));
            sb.GradientStops.Add(new GradientStop(Color.FromArgb(0, 5, 5, 9), 0.6));
            shade = new Rectangle { Width = W, Height = H, Fill = sb, Opacity = 0, IsHitTestVisible = false };
            canvas.Children.Add(shade);
        }

        // 「睁眼」那一下的光：人物图用冷的蓝白光（像视频里眼睛亮起），自带星芒用暖光
        double fs = art.Cover ? Math.Min(aw, ah) * 0.6 : aw * 0.75;
        flash.Width = fs; flash.Height = fs;
        var fb = new RadialGradientBrush();
        if (art.IsDefault)
        {
            fb.GradientStops.Add(new GradientStop(Color.FromArgb(235, 255, 250, 240), 0));
            fb.GradientStops.Add(new GradientStop(Color.FromArgb(150, 0xFF, 0xC2, 0x8C), 0.22));
            fb.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0xD9, 0x77, 0x57), 1));
        }
        else
        {
            fb.GradientStops.Add(new GradientStop(Color.FromArgb(220, 0xEA, 0xF6, 0xFF), 0));
            fb.GradientStops.Add(new GradientStop(Color.FromArgb(120, 0x6F, 0xC8, 0xFF), 0.2));
            fb.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0x5B, 0x6C, 0xFF), 1));
        }
        flash.Fill = fb;
        flash.RenderTransform = flashScale;
        flash.RenderTransformOrigin = new Point(0.5, 0.5);
        flash.IsHitTestVisible = false;
        Canvas.SetLeft(flash, ax + aw * art.FocusX - fs / 2); Canvas.SetTop(flash, ay + ah * art.FocusY - fs / 2);
        canvas.Children.Add(flash);

        // 左边的代码：灰色、行距宽，写满了往上滚
        double fsz = Math.Round(H * 0.0155, 1);
        double codeTop = H * 0.085, codeH = H * 0.80;
        code.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
        code.FontSize = fsz;
        code.LineHeight = fsz * 2.0;
        code.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        code.Opacity = 0.85;
        maxLines = Math.Max(4, (int)(codeH / code.LineHeight));
        Canvas.SetLeft(code, W * 0.045); Canvas.SetTop(code, codeTop);
        canvas.Children.Add(code);

        // 光弧：细长的椭圆光带横扫过画面，冰蓝 / 紫 / 金 / 青
        arcs = new[]
        {
            new ArcDef(0.62 * W, 0.53 * H, 0.50 * W, 0.13 * H, -12, 62, 200, Color.FromRgb(0xCF, 0xE8, 0xFF)),
            new ArcDef(0.55 * W, 0.60 * H, 0.56 * W, 0.20 * H, 8, -48, 20, Color.FromRgb(0xB8, 0x9C, 0xFF)),
            new ArcDef(0.70 * W, 0.42 * H, 0.38 * W, 0.10 * H, -27, 80, 120, Color.FromRgb(0xFF, 0xE0, 0x9C)),
            new ArcDef(0.58 * W, 0.70 * H, 0.47 * W, 0.08 * H, 4, -70, 300, Color.FromRgb(0x9F, 0xE4, 0xFF)),
        };
        arcTail = new Path[arcs.Length];
        arcHead = new Path[arcs.Length];
        arcHalo = new Path[arcs.Length];
        for (int i = 0; i < arcs.Length; i++)
        {
            // 发光不用 Effect（全屏透明窗口下太吃显卡），垫一条粗的半透明线当光晕
            arcHalo[i] = new Path { Stroke = new SolidColorBrush(arcs[i].Color), StrokeThickness = 12, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            arcTail[i] = new Path { Stroke = new SolidColorBrush(arcs[i].Color), StrokeThickness = 1.2, Opacity = 0.42 };
            arcHead[i] = new Path { Stroke = new SolidColorBrush(arcs[i].Color), StrokeThickness = 2.3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            arcLayer.Children.Add(arcHalo[i]);
            arcLayer.Children.Add(arcTail[i]);
            arcLayer.Children.Add(arcHead[i]);
        }
        arcLayer.IsHitTestVisible = false;
        canvas.Children.Add(arcLayer);

        // 流星：竖直往下落的光条，紫 / 蓝 / 青 / 金 / 淡紫白
        var rnd = new Random(11);
        Color[] palette =
        {
            Color.FromRgb(0xA8, 0x74, 0xFF), Color.FromRgb(0x7F, 0x6B, 0xFF), Color.FromRgb(0x4F, 0xB8, 0xFF),
            Color.FromRgb(0x6F, 0xE0, 0xFF), Color.FromRgb(0xFF, 0xD3, 0x6B), Color.FromRgb(0xE6, 0xDA, 0xFF),
        };
        meteors = new MeteorDef[28];   // 以前 18 颗；图压暗后多放一些、光晕加粗，头上加一粒发光的光点
        for (int i = 0; i < meteors.Length; i++)
        {
            var m = new MeteorDef();
            m.Start = 1.0 + i * 0.29 + rnd.NextDouble() * 0.25;
            m.Dur = 1.0 + rnd.NextDouble() * 0.8;
            m.Len = H * (0.08 + rnd.NextDouble() * 0.12);
            m.X = W * (0.05 + rnd.NextDouble() * 0.92);
            Color c = palette[rnd.Next(palette.Length)];
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(220, c.R, c.G, c.B), 0.82));
            g.GradientStops.Add(new GradientStop(Colors.White, 1));
            var spark = new RadialGradientBrush();
            spark.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 0));
            spark.GradientStops.Add(new GradientStop(Color.FromArgb(170, c.R, c.G, c.B), 0.28));
            spark.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            m.Shape = new Grid { Width = 22, Height = m.Len, Visibility = Visibility.Hidden };
            m.Shape.Children.Add(new Rectangle { Width = 10, Fill = g, Opacity = 0.32, RadiusX = 5, RadiusY = 5 });
            m.Shape.Children.Add(new Rectangle { Width = rnd.NextDouble() < 0.5 ? 2.5 : 2, Fill = g });
            m.Shape.Children.Add(new Ellipse { Width = 22, Height = 22, Fill = spark, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -9) });
            meteorLayer.Children.Add(m.Shape);
            meteors[i] = m;
        }
        meteorLayer.IsHitTestVisible = false;
        canvas.Children.Add(meteorLayer);

        // 收尾的大标题「Claude」+「欢迎主人回家」：蓝紫金渐变、紫色光晕，一道白光从左往右扫过
        var titleFont = new FontFamily("Segoe UI Semilight, Segoe UI");
        double titleSize = Math.Round(H * 0.19);   // 用户要「字改大一些」：约占屏幕高度的 1/5
        var titleFill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        titleFill.GradientStops.Add(new GradientStop(Color.FromRgb(0x8C, 0xC4, 0xFF), 0));
        titleFill.GradientStops.Add(new GradientStop(Color.FromRgb(0xB8, 0x92, 0xFF), 0.55));
        titleFill.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xDB, 0x8E), 1));
        foreach (var tb in new[] { title, titleShine })
        {
            tb.Text = TitleText;
            tb.FontFamily = titleFont;
            tb.FontSize = titleSize;
        }
        title.Foreground = titleFill;
        title.Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 36, Color = Color.FromRgb(0x8E, 0x6B, 0xFF), Opacity = 0.85 };
        titleShine.Foreground = Brushes.White;
        var shineMask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        shineMask.GradientStops.Add(shineA);
        shineMask.GradientStops.Add(shineB);
        shineMask.GradientStops.Add(shineC);
        titleShine.OpacityMask = shineMask;
        var titleBox = new Grid { RenderTransform = new TransformGroup { Children = { titleScale, titleShift } }, RenderTransformOrigin = new Point(0.5, 0.5), HorizontalAlignment = HorizontalAlignment.Center };
        titleBox.Children.Add(title);
        titleBox.Children.Add(titleShine);
        sub.Text = SubText;
        sub.FontFamily = new FontFamily("Microsoft YaHei UI Light, Microsoft YaHei UI");
        sub.FontSize = Math.Round(H * 0.046);
        sub.Foreground = Frozen(0xE4, 0xDE, 0xFF);
        sub.Margin = new Thickness(0, H * 0.005, 0, 0);
        sub.HorizontalAlignment = HorizontalAlignment.Center;   // 跟「Claude」居中对齐
        sub.Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 14, Color = Color.FromRgb(0x6F, 0x8C, 0xFF), Opacity = 0.7 };
        sub.RenderTransform = subShift;
        // 最下面一行慢慢呼吸的提示：动画播完停在这一幕，按任意键才进入
        hint.Text = "按 任 意 键 进 入";
        hint.FontFamily = new FontFamily("Microsoft YaHei UI");
        hint.FontSize = Math.Round(H * 0.019);
        hint.Foreground = Frozen(0xC8, 0xC0, 0xF0);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.Margin = new Thickness(0, H * 0.07, 0, 0);
        var titlePanel = new StackPanel { IsHitTestVisible = false };
        titlePanel.Children.Add(titleBox);
        titlePanel.Children.Add(sub);
        titlePanel.Children.Add(hint);
        title.Opacity = 0; titleShine.Opacity = 0; sub.Opacity = 0; hint.Opacity = 0;
        Canvas.SetLeft(titlePanel, W * 0.055); Canvas.SetTop(titlePanel, H * 0.30);
        canvas.Children.Add(titlePanel);

        status.FontFamily = new FontFamily("Microsoft YaHei UI");
        status.FontSize = Math.Round(H * 0.015);
        status.Foreground = Frozen(0x8C, 0x88, 0x98);
        Canvas.SetLeft(status, W * 0.045); Canvas.SetBottom(status, H * 0.06);
        canvas.Children.Add(status);

        Root.Children.Add(canvas);
    }

    // 脸上那层「扫描建模」的白色三角网格：在人物重心周围撒抖动的网格点，连成三角形
    static Path FaceMesh(double cx, double cy, double rx)
    {
        double ry = rx * 1.3, s = rx / 3.4;
        var rnd = new Random(3);
        int n = (int)Math.Ceiling(rx * 1.2 / s), m = (int)Math.Ceiling(ry * 1.2 / s);
        var pts = new Point?[2 * n + 2, 2 * m + 2];
        for (int i = 0; i <= 2 * n; i++)
            for (int j = 0; j <= 2 * m; j++)
            {
                double px = cx + (i - n) * s + (rnd.NextDouble() - 0.5) * s * 0.7;
                double py = cy + (j - m) * s + (rnd.NextDouble() - 0.5) * s * 0.7;
                double dx = (px - cx) / rx, dy = (py - cy) / ry;
                if (dx * dx + dy * dy <= 1.0) pts[i, j] = new Point(px, py);
            }
        var sg = new StreamGeometry();
        using (StreamGeometryContext ctx = sg.Open())
        {
            for (int i = 0; i <= 2 * n; i++)
                for (int j = 0; j <= 2 * m; j++)
                {
                    Seg(ctx, pts[i, j], pts[i + 1, j]);
                    Seg(ctx, pts[i, j], pts[i, j + 1]);
                    if ((i + j) % 2 == 0) Seg(ctx, pts[i, j], pts[i + 1, j + 1]);
                    else Seg(ctx, pts[i + 1, j], pts[i, j + 1]);
                }
        }
        sg.Freeze();
        return new Path { Data = sg, Stroke = new SolidColorBrush(Color.FromRgb(0xDC, 0xE6, 0xFF)), StrokeThickness = 0.6, Opacity = 0 };
    }

    static void Seg(StreamGeometryContext ctx, Point? a, Point? b)
    {
        if (!a.HasValue || !b.HasValue) return;
        ctx.BeginFigure(a.Value, false, false);
        ctx.LineTo(b.Value, true, false);
    }

    public void Update(double t, bool waiting)
    {
        // 线稿：从上往下描，人物透出来以后退成淡淡一层；「睁眼」时光晕再亮一下
        if (t <= MinEnd || !edgesFrozen) { edges.Render(Math.Min(t, MinEnd)); edgesFrozen = t > MinEnd; }
        bool awaken = eyeColorImg != null;   // 「眼睛先亮」模式（照 ChatGPT 版）
        double lineA = Ramp(t, TraceStart - 0.1, TraceStart + 0.1) * (1 - 0.8 * (awaken ? Ramp(t, 5.4, 7.0) : Ramp(t, 3.8, 5.4)));
        bool framed = eyeFrameImgs != null;   // 「睁眼帧」模式
        double wake = (awaken || framed) ? 0 : Bell(t, WakeStart, WakeStart + 0.3, WakeStart + 0.5, WakeEnd);
        edgeImg.Opacity = lineA;
        glowImg.Opacity = Math.Min(1, lineA * 0.9 + wake * 0.45);
        if (mesh != null) mesh.Opacity = 0.5 * Bell(t, 1.8, 2.5, 4.3, 5.3);

        // 上色：先暗暗地透出来，再变成全彩
        if (awaken)
        {
            // 照 ChatGPT 版的节奏：脸一直暗着（只透出 28%）→ 4.5 秒两只眼睛先亮起来 → 5.6~6.8 秒整张图才上色
            fillImg.Opacity = 0.28 * Ramp(t, 3.4, 4.4) + 0.72 * Ramp(t, EyeLight + 1.1, EyeLight + 2.3);
            if (shade != null) shade.Opacity = Ramp(t, 3.4, EyeLight + 2.3);
            eyeColorImg.Opacity = Ramp(t, EyeLight, EyeLight + 0.8);
        }
        else
        {
            if (framed)
                for (int i = 0; i < eyeFrameImgs.Length; i++)
                {
                    // 一口气连续睁开：k 从 0 平滑走到 1，按 k 落在第几张之间来过渡，中间不停顿。
                    // 帧细的时候用更柔的曲线：开头眼皮沉、慢慢抬，中间顺，最后轻轻停住（起止速度都是 0）
                    double k = EyeOpenK(t);
                    double kp = k * eyeFrameImgs.Length;                // 0=闭眼，n=全开
                    double f = Math.Max(0, Math.Min(1, kp - i));         // 这一张露出来多少
                    if (wipeSolid != null)
                    {
                        if (t >= EyeOpenStart && eyeFrameImgs[i].Clip != null) eyeFrameImgs[i].Clip = null;
                        double pos = f * (1 + WipeBand);                 // 交界线从框底往上走
                        wipeSolid[i].Offset = Math.Max(0, Math.Min(1, pos - WipeBand));
                        wipeEdge[i].Offset = Math.Max(0, Math.Min(1, pos));
                    }
                    else
                    {
                        // 相邻两张淡入。睁之前留 1% 让显卡先把图载好；睁的时候只画正在过渡的那两张，被盖住的、还没轮到的都不画，省资源
                        double fNext = Math.Max(0, Math.Min(1, kp - i - 1));
                        bool show = t < EyeOpenStart || (f > 0 && (i == eyeFrameImgs.Length - 1 || fNext < 1));
                        eyeFrameImgs[i].Visibility = show ? Visibility.Visible : Visibility.Hidden;
                        eyeFrameImgs[i].Opacity = t < EyeOpenStart ? 0.01 : f;
                        if (t >= EyeOpenStart && eyeFrameImgs[i].Clip != null) eyeFrameImgs[i].Clip = null;   // 开始睁眼才整张露出来
                    }
                }
            fillImg.Opacity = 0.3 * Ramp(t, FillStart, FillStart + 1.0) + 0.7 * Ramp(t, FillStart + 1.0, FillEnd);
            if (shade != null) shade.Opacity = Ramp(t, FillStart, FillEnd);
        }
        if (dimImg != null)
        {
            // 压暗跟着上色一起来；眼睛处的洞睁眼前补着，随着眼睛睁开撤掉
            double dimK = Ramp(t, FillStart + 1.0, FillEnd);
            dimImg.Opacity = dimK;
            if (dimEyesImg != null) dimEyesImg.Opacity = dimK * (framed ? 1 - EyeOpenK(t) : 1);
        }
        if (lidAnims != null)
        {
            // 逐渐睁眼：k=0 闭着，0.42 半睁，1 全开
            double k = 0.42 * Ramp(t, EyeStart, EyeHalf) + 0.58 * Ramp(t, EyeHoldEnd, WakeStart);
            var group = new GeometryGroup();
            foreach (var L in lidAnims)
            {
                if (k > 0.001)
                {
                    Point uc = new Point(L.Lc.X + (L.Uc.X - L.Lc.X) * k, L.Lc.Y + (L.Uc.Y - L.Lc.Y) * k);
                    // 眼缝：上眼皮曲线（随 k 抬起）和下眼皮曲线围成的一片
                    var fig = new PathFigure { StartPoint = L.O, IsClosed = true, IsFilled = true };
                    fig.Segments.Add(new QuadraticBezierSegment(uc, L.I, true));
                    fig.Segments.Add(new QuadraticBezierSegment(L.Lc, L.O, true));
                    var pg = new PathGeometry(); pg.Figures.Add(fig);
                    group.Children.Add(pg);
                    // 上眼皮线、眼皮下的阴影、外眼角几根睫毛，跟着眼缝上沿走；快全开时让位给原图自己的睫毛
                    var lidFig = new PathFigure { StartPoint = L.O, IsClosed = false, IsFilled = false };
                    lidFig.Segments.Add(new QuadraticBezierSegment(uc, L.I, true));
                    var lidGeo = new PathGeometry(); lidGeo.Figures.Add(lidFig);
                    L.Lid.Data = lidGeo;
                    L.Shadow.Data = lidGeo;
                    var lash = new StreamGeometry();
                    using (var c = lash.Open())
                    {
                        double dir = L.O.X < L.I.X ? -1 : 1;   // 外眼角朝哪边
                        double len = Math.Abs(L.O.X - L.I.X);
                        double[] at = { 0.02, 0.09, 0.17 };
                        for (int j = 0; j < at.Length; j++)
                        {
                            double u = at[j], v = 1 - u;
                            var lp = new Point(v * v * L.O.X + 2 * v * u * uc.X + u * u * L.I.X, v * v * L.O.Y + 2 * v * u * uc.Y + u * u * L.I.Y);
                            c.BeginFigure(lp, false, false);
                            c.LineTo(new Point(lp.X + dir * len * (0.13 - j * 0.03), lp.Y - len * (0.06 - j * 0.015)), true, false);
                        }
                    }
                    L.Lash.Data = lash;
                    double lo = 1 - Ramp(k, 0.8, 1.0);
                    L.Lid.Opacity = lo; L.Lash.Opacity = lo; L.Shadow.Opacity = 0;   // 阴影描边会变成一圈黑边，不用
                }
                else { L.Lid.Opacity = 0; L.Lash.Opacity = 0; L.Shadow.Opacity = 0; }
            }
            apertureDrawing.Geometry = group.Children.Count > 0 ? (Geometry)group : Geometry.Empty;
            fill2Full.Opacity = Ramp(t, WakeStart - 0.05, WakeStart + 0.3);
        }
        else if (fill2Img != null) fill2Img.Opacity = Ramp(t, WakeStart, WakeStart + 0.4);   // 没有眼睛位置：闭眼图直接淡入成睁眼图
        // 眼睛的蓝光：睁眼那一下猛地亮起，然后退成微微发光一直留着（照视频）
        double kOpen = framed ? EyeOpenK(t) : 0;   // 眼睛睁开了多少
        double eg = framed
            ? (0.75 * kOpen + 0.25 * Ramp(t, EyeOpenEnd - 0.2, EyeOpenEnd + 0.2)) * (1 - 0.315 * Ramp(t, EyeOpenEnd + 0.55, EyeOpenEnd + 1.9))   // 睁多少亮多少（一路匀着变亮，全开前已到七成半），全开那一下冲到最亮，再退成明显的光一直留着
            : awaken
            ? 0.95 * Ramp(t, EyeLight, EyeLight + 0.9) * (1 - 0.6 * Ramp(t, EyeLight + 1.5, EyeLight + 2.7))   // 亮起 → 全图上色后退成微光
            : 0.8 * Ramp(t, WakeStart + 0.05, WakeStart + 0.4) * (1 - 0.6 * Ramp(t, WakeStart + 0.5, WakeEnd + 0.6));
        foreach (var e in eyeGlows) e.Opacity = eg;
        // 眼珠上的亮光芯：睁眼过程中慢慢亮起来（越到后面亮得越快），全开那一下冲到最亮
        double core = (0.65 * Math.Pow(kOpen, 1.5) + 0.35 * Ramp(t, EyeOpenEnd - 0.15, EyeOpenEnd + 0.2)) * (1 - 0.6 * Ramp(t, EyeOpenEnd + 0.55, EyeOpenEnd + 1.9));
        foreach (var e in eyeCores) e.Opacity = core;
        // 洒到脸上的光：也跟着睁眼一点点亮；提亮的眼珠（用的是全开的图）快睁完才出来
        double spill = (0.6 * kOpen + 0.4 * Ramp(t, EyeOpenEnd - 0.2, EyeOpenEnd + 0.25)) * (1 - 0.3 * Ramp(t, EyeOpenEnd + 0.55, EyeOpenEnd + 1.9));
        foreach (var e in eyeBlooms) e.Opacity = spill;
        if (eyeBrightImg != null) eyeBrightImg.Opacity = Ramp(t, EyeOpenEnd - 0.25, EyeOpenEnd + 0.15) * (1 - 0.12 * Ramp(t, EyeOpenEnd + 0.55, EyeOpenEnd + 1.9));
        if (eyeFlares.Length > 0)
        {
            // 睁眼过程中光跟着眼缝走：刚睁一条缝时是贴着闭眼线的一道扁光，随着睁开变高、变宽、升到眼珠正中
            double tall = 0.28 + 0.72 * kOpen;
            // 全开那一下：光晕往外胀三成再收回，一道横光划开后收掉
            double burst = Bell(t, EyeOpenEnd - 0.15, EyeOpenEnd + 0.2, EyeOpenEnd + 0.3, EyeOpenEnd + 1.1);
            eyeHaloScale.ScaleX = (0.8 + 0.2 * kOpen) * (1 + 0.3 * burst);
            eyeHaloScale.ScaleY = tall * (1 + 0.3 * burst);
            eyeCoreScale.ScaleX = 0.85 + 0.15 * kOpen;
            eyeCoreScale.ScaleY = tall;
            eyeBloomScale.ScaleX = eyeBloomScale.ScaleY = 0.55 + 0.45 * kOpen;
            eyeDrop.Y = (1 - kOpen) * eyeDropPx;
            double fl = 0.95 * Bell(t, EyeOpenEnd - 0.08, EyeOpenEnd + 0.12, EyeOpenEnd + 0.3, EyeOpenEnd + 0.9);
            eyeFlareScale.ScaleX = 0.3 + 0.7 * Ramp(t, EyeOpenEnd - 0.08, EyeOpenEnd + 0.3);
            foreach (var e in eyeFlares) e.Opacity = fl;
        }
        if (waveBox != null)
        {
            // 扩散的光晕：完全睁开那一刻从两只眼睛处放出来，沿着人物向四周扩散（先快后慢），边扩边淡；
            // 扫到哪里，哪里的人物被照亮、线稿跟着亮一下
            double u = (t - (EyeOpenEnd + WaveDelay)) / WaveDur;
            bool on = u > 0 && u < 1 && !DbgNoWave;
            waveBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on)
            {
                waveBand.RadiusX = waveBand.RadiusY = waveR0 + (waveR1 - waveR0) * (1 - Math.Pow(1 - u, 1.7));
                waveBox.Opacity = Ramp(u, 0, 0.05) * (1 - u * u);
            }
        }
        double zoom = 1 + 0.03 * Ramp(t, 0, MinEnd);
        artScale.ScaleX = zoom; artScale.ScaleY = zoom;

        // 睁眼
        flash.Opacity = wake * (eyeGlows.Length > 0 ? 0.3 : 0.75);   // 眼睛自己会亮时，脸上的大光淡一点
        double fsc = 0.5 + 0.9 * Ramp(t, WakeStart, WakeEnd);
        flashScale.ScaleX = fsc; flashScale.ScaleY = fsc;

        // 代码
        double codeA = 0.85 * (1 - Ramp(t, OutroStart, OutroStart + 1.4));
        if (codeA > 0 || code.Opacity > 0) UpdateCode(t);
        code.Opacity = codeA;

        // 大标题：淡入 + 从 96% 放大到 100% + 微微上移，然后一道光扫过；副标题晚半秒跟上
        double ta = Ramp(t, TitleStart, TitleStart + 0.8);
        title.Opacity = ta;
        double tsc = 0.96 + 0.04 * ta;
        titleScale.ScaleX = tsc; titleScale.ScaleY = tsc;
        titleShift.Y = (1 - ta) * H * 0.02;
        // 扫光：标题出来后扫一次
        double sp = (t - (TitleStart + 0.7)) / 1.0;   // 扫光位置：-0.2 → 1.2
        titleShine.Opacity = sp > 0 && sp < 1 ? 1 : 0;
        double p = -0.2 + 1.4 * Math.Max(0, Math.Min(1, sp));
        shineA.Offset = p - 0.12; shineB.Offset = p; shineC.Offset = p + 0.12;
        double sa = Ramp(t, SubStart, SubStart + 0.7);
        sub.Opacity = sa;
        subShift.Y = (1 - sa) * H * 0.015;
        double breathe = 0.5 + 0.5 * Math.Sin((t - SubStart) * Math.PI * 2 / 2.4);
        hint.Opacity = Ramp(t, SubStart + 1.0, SubStart + 1.8) * (0.35 + 0.5 * breathe);

        // 光弧
        double arcA = Ramp(t, 0.9, 1.6) * (1 - Ramp(t, 7.0, 8.2));
        bool arcsOn = arcA > 0 || arcsWereOn;   // 光弧淡完以后就不再每帧重算曲线
        arcsWereOn = arcA > 0;
        for (int i = 0; arcsOn && i < arcs.Length; i++)
        {
            ArcDef d = arcs[i];
            double th = d.Phase + d.Speed * t;
            bool fwd = d.Speed > 0;
            arcTail[i].Data = fwd ? Arc(d, th - 150, th) : Arc(d, th, th + 150);
            arcHead[i].Data = fwd ? Arc(d, th - 30, th) : Arc(d, th, th + 30);
            arcHalo[i].Data = arcHead[i].Data;
            arcHalo[i].Opacity = 0.24 * arcA;
            arcTail[i].Opacity = 0.42 * arcA;
            arcHead[i].Opacity = 1.0 * arcA;
        }

        // 流星
        foreach (var m in meteors)
        {
            double k = (t - m.Start) / m.Dur;
            if (k < 0 || k > 1 || t > 9.4) { m.Shape.Visibility = Visibility.Hidden; continue; }
            m.Shape.Visibility = Visibility.Visible;
            Canvas.SetLeft(m.Shape, m.X);
            Canvas.SetTop(m.Shape, -m.Len + k * (H + m.Len));
            m.Shape.Opacity = Math.Min(1, (1 - k) * 3);
        }

        if (waiting)
        {
            int dots = ((int)(t * 2.5)) % 4;
            status.Text = "正在唤醒 Claude" + new string('·', dots);
        }
        else status.Text = "";
    }

    void UpdateCode(double t)
    {
        int budget = (int)Math.Max(0, (t - TypeStart) * CharsPerSec);
        var shown = new List<string>();
        foreach (string line in Lines)
        {
            if (budget <= 0) break;
            if (budget >= line.Length + 5) { shown.Add(line); budget -= line.Length + 5; }   // 每行末尾顿一下
            else { shown.Add(line.Substring(0, Math.Min(line.Length, budget))); budget = 0; }
        }
        code.Inlines.Clear();
        int from = Math.Max(0, shown.Count - maxLines);
        for (int i = from; i < shown.Count; i++)
        {
            if (i > from) code.Inlines.Add(new LineBreak());
            string line = shown[i];
            int ci = line.IndexOf("//", StringComparison.Ordinal);
            if (ci < 0) code.Inlines.Add(new Run(line) { Foreground = CodeBrush });
            else
            {
                if (ci > 0) code.Inlines.Add(new Run(line.Substring(0, ci)) { Foreground = CodeBrush });
                code.Inlines.Add(new Run(line.Substring(ci)) { Foreground = CommentBrush });
            }
        }
    }

    // 睁眼帧的开合程度 0~1：一条连贯的曲线。起步和停住都柔（速度从 0 起、到 0 止），半睁附近放慢但一直在动
    static double EyeOpenK(double t)
    {
        double u = Math.Max(0, Math.Min(1, (t - EyeOpenStart) / (EyeOpenEnd - EyeOpenStart)));
        double w = u + EyeMidSlow * Math.Sin(2 * Math.PI * u) / (2 * Math.PI);   // 中间放慢、两头稍快，始终单调往上
        return Smoother(w);
    }

    static double Smoother(double u)
    {
        u = Math.Max(0, Math.Min(1, u));
        return u * u * u * (u * (u * 6 - 15) + 10);
    }

    // 压暗层的两张图（小图拉伸铺满，都是平滑的渐变）：
    //   hole = 压暗（脸上 DimBase，越往画面边上越暗，最多再加 DimEdge），两只眼睛处留一个柔边的洞
    //   plug = 正好把洞补平的那一块：睁眼前跟 hole 叠着，整张图暗得均匀；睁开时撤掉，眼睛就不压暗了
    // eyes：每只眼 光心 x、y，眼宽，眼高（artBox 里的坐标）
    static void MakeDim(double aw, double ah, double fx, double fy, List<double[]> eyes, out BitmapSource hole, out BitmapSource plug)
    {
        int w = 640, h = Math.Max(2, (int)Math.Round(640 * ah / aw));
        byte[] ph = new byte[w * h * 4], pp = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double u = (x + 0.5) / w, v = (y + 0.5) / h;
                double du = (u - fx) / 0.60, dv = (v - fy) / 0.80;
                double a = DimBase + DimEdge * Ramp(Math.Sqrt(du * du + dv * dv), 0.30, 1.20);
                double win = 0;   // 这一格离眼睛多近：1 = 在眼睛里，0 = 在洞外
                foreach (double[] e in eyes)
                {
                    // 洞只开在眼睛本身：眼珠处全开，到眼眶边上还剩一半，再往外一点就恢复压暗（开大了眼睛周围的皮肤会亮成一圈「眼镜」）
                    double qx = (u * aw - e[0]) / (e[2] * 0.72), qy = (v * ah - e[1]) / (e[3] * 0.82);
                    win = Math.Max(win, 1 - Ramp(Math.Sqrt(qx * qx + qy * qy), 0.40, 1.0));
                }
                double holeA = a * (1 - win);
                double plugA = 1 - (1 - a) / (1 - holeA);   // 两层叠起来透光 (1-holeA)(1-plugA) 正好等于 1-a
                PutDark(ph, (y * w + x) * 4, holeA);
                PutDark(pp, (y * w + x) * 4, plugA);
            }
        hole = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, ph, w * 4);
        plug = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pp, w * 4);
        hole.Freeze(); plug.Freeze();
    }

    // 有「人物蒙版.png」时的压暗层：人物（白）不压暗，人物以外（黑）压暗 DimBack，蒙版的柔边就是过渡
    static BitmapSource MakeDimFromMask(BitmapSource mask)
    {
        var src = new FormatConvertedBitmap(mask, PixelFormats.Gray8, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        byte[] g = new byte[w * h], p = new byte[w * h * 4];
        src.CopyPixels(g, w, 0);
        for (int i = 0; i < g.Length; i++) PutDark(p, i * 4, DimBack * (1 - g[i] / 255.0));
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, p, w * 4);
        bmp.Freeze();
        return bmp;
    }

    static readonly double[] WaveTint = { 1.0, 0.85, 0.68 };   // 光的颜色（蓝、绿、红）：淡蓝

    // 光带照亮人物用的图：整张（完全睁开的）图自己跟自己「滤色」一次，暗处几乎不变、亮处明显更亮，像被光打到，
    // 再掺不到四成淡蓝（光本身的颜色）；
    // 人物以外（蒙版黑处）只留一点点（WaveOutside），没有蒙版就整张都算
    static BitmapSource MakeLit(BitmapSource open, BitmapSource mask)
    {
        var src = new FormatConvertedBitmap(open, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        byte[] p = new byte[w * h * 4], g = null;
        src.CopyPixels(p, w * 4, 0);
        if (mask != null && mask.PixelWidth == w && mask.PixelHeight == h)
        {
            g = new byte[w * h];
            new FormatConvertedBitmap(mask, PixelFormats.Gray8, null, 0).CopyPixels(g, w, 0);
        }
        for (int i = 0; i < w * h; i++)
        {
            double a = g == null ? 1 : WaveOutside + (1 - WaveOutside) * g[i] / 255.0;
            // 光的颜色只掺在本身有点亮度的地方：纯黑的地方（发丝之间露出的夜空、黑裙子的暗处）不掺，不然轮廓外会起一圈灰雾
            double luma = (0.11 * p[i * 4] + 0.59 * p[i * 4 + 1] + 0.30 * p[i * 4 + 2]) / 255.0;
            double k = 0.38 * Ramp(luma, 0.02, 0.13);
            for (int c = 0; c < 3; c++)
            {
                double v = p[i * 4 + c] / 255.0;
                double lit = 1 - (1 - v) * (1 - v);
                p[i * 4 + c] = (byte)Math.Round(255 * (lit * (1 - k) + WaveTint[c] * k) * a);
            }
            p[i * 4 + 3] = (byte)Math.Round(255 * a);
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, p, w * 4);
        bmp.Freeze();
        return bmp;
    }

    // 一格近乎纯黑（带一点蓝）的半透明色，预乘透明度
    static void PutDark(byte[] p, int i, double a)
    {
        a = Math.Max(0, Math.Min(1, a));
        p[i] = (byte)Math.Round(10 * a); p[i + 1] = (byte)Math.Round(5 * a); p[i + 2] = (byte)Math.Round(4 * a); p[i + 3] = (byte)Math.Round(255 * a);
    }

    // 睁开后把眼珠本身提亮的那层：取完全睁开那张图的两只眼睛，自己跟自己做一次「滤色」
    // （黑的睫毛还是黑的，蓝色眼珠明显变亮、花纹还在），眼睛外面全透明。eyesPx：每只眼 光心 x、y，眼宽，眼高（位图像素）
    static BitmapSource MakeEyeBright(BitmapSource open, List<double[]> eyesPx)
    {
        var src = new FormatConvertedBitmap(open, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        byte[] p = new byte[w * h * 4], o = new byte[w * h * 4];
        src.CopyPixels(p, w * 4, 0);
        foreach (double[] e in eyesPx)
        {
            double rx = e[2] * 0.62, ry = e[3] * 0.80;
            int x0 = Math.Max(0, (int)(e[0] - rx)), x1 = Math.Min(w - 1, (int)(e[0] + rx) + 1);
            int y0 = Math.Max(0, (int)(e[1] - ry)), y1 = Math.Min(h - 1, (int)(e[1] + ry) + 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double qx = (x - e[0]) / rx, qy = (y - e[1]) / ry;
                    double a = 0.92 * (1 - Ramp(Math.Sqrt(qx * qx + qy * qy), 0.45, 1.0));
                    if (a <= 0) continue;
                    int i = (y * w + x) * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        double v = p[i + c] / 255.0;
                        double s = 1 - (1 - v) * (1 - v);   // 滤色
                        o[i + c] = (byte)Math.Round(255 * s * a);
                    }
                    o[i + 3] = (byte)Math.Round(255 * a);
                }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, o, w * 4);
        bmp.Freeze();
        return bmp;
    }

    // 二次贝塞尔：要让曲线经过 mid，控制点放在 2*mid - (a+b)/2
    static Point Ctrl(Point a, Point mid, Point b)
    {
        return new Point(2 * mid.X - (a.X + b.X) / 2, 2 * mid.Y - (a.Y + b.Y) / 2);
    }

    static Geometry Arc(ArcDef d, double fromDeg, double toDeg)
    {
        var fig = new PathFigure { StartPoint = d.At(fromDeg), IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(d.At(toDeg), new Size(d.Rx, d.Ry), d.Rot, toDeg - fromDeg > 180, SweepDirection.Clockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }

    static LinearGradientBrush Fade(Point a, Point b, double edge)
    {
        var g = new LinearGradientBrush { StartPoint = a, EndPoint = b };
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), edge));
        return g;
    }

    static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    public static double Ramp(double t, double a, double b)
    {
        double k = (t - a) / (b - a);
        if (k <= 0) return 0;
        if (k >= 1) return 1;
        return k * k * (3 - 2 * k);
    }

    static double Bell(double t, double a, double b, double c, double d)
    {
        return Ramp(t, a, b) * (1 - Ramp(t, c, d));
    }
}

class ArcDef
{
    public double Cx, Cy, Rx, Ry, Rot, Speed, Phase;
    public Color Color;
    public ArcDef(double cx, double cy, double rx, double ry, double rot, double speed, double phase, Color c)
    {
        Cx = cx; Cy = cy; Rx = rx; Ry = ry; Rot = rot; Speed = speed; Phase = phase; Color = c;
    }
    public Point At(double deg)
    {
        double a = deg * Math.PI / 180, r = Rot * Math.PI / 180;
        double x = Rx * Math.Cos(a), y = Ry * Math.Sin(a);
        return new Point(Cx + x * Math.Cos(r) - y * Math.Sin(r), Cy + x * Math.Sin(r) + y * Math.Cos(r));
    }
}

// 一只眼睛的睁眼动画数据（artBox 内坐标）：外眼角 O、内眼角 I、上眼皮最高点 T、下眼皮最低点 B，及上下眼皮控制点
class LidAnim
{
    public Point O, I, T, B, Lc, Uc;
    public Path Lid, Lash, Shadow;
}

class MeteorDef
{
    public double Start, Dur, Len, X;
    public Grid Shape;
}

// ───────────────────────── 图：有「图片.png」就用它，没有就画一个 Claude 星芒 ─────────────────────────
class Art
{
    public BitmapSource Fill, Fill2;
    public const double Crop = 0.006;            // LoadImage 四周各切掉的比例
    public List<BitmapSource> EyeFrames;         // 「睁眼帧」文件夹：闭眼→半睁的几张图（不含完全睁开，完全睁开就是 图片.png）
    public List<double[]> Eyes;                  // 「眼睛.txt」：每只眼 外眼角x,y 内眼角x,y 上眼皮最高x,y 下眼皮最低x,y（原图像素）
    public List<double[]> Pupils;                // 「眼睛.txt」里的「瞳孔=x,y」：每只眼的眼珠中心（跟「眼睛=」同样的顺序，可以不写）
    public BitmapSource Mask;                    // 「人物蒙版.png」：白 = 人物，黑 = 人物以外（可以没有）
    public double EyeImgW, EyeImgH;
    public bool IsDefault, Cover;   // Cover：横图，铺满整个窗口
    public double Aspect, FocusY, FocusX = 0.5;

    // 壁纸用：只认用户放的图，没放就不贴壁纸
    public static BitmapSource LoadWallpaper(string dir)
    {
        string p = FirstExisting(dir, "图片.png", "图片.jpg", "图片.jpeg");
        if (p == null) return null;
        try { return LoadImage(p, 1440); } catch { return null; }
    }

    public static Art Load(string dir)
    {
        var a = new Art();
        string p = FirstExisting(dir, "图片.png", "图片.jpg", "图片.jpeg");
        if (p != null)
        {
            try { a.Fill = LoadImage(p, 900); a.FocusY = 0.38; }
            catch { a.Fill = null; }
        }
        if (a.Fill == null) { a.Fill = DefaultArt(900); a.IsDefault = true; a.FocusY = 0.444; }
        string p2 = a.IsDefault ? null : FirstExisting(dir, "图片2.png", "图片2.jpg", "图片2.jpeg");
        if (p2 != null)
        {
            try { a.Fill2 = LoadImage(p2, 900); } catch { }   // 跟图片.png 同样的解码高度和裁边，两张才能严丝合缝
        }
        if (!a.IsDefault) LoadEyes(a, dir, p);
        if (!a.IsDefault && a.Fill2 == null) LoadEyeFrames(a, dir);
        if (!a.IsDefault)
        {
            // 「人物蒙版.png」：白 = 人物，黑 = 人物以外。有它就只压暗人物以外的地方（跟图片同样的解码和裁边，才对得上）
            string pm = FirstExisting(dir, "人物蒙版.png");
            if (pm != null)
            {
                try
                {
                    BitmapSource mk = LoadImage(pm, 900);
                    if (mk.PixelWidth == a.Fill.PixelWidth && mk.PixelHeight == a.Fill.PixelHeight) a.Mask = mk;
                }
                catch { a.Mask = null; }
            }
        }
        a.Aspect = (double)a.Fill.PixelWidth / a.Fill.PixelHeight;
        a.Cover = !a.IsDefault && a.Aspect >= 1.2;
        if (a.Cover) a.FocusY = 0.5;
        return a;
    }

    // 有「睁眼帧」：描线、上色都用第一张（闭眼），之后按文件名顺序一张张换上去，最后换成 图片.png（完全睁开）
    static void LoadEyeFrames(Art a, string dir)
    {
        try
        {
            string folder = System.IO.Path.Combine(dir, "睁眼帧");
            if (!Directory.Exists(folder)) return;
            var files = new List<string>();
            foreach (string f in Directory.GetFiles(folder))
            {
                string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg") files.Add(f);
            }
            if (files.Count == 0) return;
            files.Sort(StringComparer.Ordinal);
            var frames = new List<BitmapSource>();
            foreach (string f in files)
            {
                BitmapSource b = LoadImage(f, 900);
                if (b.PixelWidth != a.Fill.PixelWidth || b.PixelHeight != a.Fill.PixelHeight) return;   // 尺寸跟 图片.png 对不上就不用
                frames.Add(b);
            }
            frames.Add(a.Fill);
            a.Fill = frames[0];
            frames.RemoveAt(0);
            a.EyeFrames = frames;
        }
        catch { a.EyeFrames = null; }
    }

    static void LoadEyes(Art a, string dir, string imgPath)
    {
        try
        {
            var dec = BitmapDecoder.Create(new Uri(imgPath), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            double ow = dec.Frames[0].PixelWidth, oh = dec.Frames[0].PixelHeight;
            string f = System.IO.Path.Combine(dir, "眼睛.txt");
            if (!File.Exists(f)) return;
            var eyes = new List<double[]>();
            var pupils = new List<double[]>();
            foreach (string raw in File.ReadAllLines(f))
            {
                string line = raw.Trim();
                if (line.StartsWith("瞳孔="))
                {
                    string[] xy = line.Substring(3).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (xy.Length == 2) pupils.Add(new[] { double.Parse(xy[0], CultureInfo.InvariantCulture), double.Parse(xy[1], CultureInfo.InvariantCulture) });
                    continue;
                }
                if (line.StartsWith("图片尺寸="))
                {
                    string[] wh = line.Substring(5).Split(',');
                    a.EyeImgW = double.Parse(wh[0], CultureInfo.InvariantCulture);
                    a.EyeImgH = double.Parse(wh[1], CultureInfo.InvariantCulture);
                }
                else if (line.StartsWith("眼睛="))
                {
                    var nums = new List<double>();
                    foreach (string part in line.Substring(3).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                        nums.Add(double.Parse(part, CultureInfo.InvariantCulture));
                    if (nums.Count == 8) eyes.Add(nums.ToArray());
                }
            }
            // 换了别的图、尺寸对不上，就当没有眼睛位置（不然光会打到别处）
            if (eyes.Count > 0 && a.EyeImgW > 0 && a.EyeImgH > 0 && Math.Abs(ow - a.EyeImgW) < 1 && Math.Abs(oh - a.EyeImgH) < 1)
            {
                a.Eyes = eyes;
                if (pupils.Count == eyes.Count) a.Pupils = pupils;
            }
        }
        catch { a.Eyes = null; }
    }

    static string FirstExisting(string dir, params string[] names)
    {
        foreach (string n in names)
        {
            string f = System.IO.Path.Combine(dir, n);
            if (File.Exists(f)) return f;
        }
        return null;
    }

    static BitmapSource LoadImage(string path, int height)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.UriSource = new Uri(path);
        bi.DecodePixelHeight = height;
        bi.EndInit();
        bi.Freeze();
        // 四周各切掉 0.6%：手机/网上存的图常带圆角或白边
        int cx = (int)(bi.PixelWidth * Crop), cy = (int)(bi.PixelHeight * Crop);
        var cb = new CroppedBitmap(bi, new Int32Rect(cx, cy, bi.PixelWidth - 2 * cx, bi.PixelHeight - 2 * cy));
        cb.Freeze();
        return cb;
    }

    static BitmapSource DefaultArt(int size)
    {
        var orange = Color.FromRgb(0xD9, 0x77, 0x57);
        var ivory = Color.FromRgb(0xF2, 0xEE, 0xE6);
        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            var c = new Point(size * 0.5, size * 0.444);

            var glow = new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(120, orange.R, orange.G, orange.B), 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, orange.R, orange.G, orange.B), 1));
            dc.DrawEllipse(glow, null, c, size * 0.42, size * 0.42);

            // 星轨
            var ring = new Pen(new SolidColorBrush(Color.FromArgb(160, ivory.R, ivory.G, ivory.B)), size * 0.0027);
            double[,] rings = { { 0.445, 0.13, -18 }, { 0.38, 0.105, 24 }, { 0.33, 0.09, 64 } };
            for (int i = 0; i < 3; i++)
            {
                dc.PushTransform(new RotateTransform(rings[i, 2], c.X, c.Y));
                dc.DrawEllipse(null, ring, c, size * rings[i, 0], size * rings[i, 1]);
                dc.Pop();
            }

            // 星芒：12 根长短不一、外头圆、往中间收细的光芒
            double[] jitter = { 0, -6, 5, -3, 7, -5, 2, -7, 4, -2, 6, -4 };
            double[] len = { 0.333, 0.267, 0.356, 0.256, 0.322, 0.289, 0.344, 0.25, 0.311, 0.278, 0.367, 0.261 };
            var rayBrush = new LinearGradientBrush(Color.FromRgb(0xC8, 0x66, 0x47), Color.FromRgb(0xE8, 0x8F, 0x6E), new Point(0, 1), new Point(0, 0));
            double w0 = size * 0.011, w1 = size * 0.038;
            for (int i = 0; i < 12; i++)
            {
                double L = size * len[i];
                var g = Geometry.Parse(string.Format(CultureInfo.InvariantCulture,
                    "M {0},0 L {1},{2} A {3},{3} 0 0 1 {4},{2} L {5},0 Z",
                    -w0 / 2, -w1 / 2, -(L - w1 / 2), w1 / 2, w1 / 2, w0 / 2));
                var tg = new TransformGroup();
                tg.Children.Add(new RotateTransform(i * 30 + jitter[i]));
                tg.Children.Add(new TranslateTransform(c.X, c.Y));
                dc.PushTransform(tg);
                dc.DrawGeometry(rayBrush, null, g);
                dc.Pop();
            }
            dc.DrawEllipse(new SolidColorBrush(orange), null, c, size * 0.036, size * 0.036);

            // 小星星
            var rnd = new Random(5);
            var starBrush = new SolidColorBrush(Color.FromArgb(220, ivory.R, ivory.G, ivory.B));
            for (int i = 0; i < 16; i++)
            {
                double ang = rnd.NextDouble() * Math.PI * 2, dist = size * (0.25 + rnd.NextDouble() * 0.2);
                double s = size * (0.006 + rnd.NextDouble() * 0.01);
                double x = c.X + Math.Cos(ang) * dist, y = c.Y + Math.Sin(ang) * dist * 0.8;
                var star = Geometry.Parse(string.Format(CultureInfo.InvariantCulture,
                    "M {0},{1} Q {0},{0} {2},{0} Q {0},{0} {0},{3} Q {0},{0} {4},{0} Q {0},{0} {0},{1} Z",
                    0, -s * 2, s * 2, s * 2, -s * 2));
                dc.PushTransform(new TranslateTransform(x, y));
                dc.DrawGeometry(starBrush, null, star);
                dc.Pop();
            }

            var ft = new FormattedText("Claude", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Georgia"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size * 0.115, new SolidColorBrush(ivory));
            dc.DrawText(ft, new Point(c.X - ft.Width / 2, size * 0.80));
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}

// ───────────────────────── 霓虹线稿：从图里找出轮廓，按时间一片片亮起来，颜色一直流动 ─────────────────────────
class EdgeLayer
{
    public readonly WriteableBitmap Bmp;
    public readonly double CenterX, CenterY;
    readonly int w, h;
    readonly int[] buf, idx, lut;
    readonly float[] str, rev, hue;

    public EdgeLayer(BitmapSource src, double t0, double t1)
    {
        var conv = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
        w = conv.PixelWidth; h = conv.PixelHeight;
        var px = new int[w * h];
        conv.CopyPixels(px, w * 4, 0);

        // R/G/B 三个通道各算一遍梯度取最大：只看亮度的话，橙色光芒跟背景的差别会被白色字和星星压下去
        var mag = new float[w * h];
        float max = 0;
        for (int ch = 16; ch >= 0; ch -= 8)
        {
            var v = new float[w * h];
            for (int i = 0; i < px.Length; i++) v[i] = ((px[i] >> ch) & 255) / 255f;
            v = Blur3(v, w, h);
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    float gx = v[i - w + 1] + 2 * v[i + 1] + v[i + w + 1] - v[i - w - 1] - 2 * v[i - 1] - v[i + w - 1];
                    float gy = v[i + w - 1] + 2 * v[i + w] + v[i + w + 1] - v[i - w - 1] - 2 * v[i - w] - v[i - w + 1];
                    float m = (float)Math.Sqrt(gx * gx + gy * gy);
                    if (m > mag[i]) mag[i] = m;
                    if (m > max) max = m;
                }
        }

        // 取梯度最强的约 20% 当线条
        var hist = new int[512];
        int nz = 0;
        for (int i = 0; i < mag.Length; i++)
            if (mag[i] > max * 0.02f) { hist[Math.Min(511, (int)(mag[i] / max * 511))]++; nz++; }
        int target = (int)(nz * 0.20), acc = 0, bin = 511;
        for (; bin > 0; bin--) { acc += hist[bin]; if (acc >= target) break; }
        float th = Math.Max(bin / 511f * max, max * 0.04f);

        // 亮起的先后：主要从头顶往下扫（照视频），加一点大块噪声让边缘不死板
        var rnd = new Random(7);
        int cell = 110, gw = w / cell + 2, gh = h / cell + 2;
        var grid = new float[gw * gh];
        for (int i = 0; i < grid.Length; i++) grid[i] = (float)rnd.NextDouble();

        var li = new List<int>(); var ls = new List<float>(); var lr = new List<float>(); var lh = new List<float>();
        float rmin = float.MaxValue, rmax = float.MinValue;
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                float m = mag[i];
                if (m < th) continue;
                float r = 0.35f * Noise(grid, gw, x / (float)cell, y / (float)cell) + 0.65f * (y / (float)h) + (float)rnd.NextDouble() * 0.015f;
                li.Add(i);
                ls.Add(Math.Min(1f, 0.35f + (m - th) / (th * 1.5f)));
                lr.Add(r);
                lh.Add(x / (float)w * 230f + y / (float)h * 60f);   // 左紫、中蓝、右边带金黄（照视频）
                if (r < rmin) rmin = r;
                if (r > rmax) rmax = r;
            }
        idx = li.ToArray(); str = ls.ToArray(); hue = lh.ToArray();

        // 线条的重心（按强度加权，0~1）：人物一般就在这里
        double sx = 0, sy = 0, sw = 0;
        for (int k = 0; k < idx.Length; k++)
        {
            sx += (idx[k] % w) * (double)str[k];
            sy += (idx[k] / w) * (double)str[k];
            sw += str[k];
        }
        CenterX = sw > 0 ? sx / sw / w : 0.5;
        CenterY = sw > 0 ? sy / sw / h : 0.5;
        rev = new float[idx.Length];
        float span = Math.Max(1e-4f, rmax - rmin);
        for (int k = 0; k < rev.Length; k++) rev[k] = (float)(t0 + (lr[k] - rmin) / span * (t1 - t0));

        lut = BuildLut();
        buf = new int[w * h];
        Bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
    }

    public void Render(double t)
    {
        int shift = (int)(t * 12);
        for (int k = 0; k < idx.Length; k++)
        {
            float age = (float)t - rev[k];
            if (age < 0) { buf[idx[k]] = 0; continue; }
            int c = lut[((int)hue[k] + shift) % 360];
            int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
            float a = str[k];
            if (age < 0.18f)
            {
                // 刚描到的那一下发白
                float f = (1 - age / 0.18f) * 0.6f;
                r += (int)((255 - r) * f); g += (int)((255 - g) * f); b += (int)((255 - b) * f);
                a = Math.Min(1f, a + 0.4f * f);
            }
            int A = (int)(a * 255);
            buf[idx[k]] = (A << 24) | ((r * A / 255) << 16) | ((g * A / 255) << 8) | (b * A / 255);
        }
        Bmp.WritePixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
    }

    // 紫 → 淡紫 → 紫 → 蓝紫 → 天蓝 → 紫 → 金 → 淡紫，循环（照视频：紫为主，带蓝和金黄）
    static int[] BuildLut()
    {
        int[] stops = { 0x8F5BFF, 0xB57BFF, 0x9A5CFF, 0x6A7CFF, 0x4FB8FF, 0xA86CFF, 0xFFD36E, 0xC08BFF };
        var lut = new int[360];
        for (int i = 0; i < 360; i++)
        {
            double pos = i / 360.0 * stops.Length;
            int a = (int)pos % stops.Length, b = (a + 1) % stops.Length;
            double f = pos - Math.Floor(pos);
            int ra = (stops[a] >> 16) & 255, ga = (stops[a] >> 8) & 255, ba = stops[a] & 255;
            int rb = (stops[b] >> 16) & 255, gb = (stops[b] >> 8) & 255, bb = stops[b] & 255;
            lut[i] = ((int)(ra + (rb - ra) * f) << 16) | ((int)(ga + (gb - ga) * f) << 8) | (int)(ba + (bb - ba) * f);
        }
        return lut;
    }

    static float Noise(float[] g, int gw, float fx, float fy)
    {
        int x0 = (int)fx, y0 = (int)fy;
        float tx = fx - x0, ty = fy - y0;
        tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
        float a = g[y0 * gw + x0], b = g[y0 * gw + x0 + 1], c = g[(y0 + 1) * gw + x0], d = g[(y0 + 1) * gw + x0 + 1];
        float top = a + (b - a) * tx, bottom = c + (d - c) * tx;
        return top + (bottom - top) * ty;
    }

    static float[] Blur3(float[] src, int w, int h)
    {
        var dst = (float[])src.Clone();
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                dst[i] = (src[i - w - 1] + src[i - w] + src[i - w + 1] + src[i - 1] + src[i] + src[i + 1] + src[i + w - 1] + src[i + w] + src[i + w + 1]) / 9f;
            }
        return dst;
    }
}

// ───────────────────────── 壁纸：贴在 Claude 窗口上的半透明图，鼠标点得穿，跟着窗口走 ─────────────────────────
// 做法：一个透明、点穿、不抢焦点的窗口，「挂」在 Claude 窗口名下（owner），
// 所以它永远紧贴在 Claude 上面一层：别的程序盖住 Claude 时也一起被盖住，Claude 最小化时一起藏起来
class Wallpaper : Window
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("dwmapi")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);

    static Wallpaper current;

    readonly string dir;
    readonly WinEventProc hookProc;
    readonly DispatcherTimer health = new DispatcherTimer();
    readonly WF.NotifyIcon tray = new WF.NotifyIcon();
    readonly double fadeIn;
    IntPtr me, claude, hook;
    double strength;
    bool hiddenByUser, everShown;
    int noClaudeTicks;

    // 有「图片.png」才贴；返回 false 表示没贴
    public static bool Start(string dir, double fadeIn)
    {
        if (current != null) return true;
        BitmapSource img = Art.LoadWallpaper(dir);
        if (img == null) return false;
        current = new Wallpaper(dir, img, fadeIn);
        return true;
    }

    Wallpaper(string dir, BitmapSource img, double fadeIn)
    {
        this.dir = dir;
        this.fadeIn = fadeIn;
        strength = Settings.Strength(dir);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        Title = "Claude 壁纸";
        Left = -32000; Top = -32000; Width = 100; Height = 100;
        Opacity = 0;
        var grid = new Grid { ClipToBounds = true, IsHitTestVisible = false };
        grid.Children.Add(new Image { Source = img, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        Content = grid;

        SourceInitialized += delegate
        {
            me = new WindowInteropHelper(this).Handle;
            // 点穿(0x20) + 不进 Alt-Tab(0x80) + 不抢焦点(0x08000000)
            SetWindowLong(me, -20, GetWindowLong(me, -20) | 0x20 | 0x80 | 0x08000000);
        };
        hookProc = OnWinEvent;
        health.Interval = TimeSpan.FromMilliseconds(400);
        health.Tick += delegate { Track(); };
        health.Start();
        SetupTray();
        Track();
    }

    void Track()
    {
        if (claude == IntPtr.Zero || !IsWindow(claude))
        {
            claude = FindClaude();
            if (claude == IntPtr.Zero)
            {
                if (IsVisible) Hide();
                // Claude 整个退出了（连后台都没了）就收工；只是关到托盘就继续等
                noClaudeTicks = Process.GetProcessesByName("claude").Length == 0 ? noClaudeTicks + 1 : 0;
                if (noClaudeTicks > 12 || (!everShown && noClaudeTicks > 75)) Quit();
                return;
            }
            Attach();
        }
        bool want = IsWindowVisible(claude) && !IsIconic(claude) && !hiddenByUser;
        if (want)
        {
            if (!IsVisible) { Show(); SetWindowLongPtr(me, -8, claude); }
            Follow();
            if (!everShown)
            {
                everShown = true;
                BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, strength, TimeSpan.FromSeconds(fadeIn)));
            }
        }
        else if (IsVisible) Hide();
    }

    void Attach()
    {
        if (me == IntPtr.Zero) { Show(); Hide(); }   // 先把窗口句柄建出来
        SetWindowLongPtr(me, -8, claude);             // 挂到 Claude 名下
        uint pid;
        GetWindowThreadProcessId(claude, out pid);
        if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        hook = SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, hookProc, pid, 0, 0);   // Claude 窗口一挪就跟上
    }

    void OnWinEvent(IntPtr h, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == claude && idObject == 0) Follow();
    }

    void Follow()
    {
        if (me == IntPtr.Zero || claude == IntPtr.Zero) return;
        RECT r;
        if (DwmGetWindowAttribute(claude, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(claude, out r);   // 9=不含隐形边框的真实范围
        SetWindowPos(me, IntPtr.Zero, r.L, r.T, r.R - r.L, r.B - r.T, 0x0010 | 0x0004);   // 不激活、不改层级
    }

    static IntPtr FindClaude()
    {
        foreach (var p in Process.GetProcessesByName("claude"))
        {
            try
            {
                IntPtr h = p.MainWindowHandle;
                if (h != IntPtr.Zero && IsWindowVisible(h)) return h;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return IntPtr.Zero;
    }

    void SetStrength(double v)
    {
        strength = v;
        Settings.SaveStrength(dir, v);
        BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(v, TimeSpan.FromSeconds(0.3)));
    }

    void SetupTray()
    {
        try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); } catch { }
        tray.Text = "Claude 壁纸（右键调浓淡）";
        var menu = new WF.ContextMenuStrip();
        var levels = new WF.ToolStripMenuItem("壁纸浓淡");
        double[] vals = { 0.12, 0.20, 0.30, 0.45 };
        string[] names = { "很淡", "淡", "中", "浓" };
        var items = new List<WF.ToolStripMenuItem>();
        for (int i = 0; i < vals.Length; i++)
        {
            double v = vals[i];
            var it = new WF.ToolStripMenuItem(names[i] + "  " + (int)(v * 100) + "%");
            it.Checked = Math.Abs(v - strength) < 0.005;
            it.Click += delegate
            {
                foreach (var x in items) x.Checked = false;
                it.Checked = true;
                SetStrength(v);
            };
            items.Add(it);
            levels.DropDownItems.Add(it);
        }
        var toggle = new WF.ToolStripMenuItem("暂时隐藏壁纸");
        EventHandler flip = delegate
        {
            hiddenByUser = !hiddenByUser;
            toggle.Text = hiddenByUser ? "显示壁纸" : "暂时隐藏壁纸";
            Track();
        };
        toggle.Click += flip;
        var change = new WF.ToolStripMenuItem("换图片（打开文件夹，换掉「图片.png」）");
        change.Click += delegate { try { Process.Start("explorer.exe", "\"" + dir.TrimEnd('\\') + "\""); } catch { } };
        var quit = new WF.ToolStripMenuItem("关掉壁纸");
        quit.Click += delegate { Quit(); };
        menu.Items.Add(levels);
        menu.Items.Add(toggle);
        menu.Items.Add(change);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(quit);
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += flip;
        tray.Visible = true;
    }

    void Quit()
    {
        health.Stop();
        if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        tray.Visible = false;
        tray.Dispose();
        Application.Current.Shutdown();
    }
}

// 记住壁纸浓淡：exe 旁边的「设置.txt」
static class Settings
{
    const string Key = "壁纸浓度=";

    static string FilePath(string dir) { return System.IO.Path.Combine(dir, "设置.txt"); }

    public static bool WallpaperOn(string dir)
    {
        try
        {
            foreach (string line in File.ReadAllLines(FilePath(dir)))
                if (line.Trim().Replace(" ", "") == "壁纸=开") return true;
        }
        catch { }
        return false;
    }

    public static double Strength(string dir)
    {
        try
        {
            foreach (string line in File.ReadAllLines(FilePath(dir)))
            {
                double v;
                if (line.StartsWith(Key) && double.TryParse(line.Substring(Key.Length).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    return Math.Max(0.03, Math.Min(0.9, v));
            }
        }
        catch { }
        return 0.20;
    }

    public static void SaveStrength(string dir, double v)
    {
        try
        {
            File.WriteAllText(FilePath(dir), "# Claude 壁纸设置（托盘图标右键也能改）\r\n" + Key + v.ToString("0.00", CultureInfo.InvariantCulture) + "\r\n", new System.Text.UTF8Encoding(true));
        }
        catch { }
    }
}
// ───────────────────────── 找眼睛：闭眼图和睁眼图差别大的地方就是眼睛，取最大的两块（0~1 的相对坐标） ─────────────────────────
static class EyeFinder
{
    public static List<Rect> Find(BitmapSource closed, BitmapSource open)
    {
        return Find(closed, open, 90, 4, 0);
    }

    // pixThresh：一个像素 RGB 差之和超过多少算变了；cellDiv：一格里超过 1/cellDiv 的像素变了才算；grow：最后每边多扩几格
    public static List<Rect> Find(BitmapSource closed, BitmapSource open, int pixThresh, int cellDiv, int grow)
    {
        var res = new List<Rect>();
        var ca = new FormatConvertedBitmap(closed, PixelFormats.Bgra32, null, 0);
        var cb = new FormatConvertedBitmap(open, PixelFormats.Bgra32, null, 0);
        int w = ca.PixelWidth, h = ca.PixelHeight;
        if (cb.PixelWidth != w || cb.PixelHeight != h) return res;   // 两张图大小不一样就不找了
        var pa = new int[w * h];
        var pb = new int[w * h];
        ca.CopyPixels(pa, w * 4, 0);
        cb.CopyPixels(pb, w * 4, 0);

        const int cell = 6;
        int gw = w / cell, gh = h / cell;
        var hot = new bool[gw * gh];
        for (int gy = 0; gy < gh; gy++)
            for (int gx = 0; gx < gw; gx++)
            {
                int cnt = 0;
                for (int y = gy * cell; y < gy * cell + cell; y++)
                    for (int x = gx * cell; x < gx * cell + cell; x++)
                    {
                        int p = pa[y * w + x], q = pb[y * w + x];
                        int d = Math.Abs(((p >> 16) & 255) - ((q >> 16) & 255)) + Math.Abs(((p >> 8) & 255) - ((q >> 8) & 255)) + Math.Abs((p & 255) - (q & 255));
                        if (d > pixThresh) cnt++;
                    }
                hot[gy * gw + gx] = cnt > cell * cell / cellDiv;
            }

        // 连成片（上下左右相邻），记每片的范围和大小
        var seen = new bool[gw * gh];
        var blobs = new List<int[]>();   // minX, minY, maxX, maxY, count
        var stack = new Stack<int>();
        for (int k = 0; k < hot.Length; k++)
        {
            if (!hot[k] || seen[k]) continue;
            int[] b = { int.MaxValue, int.MaxValue, -1, -1, 0 };
            seen[k] = true;
            stack.Push(k);
            while (stack.Count > 0)
            {
                int c = stack.Pop(), cx = c % gw, cy = c / gw;
                b[0] = Math.Min(b[0], cx); b[1] = Math.Min(b[1], cy); b[2] = Math.Max(b[2], cx); b[3] = Math.Max(b[3], cy); b[4]++;
                int[] nb = { cx > 0 ? c - 1 : -1, cx < gw - 1 ? c + 1 : -1, cy > 0 ? c - gw : -1, cy < gh - 1 ? c + gw : -1 };
                foreach (int n in nb)
                    if (n >= 0 && hot[n] && !seen[n]) { seen[n] = true; stack.Push(n); }
            }
            blobs.Add(b);
        }
        blobs.Sort((x, y) => y[4].CompareTo(x[4]));
        for (int i = 0; i < blobs.Count && i < 2; i++)
        {
            int[] b = blobs[i];
            if (b[4] < 6) break;   // 太小的是噪点
            int x0 = Math.Max(0, b[0] - grow), y0 = Math.Max(0, b[1] - grow), x1 = Math.Min(gw - 1, b[2] + grow), y1 = Math.Min(gh - 1, b[3] + grow);
            res.Add(new Rect(x0 * cell / (double)w, y0 * cell / (double)h, (x1 - x0 + 1) * cell / (double)w, (y1 - y0 + 1) * cell / (double)h));
        }
        return res;
    }
}