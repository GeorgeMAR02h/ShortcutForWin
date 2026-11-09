using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace TaskbarFlyout;

// ───────────────────────── Entrada do programa ─────────────────────────
static class Program
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "TaskbarFlyout.SingleInstance", out bool created);
        if (!created) return;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var store = new Store();
        var flyout = new FlyoutWindow(store);
        using var hook = new MouseHook();

        // Botão do meio sobre a barra de tarefas -> descobre qual ícone e mostra o flyout
        app.DispatcherUnhandledException += (_, e) => { Log.Write("ERRO: " + e.Exception); e.Handled = true; };
        Log.Write("Programa iniciado");

        hook.MiddleDown += (x, y) =>
        {
            Log.Write($"Botão do meio na barra em {x},{y}");
            Task.Run(() => TaskbarButton.At(x, y)).ContinueWith(t =>
            {
                var info = t.IsFaulted ? null : t.Result;
                if (info == null || Exclusions.IsExcluded(info))
                {
                    Log.Write(info == null
                        ? "  => botão não reconhecido, repassando o clique ao Windows"
                        : $"  => '{info.Name}' está na lista de exceções, repassando o clique ao Windows");
                    MouseHook.ReplayMiddleClick();
                    return;
                }
                app.Dispatcher.BeginInvoke(() => flyout.Toggle(info));
            });
        };

        // Clique em qualquer outro lugar fecha o flyout
        hook.OtherDown += (x, y) => app.Dispatcher.BeginInvoke(() => flyout.OutsideClick(x, y));

        // Ícone na bandeja
        var tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Taskbar Flyout",
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
        };
        var auto = new System.Windows.Forms.ToolStripMenuItem("Iniciar com o Windows")
        { CheckOnClick = true, Checked = GetAutoStart() };
        auto.CheckedChanged += (_, _) => SetAutoStart(auto.Checked);
        var exit = new System.Windows.Forms.ToolStripMenuItem("Sair");
        exit.Click += (_, _) => app.Shutdown();
        tray.ContextMenuStrip.Items.Add(auto);
        tray.ContextMenuStrip.Items.Add(exit);

        app.Run();
        tray.Visible = false;
        tray.Dispose();
    }

    // Ícone da bandeja: usa o mouse_icon.ico embutido no programa
    static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream("mouse_icon.ico");
            if (stream != null)
                return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    static bool GetAutoStart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("TaskbarFlyout") != null;
    }

    static void SetAutoStart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k == null) return;
        if (on) k.SetValue("TaskbarFlyout", "\"" + Environment.ProcessPath + "\"");
        else k.DeleteValue("TaskbarFlyout", false);
    }
}

// ───────────────────────── Apps que mantêm o comportamento padrão do Windows ─────────────────────────
static class Exclusions
{
    // Explorador de Arquivos: o botão do meio continua abrindo uma nova janela, como no Windows
    static readonly string[] Defaults =
    {
        "Microsoft.Windows.Explorer", "explorer.exe",
        "File Explorer", "Explorador de Arquivos", "Windows Explorer"
    };

    static readonly string UserFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskbarFlyout", "excluded.txt");

    public static bool IsExcluded(ButtonInfo b)
    {
        var terms = new List<string>(Defaults);
        try
        {
            if (File.Exists(UserFile))
                terms.AddRange(File.ReadAllLines(UserFile)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("#")));
        }
        catch { }

        foreach (var t in terms)
            if (b.Key.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                b.Name.Contains(t, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

// ───────────────────────── Armazenamento (JSON) ─────────────────────────
sealed class Store
{
    readonly string file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskbarFlyout", "groups.json");
    Dictionary<string, List<string>> data = new();

    public Store()
    {
        try
        {
            if (File.Exists(file))
                data = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file)) ?? new();
        }
        catch { data = new(); }
    }

    public List<string> Get(string key) => data.TryGetValue(key, out var l) ? new List<string>(l) : new();

    public void Add(string key, string path)
    {
        if (!data.TryGetValue(key, out var l)) data[key] = l = new List<string>();
        if (!l.Contains(path, StringComparer.OrdinalIgnoreCase)) l.Add(path);
        Save();
    }

    public void Remove(string key, string path)
    {
        if (data.TryGetValue(key, out var l)) { l.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)); Save(); }
    }

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(data));
    }
}

// ───────────────────────── Hook global do mouse ─────────────────────────
sealed class MouseHook : IDisposable
{
    const int WH_MOUSE_LL = 14;
    const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? win);
    [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? win);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    // ── reenvio do clique do meio (para apps excluídos) ──
    const long Marker = 0x54464C59; // "TFLY": identifica cliques gerados por este programa
    const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public MOUSEINPUT mi; }
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] inputs, int size);

    public static void ReplayMiddleClick()
    {
        var inputs = new INPUT[2];
        inputs[0].type = 0; inputs[0].mi.dwFlags = MOUSEEVENTF_MIDDLEDOWN; inputs[0].mi.dwExtraInfo = (IntPtr)Marker;
        inputs[1].type = 0; inputs[1].mi.dwFlags = MOUSEEVENTF_MIDDLEUP;   inputs[1].mi.dwExtraInfo = (IntPtr)Marker;
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    public event Action<int, int>? MiddleDown;
    public event Action<int, int>? OtherDown;

    readonly HookProc proc;
    IntPtr handle;
    bool swallowUp;

    public MouseHook()
    {
        proc = Callback; // guarda referência para o GC não coletar
        using var m = Process.GetCurrentProcess().MainModule!;
        handle = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(m.ModuleName), 0);
    }

    IntPtr Callback(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            int msg = (int)w;
            var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l);
            if (s.extra == (IntPtr)Marker) return CallNextHookEx(handle, code, w, l); // clique reenviado: deixa passar

            if (msg == WM_MBUTTONDOWN && OverTaskbar(s.pt))
            {
                swallowUp = true;
                MiddleDown?.Invoke(s.pt.X, s.pt.Y);
                return (IntPtr)1; // engole o clique para o Windows não abrir nova instância
            }
            if (msg == WM_MBUTTONDOWN) Log.Write($"Botão do meio FORA da barra em {s.pt.X},{s.pt.Y}");
            if (msg == WM_MBUTTONUP && swallowUp)
            {
                swallowUp = false;
                return (IntPtr)1;
            }
            if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
                OtherDown?.Invoke(s.pt.X, s.pt.Y);
        }
        return CallNextHookEx(handle, code, w, l);
    }

    static bool OverTaskbar(POINT p)
    {
        // barra principal e barras de monitores secundários
        foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            IntPtr h = FindWindow(cls, null);
            while (h != IntPtr.Zero)
            {
                if (GetWindowRect(h, out var r) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom)
                    return true;
                h = cls == "Shell_SecondaryTrayWnd" ? FindWindowEx(IntPtr.Zero, h, cls, null) : IntPtr.Zero;
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero) { UnhookWindowsHookEx(handle); handle = IntPtr.Zero; }
    }
}

// ───────────── Descobre qual botão da barra de tarefas foi clicado (UI Automation) ─────────────
sealed record ButtonInfo(string Key, string Name, Rect Bounds);

static class Log
{
    static readonly string dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarFlyout");
    static readonly string file = Path.Combine(dir, "log.txt");
    static readonly string flag = Path.Combine(dir, "debug.flag");

    // O log só é gravado se existir o arquivo debug.flag na pasta de dados
    public static readonly bool Enabled = File.Exists(flag);

    static Log()
    {
        try
        {
            if (!File.Exists(file)) return;
            // desligado: remove o log antigo; ligado: recomeça se passou de ~500 KB
            if (!Enabled || new FileInfo(file).Length > 500_000) File.Delete(file);
        }
        catch { }
    }

    public static void Write(string s)
    {
        if (!Enabled) return;
        try
        {
            Directory.CreateDirectory(dir);
            File.AppendAllText(file, DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine);
        }
        catch { }
    }
}

static class TaskbarButton
{
    public static ButtonInfo? At(int x, int y)
    {
        try
        {
            // 1) Caminho rápido (funciona em algumas versões do Windows)
            var walker = TreeWalker.RawViewWalker;
            var el = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            for (int i = 0; el != null && i < 8; i++)
            {
                var c = el.Current;
                Log.Write($"  [{i}] class='{c.ClassName}' tipo={c.ControlType.ProgrammaticName} id='{c.AutomationId}' nome='{c.Name}'");
                bool isTaskButton =
                    c.ClassName.Contains("TaskListButton") ||
                    c.AutomationId.StartsWith("Appid:", StringComparison.OrdinalIgnoreCase) ||
                    (c.ControlType == ControlType.Button && !string.IsNullOrWhiteSpace(c.Name));
                if (isTaskButton) return Make(c.Name, c.AutomationId, c.BoundingRectangle);
                el = walker.GetParent(el);
            }

            // 2) Windows 11: varre os elementos dentro da barra e vê qual contém o clique
            return FromTaskbarTree(x, y);
        }
        catch (Exception ex) { Log.Write("  ERRO UIA: " + ex.Message); }
        return null;
    }

    static ButtonInfo? Make(string? rawName, string? id, Rect bounds)
    {
        string name = Regex.Replace(rawName ?? "", @"\s[-–—]\s.*$", "").Trim();
        string key = !string.IsNullOrWhiteSpace(id) ? id! : name;
        if (string.IsNullOrWhiteSpace(key)) return null;
        Log.Write($"  => botão reconhecido: key='{key}' nome='{name}' bounds={bounds}");
        return new ButtonInfo(key, name, bounds);
    }

    static ButtonInfo? FromTaskbarTree(int x, int y)
    {
        var pt = new System.Windows.Point(x, y);

        // acha a barra de tarefas (principal ou de outro monitor) que contém o ponto
        var trays = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new OrCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_SecondaryTrayWnd")));

        AutomationElement? root = null;
        foreach (AutomationElement t in trays)
            if (t.Current.BoundingRectangle.Contains(pt)) { root = t; break; }
        if (root == null) { Log.Write("  => barra de tarefas não encontrada via UIA"); return null; }

        var req = new CacheRequest { TreeScope = TreeScope.Element };
        req.Add(AutomationElement.NameProperty);
        req.Add(AutomationElement.ClassNameProperty);
        req.Add(AutomationElement.AutomationIdProperty);
        req.Add(AutomationElement.BoundingRectangleProperty);
        req.Add(AutomationElement.ControlTypeProperty);

        AutomationElementCollection all;
        using (req.Activate())
            all = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);

        Log.Write($"  varredura: {all.Count} elementos dentro da barra");

        AutomationElement? best = null;
        double bestArea = double.MaxValue;
        foreach (AutomationElement e in all)
        {
            var c = e.Cached;
            var r = c.BoundingRectangle;
            if (r.IsEmpty || !r.Contains(pt)) continue;
            Log.Write($"  candidato: class='{c.ClassName}' tipo={c.ControlType.ProgrammaticName} id='{c.AutomationId}' nome='{c.Name}' rect={r}");
            if (string.IsNullOrWhiteSpace(c.Name) && string.IsNullOrWhiteSpace(c.AutomationId)) continue;
            double area = r.Width * r.Height;
            if (area < bestArea) { best = e; bestArea = area; }
        }

        if (best != null)
        {
            var c = best.Cached;
            return Make(c.Name, c.AutomationId, c.BoundingRectangle);
        }

        // nada achado: lista alguns elementos para diagnóstico
        int n = 0;
        foreach (AutomationElement e in all)
        {
            var c = e.Cached;
            Log.Write($"  elemento: class='{c.ClassName}' tipo={c.ControlType.ProgrammaticName} id='{c.AutomationId}' nome='{c.Name}' rect={c.BoundingRectangle}");
            if (++n >= 25) break;
        }
        Log.Write("  => nenhum botão da barra reconhecido");
        return null;
    }
}

// ───────────────────────── Cores do sistema (tema / cor de destaque) ─────────────────────────
static class SystemTheme
{
    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    const string Accent = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    const string Dwm = @"Software\Microsoft\Windows\DWM";

    public static Color Background, Foreground, Hover, Edge;

    public static void Refresh()
    {
        bool lightTheme = ReadDword(Personalize, "SystemUsesLightTheme") == 1;
        bool accentOnTaskbar = ReadDword(Personalize, "ColorPrevalence") == 1;
        bool transparency = ReadDword(Personalize, "EnableTransparency") != 0;
        byte alpha = (byte)(transparency ? 0xE6 : 0xFF);

        Color bg;
        if (accentOnTaskbar)
        {
            // mesma cor que o Windows usa na barra de tarefas quando a cor de destaque está ativada
            bg = ReadAbgr(Accent, "StartColorMenu")
                 ?? ReadAbgr(Dwm, "AccentColor")
                 ?? SystemParameters.WindowGlassColor;
        }
        else if (lightTheme) bg = Color.FromRgb(0xF3, 0xF3, 0xF3);
        else bg = Color.FromRgb(0x20, 0x20, 0x20);

        double lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        bool darkBg = lum < 0.6;

        Background = Color.FromArgb(alpha, bg.R, bg.G, bg.B);
        Foreground = darkBg ? Colors.White : Colors.Black;
        Hover = darkBg ? Color.FromArgb(0x33, 255, 255, 255) : Color.FromArgb(0x22, 0, 0, 0);
        Edge = darkBg ? Color.FromArgb(0x40, 255, 255, 255) : Color.FromArgb(0x30, 0, 0, 0);
    }

    static int? ReadDword(string path, string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(path);
            return k?.GetValue(name) is int v ? v : null;
        }
        catch { return null; }
    }

    // o Windows guarda as cores como 0xAABBGGRR
    static Color? ReadAbgr(string path, string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(path);
            if (k?.GetValue(name) is not int raw) return null;
            uint v = unchecked((uint)raw);
            return Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
        }
        catch { return null; }
    }
}

// ───────────────────────── Janelinha (flyout) ─────────────────────────
sealed class FlyoutWindow : Window
{
    static readonly Style BtnStyle = (Style)XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
  <Setter Property='Template'><Setter.Value>
    <ControlTemplate TargetType='Button'>
      <Border x:Name='b' Background='Transparent' CornerRadius='6' Width='44' Height='44'>
        <ContentPresenter x:Name='cp' HorizontalAlignment='Center' VerticalAlignment='Center'
                          RenderTransformOrigin='0.5,0.5'>
          <ContentPresenter.RenderTransform>
            <ScaleTransform x:Name='sc' ScaleX='1' ScaleY='1'/>
          </ContentPresenter.RenderTransform>
          <ContentPresenter.Effect>
            <DropShadowEffect x:Name='sh' Color='Black' BlurRadius='12' ShadowDepth='6' Direction='270' Opacity='0'/>
          </ContentPresenter.Effect>
        </ContentPresenter>
      </Border>
      <ControlTemplate.Triggers>

        <Trigger Property='IsMouseOver' Value='True'>
          <Setter TargetName='b' Property='Background' Value='{DynamicResource HoverBrush}'/>
          <Trigger.EnterActions>
            <BeginStoryboard>
              <Storyboard>
                <DoubleAnimation Storyboard.TargetName='sc' Storyboard.TargetProperty='ScaleX' To='1.25' Duration='0:0:0.16'>
                  <DoubleAnimation.EasingFunction><BackEase EasingMode='EaseOut' Amplitude='0.6'/></DoubleAnimation.EasingFunction>
                </DoubleAnimation>
                <DoubleAnimation Storyboard.TargetName='sc' Storyboard.TargetProperty='ScaleY' To='1.25' Duration='0:0:0.16'>
                  <DoubleAnimation.EasingFunction><BackEase EasingMode='EaseOut' Amplitude='0.6'/></DoubleAnimation.EasingFunction>
                </DoubleAnimation>
                <DoubleAnimation Storyboard.TargetName='sh' Storyboard.TargetProperty='Opacity' To='0.65' Duration='0:0:0.16'/>
              </Storyboard>
            </BeginStoryboard>
          </Trigger.EnterActions>
          <Trigger.ExitActions>
            <BeginStoryboard>
              <Storyboard>
                <DoubleAnimation Storyboard.TargetName='sc' Storyboard.TargetProperty='ScaleX' To='1' Duration='0:0:0.14'/>
                <DoubleAnimation Storyboard.TargetName='sc' Storyboard.TargetProperty='ScaleY' To='1' Duration='0:0:0.14'/>
                <DoubleAnimation Storyboard.TargetName='sh' Storyboard.TargetProperty='Opacity' To='0' Duration='0:0:0.14'/>
              </Storyboard>
            </BeginStoryboard>
          </Trigger.ExitActions>
        </Trigger>

        <Trigger Property='IsPressed' Value='True'>
          <Trigger.EnterActions>
            <BeginStoryboard>
              <Storyboard>
                <DoubleAnimation Storyboard.TargetName='sc' Storyboard.TargetProperty='ScaleX' To='0.92' Duration='0:0:0.07'/>
                <DoubleAnimation Storyboard.TargetName='sc' Storyboard.TargetProperty='ScaleY' To='0.92' Duration='0:0:0.07'/>
              </Storyboard>
            </BeginStoryboard>
          </Trigger.EnterActions>
          <Trigger.ExitActions>
            <BeginStoryboard>
              <Storyboard>
                <DoubleAnimation Storyboard.TargetName='sc' Storyboard.TargetProperty='ScaleX' To='1.25' Duration='0:0:0.12'/>
                <DoubleAnimation Storyboard.TargetName='sc' Storyboard.TargetProperty='ScaleY' To='1.25' Duration='0:0:0.12'/>
              </Storyboard>
            </BeginStoryboard>
          </Trigger.ExitActions>
        </Trigger>

      </ControlTemplate.Triggers>
    </ControlTemplate>
  </Setter.Value></Setter>
</Style>");

    readonly Store store;
    readonly StackPanel list = new() { Orientation = Orientation.Vertical };
    readonly Button pinBtn;
    Border card = null!;
    Border sep = null!;
    TextBlock addGlyph = null!;
    Button addBtn = null!;
    readonly TranslateTransform shift = new();
    bool dismissing;
    int dismissToken;
    readonly TextBlock pinGlyph;
    string key = "";
    Rect anchor;
    bool busy;
    public bool Pinned { get; private set; }

    public FlyoutWindow(Store s)
    {
        store = s;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        AllowDrop = true;
        ShowActivated = false;

        var add = MakeButton("\uE710", out addGlyph);
        addBtn = add;
        add.ToolTip = "Adicionar programa";
        add.Click += (_, _) => PickProgram();

        pinBtn = MakeButton("\uE718", out pinGlyph);
        pinBtn.ToolTip = "Manter aberto (útil para arrastar arquivos)";
        pinBtn.Click += (_, _) => SetPinned(!Pinned);

        var panel = new StackPanel();
        panel.Children.Add(list);
        sep = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
        panel.Children.Add(sep);
        panel.Children.Add(add);
        panel.Children.Add(pinBtn);

        card = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.5 },
            Child = panel
        };
        card.RenderTransform = shift;
        Content = card;
        ApplyTheme();
    }

    static Button MakeButton(string glyph, out TextBlock tb)
    {
        tb = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = Brushes.White
        };
        return new Button { Style = BtnStyle, Content = tb };
    }

    void ApplyTheme()
    {
        SystemTheme.Refresh();
        var fg = new SolidColorBrush(SystemTheme.Foreground);
        card.Background = new SolidColorBrush(SystemTheme.Background);
        card.BorderBrush = new SolidColorBrush(SystemTheme.Edge);
        sep.Background = new SolidColorBrush(SystemTheme.Edge);
        addGlyph.Foreground = fg;
        pinGlyph.Foreground = fg;
        Resources["HoverBrush"] = new SolidColorBrush(SystemTheme.Hover);
    }

    void SetPinned(bool v)
    {
        Pinned = v;
        pinGlyph.Text = v ? "\uE840" : "\uE718";
    }

    // ── abrir / fechar ──
    public void Toggle(ButtonInfo info)
    {
        if (IsVisible && !dismissing && key == info.Key) { Dismiss(); return; }

        dismissToken++;          // cancela um fechamento em andamento
        dismissing = false;
        key = info.Key;
        anchor = info.Bounds;
        SetPinned(false);
        ApplyTheme();
        Rebuild();
        Reposition();

        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Show();
        PlayOpen();
    }

    // Abertura: a caixa sobe da barra de tarefas e os ícones aparecem de baixo para cima
    void PlayOpen()
    {
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        shift.BeginAnimation(TranslateTransform.YProperty, null);
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = easeOut });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));

        // do mais próximo da barra de tarefas (embaixo) até o mais distante (em cima)
        var ordered = new List<UIElement> { pinBtn, addBtn };
        for (int i = list.Children.Count - 1; i >= 0; i--) ordered.Add(list.Children[i]);

        for (int i = 0; i < ordered.Count; i++)
        {
            var el = ordered[i];
            var delay = TimeSpan.FromMilliseconds(50 + i * 45);

            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = 0;
            var tt = new TranslateTransform(0, 20);
            el.RenderTransform = tt;

            el.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = delay });
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(280))
                {
                    BeginTime = delay,
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }
                });
        }
    }

    // Fechamento: desce um pouco e some
    void Dismiss()
    {
        if (!IsVisible || dismissing) return;
        dismissing = true;
        int token = ++dismissToken;

        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(16, TimeSpan.FromMilliseconds(140))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) =>
        {
            if (token != dismissToken) return; // foi reaberto no meio do caminho
            dismissing = false;
            Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    public void OutsideClick(int x, int y)
    {
        if (!IsVisible || dismissing || Pinned || busy) return;
        var d = VisualTreeHelper.GetDpi(this);
        double px = x / d.DpiScaleX, py = y / d.DpiScaleY;
        if (px >= Left && px <= Left + ActualWidth && py >= Top && py <= Top + ActualHeight) return;
        Dismiss();
    }

    void Reposition()
    {
        var root = (FrameworkElement)Content;
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = root.DesiredSize;
        var d = VisualTreeHelper.GetDpi(this);
        double cx = (anchor.Left + anchor.Width / 2) / d.DpiScaleX;
        double top = anchor.Top / d.DpiScaleY;
        var wa = SystemParameters.WorkArea;
        Left = Math.Clamp(cx - sz.Width / 2, wa.Left, Math.Max(wa.Left, wa.Right - sz.Width));
        Top = top - sz.Height + 6; // 12 de margem (sombra) - 6 de folga
    }

    // ── conteúdo ──
    void Rebuild()
    {
        list.Children.Clear();
        foreach (var path in store.Get(key)) list.Children.Add(MakeItem(path));
    }

    Button MakeItem(string path)
    {
        var icon = IconFor(path);
        var b = new Button
        {
            Style = BtnStyle,
            ToolTip = Directory.Exists(path) ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path),
            Content = icon != null
                ? new System.Windows.Controls.Image { Source = icon, Width = 32, Height = 32 }
                : new TextBlock { Text = "📁", FontSize = 24, Foreground = new SolidColorBrush(SystemTheme.Foreground) }
        };
        b.Click += (_, _) => { Launch(path); if (!Pinned) Dismiss(); };

        var menu = new ContextMenu();
        var rm = new MenuItem { Header = "Remover" };
        rm.Click += (_, _) => { store.Remove(key, path); Rebuild(); Reposition(); };
        menu.Items.Add(rm);
        menu.Opened += (_, _) => busy = true;
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(() => busy = false);
        b.ContextMenu = menu;
        return b;
    }

    void PickProgram()
    {
        busy = true;
        try
        {
            var w = new PickerWindow();
            if (w.ShowDialog() == true && w.Result != null)
            {
                store.Add(key, w.Result);
                Rebuild();
                Reposition();
            }
        }
        finally { busy = false; }
    }

    // ── arrastar e soltar ──
    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files) store.Add(key, f);
            Rebuild();
            Reposition();
        }
        e.Handled = true;
    }

    // ── utilidades ──
    static void Launch(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Taskbar Flyout"); }
    }

    public static ImageSource? IconFor(string path)
    {
        try
        {
            using var ic = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ic == null) return null;
            var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze();
            return bs;
        }
        catch { return null; }
    }
}

// ───────────────────────── Seletor de programas instalados ─────────────────────────
sealed class Entry
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    ImageSource? icon;
    bool loaded;
    public ImageSource? Icon
    {
        get
        {
            if (!loaded) { loaded = true; icon = FlyoutWindow.IconFor(FullPath); }
            return icon;
        }
    }
}

sealed class PickerWindow : Window
{
    public string? Result;
    readonly List<Entry> all;
    readonly ListBox box = new();

    public PickerWindow()
    {
        Title = "Escolher programa";
        Width = 380; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        var bgc = SystemTheme.Background;
        Background = new SolidColorBrush(Color.FromRgb(bgc.R, bgc.G, bgc.B));
        Foreground = new SolidColorBrush(SystemTheme.Foreground);

        all = Scan();

        var search = new TextBox { Margin = new Thickness(8, 8, 8, 4), Padding = new Thickness(6) };
        var browse = new Button { Content = "Procurar arquivo…", Margin = new Thickness(8, 0, 8, 8), Padding = new Thickness(6) };

        box.ItemTemplate = (DataTemplate)XamlReader.Parse(@"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <StackPanel Orientation='Horizontal' Margin='2'>
    <Image Source='{Binding Icon}' Width='22' Height='22'/>
    <TextBlock Text='{Binding Name}' Foreground='{Binding Foreground, RelativeSource={RelativeSource AncestorType=ListBox}}' Margin='10,0,0,0' VerticalAlignment='Center'/>
  </StackPanel>
</DataTemplate>");
        box.Background = Brushes.Transparent;
        box.Foreground = Foreground;
        box.BorderThickness = new Thickness(0);
        box.ItemsSource = all;
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Disabled);

        search.TextChanged += (_, _) =>
            box.ItemsSource = all.Where(e => e.Name.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)).ToList();

        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is Entry en) { Result = en.FullPath; DialogResult = true; }
        };

        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "Programas e atalhos|*.exe;*.lnk;*.bat|Todos os arquivos|*.*" };
            if (dlg.ShowDialog(this) == true) { Result = dlg.FileName; DialogResult = true; }
        };

        var dock = new DockPanel();
        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(browse, Dock.Top);
        dock.Children.Add(search);
        dock.Children.Add(browse);
        dock.Children.Add(box);
        Content = dock;

        Loaded += (_, _) => search.Focus();
    }

    static List<Entry> Scan()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };
        var dict = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var r in roots)
        {
            var dir = Path.Combine(r, "Programs");
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.lnk", opt))
                {
                    var n = Path.GetFileNameWithoutExtension(f);
                    if (n.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
                        n.StartsWith("Desinstalar", StringComparison.OrdinalIgnoreCase)) continue;
                    dict.TryAdd(n, new Entry { Name = n, FullPath = f });
                }
            }
            catch { }
        }
        return dict.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
