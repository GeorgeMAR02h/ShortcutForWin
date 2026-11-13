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
    const string RunName = "ShortcutForWin";
    const string OldRunName = "TaskbarFlyout"; // nome usado nas versões anteriores

    // Se o início automático estava ligado com o nome antigo, passa para o novo e remove o antigo
    static void MigrateAutoStart()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k?.GetValue(OldRunName) == null) return;
            k.DeleteValue(OldRunName, false);
            k.SetValue(RunName, "\"" + Environment.ProcessPath + "\"");
        }
        catch { }
    }

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "ShortcutForWin.SingleInstance", out bool created);
        if (!created)
        {
            // já existe uma cópia rodando: avisa em vez de sair em silêncio
            MessageBox.Show(L.T("AlreadyRunning"), "Shortcut for Win", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        MigrateAutoStart();
        DarkMenus.Enable();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var store = new Store();
        var flyout = new FlyoutWindow(store);
        MemoryTrimmer.Schedule(5); // limpa a memória logo depois de iniciar
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
            Text = "Shortcut for Win",
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
        };
        var auto = new System.Windows.Forms.ToolStripMenuItem(L.T("StartWithWindows"))
        { CheckOnClick = true, Checked = GetAutoStart() };
        auto.CheckedChanged += (_, _) => SetAutoStart(auto.Checked);
        var exit = new System.Windows.Forms.ToolStripMenuItem(L.T("Exit"));
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
        return k?.GetValue(RunName) != null;
    }

    static void SetAutoStart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k == null) return;
        if (on) k.SetValue(RunName, "\"" + Environment.ProcessPath + "\"");
        else k.DeleteValue(RunName, false);
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

// ───────────────────────── Devolve memória ao Windows quando o programa está parado ─────────────────────────
static class MemoryTrimmer
{
    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    static System.Windows.Threading.DispatcherTimer? timer;

    // Agenda a limpeza para alguns segundos depois (reinicia a contagem a cada chamada)
    public static void Schedule(int seconds = 4)
    {
        if (timer == null)
        {
            timer = new System.Windows.Threading.DispatcherTimer();
            timer.Tick += (_, _) => { timer!.Stop(); Trim(); };
        }
        timer.Stop();
        timer.Interval = TimeSpan.FromSeconds(seconds);
        timer.Start();
    }

    // Cancela a limpeza (usado enquanto a janelinha está aberta, para não atrapalhar a animação)
    public static void Cancel() => timer?.Stop();

    static void Trim()
    {
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            using var me = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(me.Handle, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch { }
    }
}

// ───────────────────────── Idioma (segue o idioma de exibição do Windows) ─────────────────────────
static class L
{
    [DllImport("kernel32.dll")]
    static extern ushort GetUserDefaultUILanguage();

    // índice das colunas abaixo: 0 = inglês, 1 = português, 2 = espanhol, 3 = francês, 4 = alemão
    static readonly int lang = Detect();

    static int Detect()
    {
        try
        {
            return (GetUserDefaultUILanguage() & 0x3FF) switch
            {
                0x16 => 1, // pt
                0x0A => 2, // es
                0x0C => 3, // fr
                0x07 => 4, // de
                _ => 0     // qualquer outro idioma: inglês
            };
        }
        catch { return 0; }
    }

    static readonly Dictionary<string, string[]> table = new()
    {
        ["StartWithWindows"] = new[] { "Start with Windows", "Iniciar com o Windows", "Iniciar con Windows", "Démarrer avec Windows", "Mit Windows starten" },
        ["Exit"]             = new[] { "Exit", "Sair", "Salir", "Quitter", "Beenden" },
        ["AlreadyRunning"]   = new[] { "Shortcut for Win is already running. Look for its icon near the clock (click the ^ arrow if it is hidden).", "O Shortcut for Win já está em execução. Procure o ícone perto do relógio (clique na seta ^ se ele estiver oculto).", "Shortcut for Win ya se está ejecutando. Busca su icono cerca del reloj (haz clic en la flecha ^ si está oculto).", "Shortcut for Win est déjà en cours d'exécution. Cherchez son icône près de l'horloge (cliquez sur la flèche ^ s'il est masqué).", "Shortcut for Win läuft bereits. Das Symbol befindet sich neben der Uhr (ggf. auf den Pfeil ^ klicken)." },
        ["AddProgram"]       = new[] { "Add program", "Adicionar programa", "Añadir programa", "Ajouter un programme", "Programm hinzufügen" },
        ["KeepOpen"]         = new[] { "Keep open (useful for dragging files)", "Manter aberto (útil para arrastar arquivos)", "Mantener abierto (útil para arrastrar archivos)", "Garder ouvert (utile pour glisser des fichiers)", "Geöffnet lassen (nützlich zum Ziehen von Dateien)" },
        ["Remove"]           = new[] { "Remove", "Remover", "Quitar", "Supprimer", "Entfernen" },
        ["Replace"]          = new[] { "Replace…", "Trocar…", "Reemplazar…", "Remplacer…", "Ersetzen…" },
        ["MissingTip"]       = new[] { "Program not found (it may have been uninstalled). Right-click to replace or remove.", "Programa não encontrado (pode ter sido desinstalado). Clique com o botão direito para trocar ou remover.", "Programa no encontrado (puede haberse desinstalado). Haz clic derecho para reemplazar o quitar.", "Programme introuvable (peut-être désinstallé). Clic droit pour remplacer ou supprimer.", "Programm nicht gefunden (möglicherweise deinstalliert). Rechtsklick zum Ersetzen oder Entfernen." },
        ["PickerTitle"]      = new[] { "Choose a program", "Escolher programa", "Elegir programa", "Choisir un programme", "Programm auswählen" },
        ["Browse"]           = new[] { "Browse for a file…", "Procurar arquivo…", "Buscar archivo…", "Parcourir…", "Datei suchen…" },
        ["FilterPrograms"]   = new[] { "Programs and shortcuts", "Programas e atalhos", "Programas y accesos directos", "Programmes et raccourcis", "Programme und Verknüpfungen" },
        ["FilterAll"]        = new[] { "All files", "Todos os arquivos", "Todos los archivos", "Tous les fichiers", "Alle Dateien" },
    };

    public static string T(string key) => table.TryGetValue(key, out var v) ? v[lang] : key;
}

// ───────────────────────── Menu de contexto do Windows (botão direito nos itens) ─────────────────────────
enum MenuResult { None, ShellCommand, Replace, Remove }

[StructLayout(LayoutKind.Sequential)]
struct NativePoint { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
struct CMINVOKECOMMANDINFOEX
{
    public int cbSize;
    public int fMask;
    public IntPtr hwnd;
    public IntPtr lpVerb;
    public IntPtr lpParameters;
    public IntPtr lpDirectory;
    public int nShow;
    public int dwHotKey;
    public IntPtr hIcon;
    public IntPtr lpTitle;
    public IntPtr lpVerbW;
    public IntPtr lpParametersW;
    public IntPtr lpDirectoryW;
    public IntPtr lpTitleW;
    public NativePoint ptInvoke;
}

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
}

[ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IContextMenu
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, [MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder pszName, uint cchMax);
}

[ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IContextMenu2
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, [MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder pszName, uint cchMax);
    [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
}

[ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IContextMenu3
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, [MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder pszName, uint cchMax);
    [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
}

static class ShellMenu
{
    const uint CmdReplace = 0x8001, CmdRemove = 0x8002;   // comandos nossos (os do Windows usam 1..0x7FFF)
    const uint IdFirst = 1, IdLast = 0x7FFF;
    const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;
    const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800, MF_BYPOSITION = 0x0400;

    static IContextMenu2? ctx2;
    static IContextMenu3? ctx3;

    static readonly Guid BHID_SFUIObject = new("3981E225-F559-11D3-8E3A-00C04F6837D5");

    [StructLayout(LayoutKind.Sequential)]
    struct MENUITEMINFO
    {
        public uint cbSize, fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData, dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string? text);
    [DllImport("user32.dll")] static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll", EntryPoint = "GetMenuItemInfoW")] static extern bool GetMenuItemInfo(IntPtr menu, uint item, bool byPosition, ref MENUITEMINFO mii);
    [DllImport("user32.dll")] static extern bool DeleteMenu(IntPtr menu, uint position, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint p);

    // Mostra o menu de contexto do Windows para o item (mais as opções "Trocar" e, se quebrado, "Remover")
    public static MenuResult Show(IntPtr hwnd, string path, bool broken, string replaceText, string removeText)
    {
        IntPtr menu = CreatePopupMenu();
        IContextMenu? ctx = null;
        try
        {
            if (!broken)
            {
                try { ctx = GetContextMenu(path); }
                catch (Exception ex) { Log.Write("menu: " + ex.Message); }

                if (ctx != null)
                {
                    ctx2 = ctx as IContextMenu2;
                    ctx3 = ctx as IContextMenu3;
                    uint flags = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0 ? 0x100u : 0u; // Shift = verbos extras
                    if (ctx.QueryContextMenu(menu, 0, IdFirst, IdLast, flags) >= 0) Prune(ctx, menu);
                }
                if (GetMenuItemCount(menu) > 0) AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
                AppendMenu(menu, MF_STRING, (IntPtr)CmdReplace, replaceText);
            }
            else
            {
                AppendMenu(menu, MF_STRING, (IntPtr)CmdReplace, replaceText);
                AppendMenu(menu, MF_STRING, (IntPtr)CmdRemove, removeText);
            }

            GetCursorPos(out var pt);
            SetForegroundWindow(hwnd);
            uint cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, hwnd, IntPtr.Zero);
            PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);

            if (cmd == CmdReplace) return MenuResult.Replace;
            if (cmd == CmdRemove) return MenuResult.Remove;
            if (cmd >= IdFirst && cmd <= IdLast && ctx != null && Invoke(ctx, hwnd, cmd - IdFirst, pt))
                return MenuResult.ShellCommand;
            return MenuResult.None;
        }
        catch (Exception ex)
        {
            Log.Write("menu: " + ex);
            return MenuResult.None;
        }
        finally
        {
            ctx2 = null;
            ctx3 = null;
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (ctx != null) Marshal.ReleaseComObject(ctx);
        }
    }

    static IContextMenu? GetContextMenu(string path)
    {
        var iidItem = typeof(IShellItem).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidItem, out var item);
        try
        {
            var bhid = BHID_SFUIObject;
            var iidMenu = typeof(IContextMenu).GUID;
            if (item.BindToHandler(IntPtr.Zero, ref bhid, ref iidMenu, out var ptr) != 0 || ptr == IntPtr.Zero)
                return null;
            try { return (IContextMenu)Marshal.GetObjectForIUnknown(ptr); }
            finally { Marshal.Release(ptr); }
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    static bool Invoke(IContextMenu ctx, IntPtr hwnd, uint offset, NativePoint pt)
    {
        var ci = new CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = 0x4000 | 0x20000000,   // CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE
            hwnd = hwnd,
            lpVerb = (IntPtr)offset,
            lpVerbW = (IntPtr)offset,
            nShow = 1,
            ptInvoke = pt
        };
        return ctx.InvokeCommand(ref ci) >= 0;
    }

    // Tira do menu "Recortar", "Excluir", "Renomear" e "Criar atalho": no item da box eles mexeriam
    // na cópia guardada pelo programa. O resto do menu fica como o Windows entrega.
    static void Prune(IContextMenu ctx, IntPtr menu)
    {
        for (int i = GetMenuItemCount(menu) - 1; i >= 0; i--)
        {
            var mii = new MENUITEMINFO { cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(), fMask = 0x0006 }; // MIIM_ID | MIIM_SUBMENU
            if (!GetMenuItemInfo(menu, (uint)i, true, ref mii)) continue;
            if (mii.hSubMenu != IntPtr.Zero || mii.wID < IdFirst || mii.wID > IdLast) continue;

            var sb = new System.Text.StringBuilder(64);
            if (ctx.GetCommandString((UIntPtr)(mii.wID - IdFirst), 0 /* GCS_VERBA */, IntPtr.Zero, sb, 64) != 0) continue;
            var verb = sb.ToString().ToLowerInvariant();
            if (verb == "cut" || verb == "delete" || verb == "rename" || verb == "link")
                DeleteMenu(menu, (uint)i, MF_BYPOSITION);
        }

        // separadores duplicados ou nas pontas
        for (int i = 0; i < GetMenuItemCount(menu); i++)
        {
            if (!IsSeparator(menu, i)) continue;
            if (i == 0 || IsSeparator(menu, i - 1)) { DeleteMenu(menu, (uint)i, MF_BYPOSITION); i--; }
        }
        int last;
        while ((last = GetMenuItemCount(menu) - 1) >= 0 && IsSeparator(menu, last))
            DeleteMenu(menu, (uint)last, MF_BYPOSITION);
    }

    static bool IsSeparator(IntPtr menu, int pos)
    {
        var mii = new MENUITEMINFO { cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(), fMask = 0x0100 }; // MIIM_FTYPE
        return GetMenuItemInfo(menu, (uint)pos, true, ref mii) && (mii.fType & MF_SEPARATOR) != 0;
    }

    // Repassa ao Windows as mensagens do menu (submenus como "Enviar para", ícones etc.)
    public static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (ctx2 == null && ctx3 == null) return IntPtr.Zero;
        // WM_INITMENUPOPUP, WM_DRAWITEM, WM_MEASUREITEM, WM_MENUCHAR
        if (msg != 0x117 && msg != 0x2B && msg != 0x2C && msg != 0x120) return IntPtr.Zero;
        try
        {
            if (ctx3 != null)
            {
                if (ctx3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result) == 0)
                {
                    handled = true;
                    return result;
                }
            }
            else if (ctx2 != null && msg != 0x120)
            {
                if (ctx2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
                {
                    handled = true;
                    return msg == 0x117 ? IntPtr.Zero : (IntPtr)1;
                }
            }
        }
        catch { }
        return IntPtr.Zero;
    }
}

// Menus no modo escuro quando o Windows está no modo escuro (melhor esforço)
static class DarkMenus
{
    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();

    public static void Enable()
    {
        try { SetPreferredAppMode(1 /* AllowDark: segue o tema do Windows */); FlushMenuThemes(); }
        catch { }
    }
}

// ───────────────────────── Armazenamento (JSON) ─────────────────────────
sealed class Store
{
    static readonly string baseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarFlyout");
    readonly string file = Path.Combine(baseDir, "groups.json");
    readonly string shortcutsDir = Path.Combine(baseDir, "Shortcuts"); // cópias dos atalhos ficam aqui
    Dictionary<string, List<string>> data = new();
    bool loadedOk = true;

    public Store()
    {
        try
        {
            if (File.Exists(file))
                data = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file)) ?? new();
        }
        catch { data = new(); loadedOk = false; }
        AdoptAll();
        if (loadedOk) CleanOrphans(); // apaga cópias que nenhum grupo usa mais
    }

    public List<string> Get(string key) => data.TryGetValue(key, out var l) ? new List<string>(l) : new();

    public void Add(string key, string path)
    {
        path = Import(path);
        if (!data.TryGetValue(key, out var l)) data[key] = l = new List<string>();
        if (!l.Contains(path, StringComparer.OrdinalIgnoreCase)) l.Add(path);
        Save();
    }

    // deleteFile = false: usado ao arrastar para fora, porque o Windows ainda pode estar copiando o arquivo
    public void Remove(string key, string path, bool deleteFile = true)
    {
        if (data.TryGetValue(key, out var l))
            l.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (deleteFile) DeleteIfOrphan(path);
        Save();
    }

    // Daqui a pouco apaga as cópias que ficaram sem uso (dá tempo do Windows terminar de copiar)
    public void ScheduleCleanup()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        t.Tick += (_, _) => { t.Stop(); CleanOrphans(); };
        t.Start();
    }

    void CleanOrphans()
    {
        try
        {
            if (!Directory.Exists(shortcutsDir)) return;
            var used = new HashSet<string>(data.Values.SelectMany(x => x), StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(shortcutsDir))
                if (!used.Contains(f)) { try { File.Delete(f); } catch { } }
        }
        catch { }
    }

    // Troca um item por outro, mantendo a mesma posição na lista
    public void Replace(string key, string oldPath, string newPath)
    {
        if (!data.TryGetValue(key, out var l)) return;
        int i = l.FindIndex(p => p.Equals(oldPath, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;

        newPath = Import(newPath);
        bool alreadyThere = l.Contains(newPath, StringComparer.OrdinalIgnoreCase)
                            && !newPath.Equals(oldPath, StringComparison.OrdinalIgnoreCase);
        if (alreadyThere) l.RemoveAt(i);   // o novo já está na lista: só tira o antigo
        else l[i] = newPath;

        DeleteIfOrphan(oldPath);
        Save();
    }

    // apaga a cópia guardada pelo programa quando nenhum grupo usa mais
    void DeleteIfOrphan(string path)
    {
        if (IsOwnCopy(path) && !data.Values.Any(x => x.Contains(path, StringComparer.OrdinalIgnoreCase)))
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ── cópia própria dos atalhos ──
    public bool IsOwnCopy(string path) =>
        path.StartsWith(shortcutsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    bool ShouldCopy(string path)
    {
        if (!File.Exists(path) || IsOwnCopy(path)) return false;

        // atalhos (.lnk / .url): sempre copia, assim apagar o original não afeta o programa
        var ext = Path.GetExtension(path);
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) return true;

        // programas soltos direto na Área de Trabalho também são copiados
        var dir = Path.GetDirectoryName(path);
        foreach (var d in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
            if (string.Equals(dir, Environment.GetFolderPath(d), StringComparison.OrdinalIgnoreCase)) return true;

        return false; // demais arquivos e pastas: mantém só a referência
    }

    string Import(string path)
    {
        try
        {
            if (!ShouldCopy(path)) return path;
            Directory.CreateDirectory(shortcutsDir);

            string name = Path.GetFileName(path);
            string dest = Path.Combine(shortcutsDir, name);
            var src = new FileInfo(path);

            // já existe uma cópia idêntica: reaproveita; senão, procura um nome livre
            for (int i = 2; File.Exists(dest); i++)
            {
                var ex = new FileInfo(dest);
                if (ex.Length == src.Length && ex.LastWriteTimeUtc == src.LastWriteTimeUtc) return dest;
                dest = Path.Combine(shortcutsDir,
                    Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
            }
            File.Copy(path, dest);
            return dest;
        }
        catch { return path; }
    }

    // Itens adicionados em versões anteriores (só com o caminho) passam a ter cópia própria
    void AdoptAll()
    {
        bool changed = false;
        foreach (var l in data.Values)
        {
            for (int i = 0; i < l.Count; i++)
            {
                var n = Import(l[i]);
                if (!n.Equals(l[i], StringComparison.OrdinalIgnoreCase)) { l[i] = n; changed = true; }
            }
            var unique = l.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (unique.Count != l.Count) { l.Clear(); l.AddRange(unique); changed = true; }
        }
        if (changed) Save();
    }

    void Save()
    {
        Directory.CreateDirectory(baseDir);
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
    bool internalDrag; // true enquanto um item da própria box está sendo arrastado
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
        add.ToolTip = L.T("AddProgram");
        add.Click += (_, _) => PickProgram();

        pinBtn = MakeButton("\uE718", out pinGlyph);
        pinBtn.ToolTip = L.T("KeepOpen");
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
        MemoryTrimmer.Cancel();
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
            MemoryTrimmer.Schedule(); // janelinha fechada: devolve memória ao Windows
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource src)
            src.AddHook(ShellMenu.Hook);
    }

    // Item "quebrado" = o programa não existe mais (desinstalado). Apagar o atalho da
    // Área de Trabalho não conta, porque o programa guarda a própria cópia do atalho.
    static bool IsBroken(string path)
    {
        if (Directory.Exists(path)) return false;
        if (!File.Exists(path)) return true;
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var target = ShortcutTarget(path);
            if (!string.IsNullOrWhiteSpace(target) && !File.Exists(target) && !Directory.Exists(target))
                return true;
        }
        return false;
    }

    static string? ShortcutTarget(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return null;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic link = shell.CreateShortcut(lnk);
            string target = link.TargetPath;
            Marshal.FinalReleaseComObject((object)link);
            Marshal.FinalReleaseComObject((object)shell);
            return target;
        }
        catch { return null; }
    }

    void ShowItemMenu(string path, bool broken)
    {
        busy = true;
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            switch (ShellMenu.Show(hwnd, path, broken, L.T("Replace"), L.T("Remove")))
            {
                case MenuResult.Replace: ReplaceItem(path); break;
                case MenuResult.Remove: RemoveItem(path); break;
                case MenuResult.ShellCommand:
                    if (!File.Exists(path) && !Directory.Exists(path)) RemoveItem(path);
                    else if (!Pinned) Dismiss();
                    break;
            }
        }
        finally { busy = false; }
    }

    // Arrastar um item para fora da box (Área de Trabalho, uma pasta...): ele vai para lá e sai da box
    void StartDrag(UIElement source, string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;

        // cópia própria do programa: copia o atalho. Referência a arquivo/pasta real: cria um atalho para ele
        var allowed = store.IsOwnCopy(path) ? DragDropEffects.Copy : DragDropEffects.Link;
        var data = new DataObject(DataFormats.FileDrop, new[] { path });

        DragDropEffects result;
        internalDrag = true;
        busy = true;
        try
        {
            source.ReleaseMouseCapture();
            result = DragDrop.DoDragDrop(source, data, allowed);
        }
        finally
        {
            internalDrag = false;
            busy = false;
        }

        if (result != DragDropEffects.None)
        {
            store.Remove(key, path, deleteFile: false);
            Rebuild();
            Reposition();
            store.ScheduleCleanup();
        }
    }

    void RemoveItem(string path)
    {
        store.Remove(key, path);
        Rebuild();
        Reposition();
    }

    void ReplaceItem(string oldPath)
    {
        busy = true;
        try
        {
            var w = new PickerWindow();
            if (w.ShowDialog() == true && w.Result != null)
            {
                store.Replace(key, oldPath, w.Result);
                Rebuild();
                Reposition();
            }
        }
        finally { busy = false; }
    }

    UIElement MakeItem(string path)
    {
        bool broken = IsBroken(path);
        var icon = broken ? null : IconFor(path);

        var b = new Button
        {
            Style = BtnStyle,
            ToolTip = broken
                ? L.T("MissingTip")
                : (Directory.Exists(path) ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path)),
            Content = icon != null
                ? new System.Windows.Controls.Image { Source = icon, Width = 32, Height = 32 }
                : new TextBlock { Text = broken ? "⚠" : "📁", FontSize = 24, Foreground = new SolidColorBrush(SystemTheme.Foreground) }
        };

        b.Click += (_, _) =>
        {
            if (broken) return; // programa desinstalado: use o botão direito para trocar ou o × para remover
            Launch(path);
            if (!Pinned) Dismiss();
        };

        // botão direito: menu de contexto do Windows para o programa
        b.PreviewMouseRightButtonUp += (_, e) => { e.Handled = true; ShowItemMenu(path, broken); };

        // arrastar o item para fora da box
        System.Windows.Point? dragStart = null;
        b.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(null);
        b.PreviewMouseMove += (_, e) =>
        {
            if (dragStart == null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - dragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - dragStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragStart = null;
            StartDrag(b, path);
        };

        // botão "×" no canto, visível só quando o mouse está sobre o item
        var del = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 1, 0),
            Visibility = Visibility.Collapsed,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = L.T("Remove"),
            Child = new TextBlock
            {
                Text = "\uE711",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 9,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        del.MouseLeftButtonDown += (_, e) => e.Handled = true;
        del.MouseLeftButtonUp += (_, e) => { e.Handled = true; RemoveItem(path); };

        var g = new Grid { Width = 44, Height = 44 };
        g.Children.Add(b);
        g.Children.Add(del);
        g.MouseEnter += (_, _) => del.Visibility = Visibility.Visible;
        g.MouseLeave += (_, _) => del.Visibility = Visibility.Collapsed;
        return g;
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
        e.Effects = (!internalDrag && e.Data.GetDataPresent(DataFormats.FileDrop)) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        if (!internalDrag && e.Data.GetData(DataFormats.FileDrop) is string[] files)
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
        catch (Exception ex) { MessageBox.Show(ex.Message, "Shortcut for Win"); }
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
        Title = L.T("PickerTitle");
        Width = 380; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        var bgc = SystemTheme.Background;
        Background = new SolidColorBrush(Color.FromRgb(bgc.R, bgc.G, bgc.B));
        Foreground = new SolidColorBrush(SystemTheme.Foreground);

        all = Scan();

        var search = new TextBox { Margin = new Thickness(8, 8, 8, 4), Padding = new Thickness(6) };
        var browse = new Button { Content = L.T("Browse"), Margin = new Thickness(8, 0, 8, 8), Padding = new Thickness(6) };

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
            var dlg = new OpenFileDialog { Filter = L.T("FilterPrograms") + "|*.exe;*.lnk;*.bat|" + L.T("FilterAll") + "|*.*" };
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

    static readonly string[] UninstallWords = { "uninstall", "desinstal", "désinstall", "deinstall" };

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
                    if (UninstallWords.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                    dict.TryAdd(n, new Entry { Name = n, FullPath = f });
                }
            }
            catch { }
        }
        return dict.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
