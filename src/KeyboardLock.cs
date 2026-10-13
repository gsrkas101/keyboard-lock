// Keyboard Lock - lock/unlock the laptop keyboard with a single key, so kids can't type on
// it by accident. Works like the touchpad on/off key, but for the keyboard.
//
// Safe by design:
//  * Only a user-mode low-level keyboard hook. No drivers, no device changes, nothing
//    persistent: exiting the app (or the app crashing) gives the keyboard back instantly.
//  * Ctrl+Alt+Del can never be blocked by a hook, so there is always a way out.
//  * The mouse/touchpad can always unlock (tray icon, or double-click the on-screen box).
//  * Always starts unlocked, and unlocks itself whenever Windows is locked or signed in to.
//  * A key-up is only blocked if its key-down was blocked, so no key ever gets "stuck".
//  * Any error inside the hook lets the key through (fails open, never fails locked).
//
// Everything except the lock key is blocked while locked. Brightness keys are handled by the
// laptop's firmware and can't be blocked, so brightness changes are undone instead.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Keyboard Lock")]
[assembly: System.Reflection.AssemblyProduct("Keyboard Lock")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace KeyboardLock
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--export-icon")
            {
                Icons.ExportIco(args[1]);
                return 0;
            }

            string command = ParseCommand(args);
            bool firstInstance;
            using (var mutex = new Mutex(true, Ipc.Prefix + "instance", out firstInstance))
            {
                if (!firstInstance)
                {
                    // Already running: forward the command (e.g. --toggle from the macro key).
                    if (command != "background") Ipc.Send(command);
                    return 0;
                }
                if (command == "exit") return 0;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

                TrayApp app;
                try { app = new TrayApp(command); }
                catch (Exception ex)
                {
                    MessageBox.Show("Keyboard Lock could not start.\n\n" + ex.Message, "Keyboard Lock",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                Application.Run(app);
                mutex.ReleaseMutex();
            }
            return 0;
        }

        static string ParseCommand(string[] args)
        {
            foreach (string a in args)
            {
                string c = a.Trim().TrimStart('-', '/').ToLowerInvariant();
                if (c == "background" || Array.IndexOf(Ipc.Commands, c) >= 0) return c;
            }
            return "show";
        }
    }

    // Lets a second launch ("KeyboardLock.exe --toggle") control the running instance.
    static class Ipc
    {
        public const string Prefix = @"Local\KeyboardLock-6c1e2f4a-";
        public static readonly string[] Commands = { "show", "toggle", "lock", "unlock", "exit" };

        public static EventWaitHandle Open(string command)
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, Prefix + command);
        }

        public static void Send(string command)
        {
            using (EventWaitHandle e = Open(command)) e.Set();
        }
    }

    static class Native
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        public const uint WM_QUIT = 0x0012;
        public const uint LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10;
        public const int WS_EX_TOPMOST = 0x08, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);
        [DllImport("user32.dll")]
        public static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);
        [DllImport("user32.dll")]
        public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string moduleName);
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }

    // The lock/unlock key, optionally with modifiers (e.g. Ctrl+Alt+K).
    sealed class HotKey
    {
        public const int Ctrl = 1, Alt = 2, Shift = 4, Win = 8;
        public static readonly HotKey Default = new HotKey(0x4B, 0x25, false, Ctrl | Alt);   // Ctrl+Alt+K
        public readonly int Vk, Scan, Mods;
        public readonly bool Extended;

        public HotKey(int vk, int scan, bool extended, int mods)
        {
            Vk = vk & 0xFF;
            Scan = scan;
            Extended = extended;
            Mods = mods & 15;
        }

        // Some OEM keys have no virtual-key code (0 or 0xFF); those are told apart by scan code.
        bool ByScanCode { get { return Vk == 0 || Vk == 0xFF; } }

        public bool IsKey(int vk, int scan, bool extended)
        {
            return ByScanCode ? scan == Scan && extended == Extended : vk == Vk;
        }

        public static int ModifierOf(int vk)
        {
            switch (vk)
            {
                case 0x10: case 0xA0: case 0xA1: return Shift;
                case 0x11: case 0xA2: case 0xA3: return Ctrl;
                case 0x12: case 0xA4: case 0xA5: return Alt;
                case 0x5B: case 0x5C: return Win;
                default: return 0;
            }
        }

        // Letters, digits, punctuation, space, Enter, Backspace, Tab, numpad.
        public bool IsTypingKey
        {
            get
            {
                if ((Mods & (Ctrl | Alt | Win)) != 0) return false;
                int v = Vk;
                return (v >= 0x30 && v <= 0x39) || (v >= 0x41 && v <= 0x5A) || (v >= 0x60 && v <= 0x6F)
                    || v == 0x08 || v == 0x09 || v == 0x0D || v == 0x20
                    || (v >= 0xBA && v <= 0xC0) || (v >= 0xDB && v <= 0xDF) || v == 0xE2;
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            if ((Mods & Ctrl) != 0) sb.Append("Ctrl+");
            if ((Mods & Alt) != 0) sb.Append("Alt+");
            if ((Mods & Shift) != 0) sb.Append("Shift+");
            if ((Mods & Win) != 0) sb.Append("Win+");
            if (ByScanCode) sb.Append("Special key (scan code 0x" + Scan.ToString("X2") + (Extended ? ", extended" : "") + ")");
            else if ((Vk >= 0x30 && Vk <= 0x39) || (Vk >= 0x41 && Vk <= 0x5A)) sb.Append((char)Vk);
            else sb.Append(((Keys)Vk).ToString());
            return sb.ToString();
        }
    }

    sealed class Settings
    {
        public HotKey Toggle = HotKey.Default;
        public bool ShowOverlay = true;

        public static string FilePath
        {
            get
            {
                return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "KeyboardLock"), "settings.ini");
            }
        }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath)) return Parse(File.ReadAllLines(FilePath));
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllLines(FilePath, ToLines());
        }

        public static Settings Parse(string[] lines)
        {
            var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines)
            {
                int eq = line.IndexOf('=');
                int n;
                if (eq > 0 && int.TryParse(line.Substring(eq + 1).Trim(), out n))
                    values[line.Substring(0, eq).Trim()] = n;
            }
            var s = new Settings();
            int vk, scan, ext, mods, flag;
            if (values.TryGetValue("ToggleVk", out vk) && vk >= 0 && vk <= 0xFF)
            {
                values.TryGetValue("ToggleScan", out scan);
                values.TryGetValue("ToggleExtended", out ext);
                values.TryGetValue("ToggleModifiers", out mods);
                if ((vk != 0 && vk != 0xFF) || scan > 0) s.Toggle = new HotKey(vk, scan, ext != 0, mods);
            }
            if (values.TryGetValue("ShowOverlay", out flag)) s.ShowOverlay = flag != 0;
            return s;
        }

        public string[] ToLines()
        {
            var lines = new List<string> { "; Keyboard Lock settings" };
            if (Toggle != null)
            {
                lines.Add("ToggleVk=" + Toggle.Vk);
                lines.Add("ToggleScan=" + Toggle.Scan);
                lines.Add("ToggleExtended=" + (Toggle.Extended ? 1 : 0));
                lines.Add("ToggleModifiers=" + Toggle.Mods);
            }
            lines.Add("ShowOverlay=" + (ShowOverlay ? 1 : 0));
            return lines.ToArray();
        }
    }

    // Decides, for every key event, whether it is blocked. Pure logic, no Windows calls, so it
    // can be unit tested. Called on the hook thread; Locked/Toggle may be set from the UI thread.
    sealed class KeyFilter
    {
        const int VK_ESCAPE = 0x1B;

        readonly bool[] down = new bool[256];       // key state as seen by us (blocked or not)
        readonly bool[] swallowed = new bool[256];  // key-down was blocked -> block its key-up too
        bool toggleHeld;
        volatile bool resetPending;
        volatile Action<HotKey> learnCallback;

        public volatile bool Locked;
        public volatile HotKey Toggle;
        public Action<Action> Post = a => a();      // how callbacks reach the UI thread
        public Action Toggled;                      // the lock key flipped Locked

        // Next non-modifier key press is reported to the callback (null = Esc, cancelled).
        public void BeginLearn(Action<HotKey> callback) { learnCallback = callback; }
        public void CancelLearn() { learnCallback = null; }
        public bool IsLearning { get { return learnCallback != null; } }

        // Forget held keys, e.g. after Win+L where key-ups go to the lock screen instead.
        public void ResetKeyState() { resetPending = true; }

        // Returns true if the key event must be blocked.
        public bool Process(int msg, int vk, int scan, uint flags)
        {
            bool isDown = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
            bool isUp = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
            if (!isDown && !isUp) return false;
            vk &= 0xFF;

            if (resetPending)
            {
                resetPending = false;
                Array.Clear(down, 0, down.Length);
                Array.Clear(swallowed, 0, swallowed.Length);
                toggleHeld = false;
            }

            bool extended = (flags & Native.LLKHF_EXTENDED) != 0;
            HotKey toggle = Toggle;
            bool isToggleKey = toggle != null && toggle.IsKey(vk, scan, extended);
            down[vk] = isDown;

            if (isUp)
            {
                if (isToggleKey) toggleHeld = false;
                if (!swallowed[vk]) return false;
                swallowed[vk] = false;
                return true;
            }

            // Key down, or auto-repeat while held.
            int mods = CurrentModifiers();
            Action<HotKey> learn = learnCallback;
            if (learn != null)
            {
                if (HotKey.ModifierOf(vk) != 0) return false;   // let modifiers through to build combinations
                learnCallback = null;
                HotKey picked = vk == VK_ESCAPE && mods == 0 ? null : new HotKey(vk, scan, extended, mods);
                Post(() => learn(picked));
                return Swallow(vk);
            }

            // Extra modifiers are fine, so a single lock key works even if Shift etc. is held.
            if (isToggleKey && (toggleHeld || (mods & toggle.Mods) == toggle.Mods))
            {
                if (!toggleHeld)   // auto-repeat must not flip it again
                {
                    toggleHeld = true;
                    Locked = !Locked;
                    Action toggled = Toggled;
                    if (toggled != null) Post(toggled);
                }
                return Swallow(vk);
            }

            // Everything else is blocked while locked, including keys typed by software.
            if (Locked) return Swallow(vk);
            swallowed[vk] = false;
            return false;
        }

        bool Swallow(int vk)
        {
            swallowed[vk] = true;
            return true;
        }

        int CurrentModifiers()
        {
            int m = 0;
            if (down[0x10] || down[0xA0] || down[0xA1]) m |= HotKey.Shift;
            if (down[0x11] || down[0xA2] || down[0xA3]) m |= HotKey.Ctrl;
            if (down[0x12] || down[0xA4] || down[0xA5]) m |= HotKey.Alt;
            if (down[0x5B] || down[0x5C]) m |= HotKey.Win;
            return m;
        }
    }

    // Installs the low-level keyboard hook on its own thread, which does nothing else, so the
    // hook always answers fast (Windows silently removes hooks that are too slow).
    sealed class KeyboardHook : IDisposable
    {
        readonly KeyFilter filter;
        Native.LowLevelKeyboardProc proc;   // kept in a field so the GC can't collect it
        IntPtr hook;
        Thread thread;
        uint threadId;

        public KeyboardHook(KeyFilter filter) { this.filter = filter; }

        public void Start()
        {
            int error = 0;
            using (var ready = new ManualResetEvent(false))
            {
                thread = new Thread(() =>
                {
                    Native.MSG msg;
                    Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 0);   // create this thread's message queue
                    threadId = Native.GetCurrentThreadId();
                    proc = Callback;
                    hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
                    error = Marshal.GetLastWin32Error();
                    ready.Set();
                    if (hook == IntPtr.Zero) return;
                    while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }
                    Native.UnhookWindowsHookEx(hook);
                });
                thread.IsBackground = true;
                thread.Priority = ThreadPriority.Highest;
                thread.Name = "Keyboard hook";
                thread.Start();
                ready.WaitOne();
            }
            if (hook == IntPtr.Zero)
                throw new Exception("The keyboard hook could not be installed: " + new Win32Exception(error).Message);
        }

        IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    if (filter.Process(wParam.ToInt32(), (int)k.vkCode, (int)k.scanCode, k.flags)) return new IntPtr(1);
                }
                catch { }   // a bug must never lock the keyboard: let the key through
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (thread == null) return;
            Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(2000);
            thread = null;
        }
    }

    // Brightness keys are handled by the laptop's firmware, not the keyboard, so no hook can block
    // them. Instead, while the keyboard is locked, any brightness change is put straight back.
    sealed class BrightnessGuard : IDisposable
    {
        const string Scope = @"root\wmi";
        readonly object gate = new object();
        ManagementEventWatcher watcher;
        System.Threading.Timer poll;   // backup in case a change raises no event
        int saved = -1;

        public void Start()
        {
            lock (gate)
            {
                if (saved >= 0) return;
                saved = Read();
                if (saved < 0) return;   // no adjustable built-in screen
                try
                {
                    watcher = new ManagementEventWatcher(new ManagementScope(Scope),
                        new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
                    watcher.EventArrived += (s, e) => Enforce();
                    watcher.Start();
                }
                catch { DisposeWatcher(); }
                poll = new System.Threading.Timer(_ => Enforce(), null, 1000, 1000);
            }
        }

        public void Stop()
        {
            lock (gate)
            {
                saved = -1;
                DisposeWatcher();
                if (poll != null)
                {
                    poll.Dispose();
                    poll = null;
                }
            }
        }

        public void Dispose() { Stop(); }

        void Enforce()
        {
            int target;
            lock (gate) target = saved;
            if (target < 0) return;
            try
            {
                if (Read() != target) Set(target);
            }
            catch { }
        }

        void DisposeWatcher()
        {
            if (watcher == null) return;
            try { watcher.Stop(); } catch { }
            watcher.Dispose();
            watcher = null;
        }

        static int Read()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(Scope,
                    "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE"))
                using (ManagementObjectCollection results = searcher.Get())
                    foreach (ManagementObject m in results)
                        using (m) return Convert.ToInt32(m["CurrentBrightness"]);
            }
            catch { }
            return -1;
        }

        static void Set(int level)
        {
            using (var searcher = new ManagementObjectSearcher(Scope,
                "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE"))
            using (ManagementObjectCollection results = searcher.Get())
                foreach (ManagementObject m in results)
                    using (m) m.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)level });
        }
    }

    // Starts the app the moment you sign in, using a Task Scheduler task. (The registry Run key
    // starts apps a minute or more after sign-in, and Windows skips entries that weren't approved
    // in its startup-apps list.) No admin rights needed; runs with normal user rights.
    static class Autostart
    {
        const string TaskName = "Keyboard Lock";
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "KeyboardLock";   // used by older versions

        public static bool IsEnabled()
        {
            return Schtasks("/query /tn \"" + TaskName + "\"") == 0;
        }

        // Creates the task, or refreshes it so it points at this exe.
        public static void Enable()
        {
            string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                "  <RegistrationInfo><Description>Starts Keyboard Lock when you sign in.</Description></RegistrationInfo>\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId>" +
                "<LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\n" +
                "  <Settings>\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
                "    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n" +
                "    <Priority>4</Priority>\n" +
                "  </Settings>\n" +
                "  <Actions Context=\"Author\"><Exec><Command>\"" + SecurityElement.Escape(Application.ExecutablePath) +
                "\"</Command><Arguments>--background</Arguments></Exec></Actions>\n" +
                "</Task>\n";
            string file = Path.Combine(Path.GetTempPath(), "KeyboardLock-task.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            try
            {
                if (Schtasks("/create /f /tn \"" + TaskName + "\" /xml \"" + file + "\"") != 0)
                    throw new Exception("Windows Task Scheduler could not create the startup task.");
            }
            finally { File.Delete(file); }
            RemoveRunKeyEntry();
        }

        public static void Disable()
        {
            Schtasks("/delete /f /tn \"" + TaskName + "\"");
            RemoveRunKeyEntry();
        }

        public static bool HasRunKeyEntry()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                return key != null && key.GetValue(RunValueName) != null;
        }

        static void RemoveRunKeyEntry()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                if (key != null) key.DeleteValue(RunValueName, false);
        }

        static int Schtasks(string arguments)
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using (Process p = Process.Start(info))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        public static readonly Color UnlockedColor = Color.FromArgb(34, 160, 80);
        public static readonly Color LockedColor = Color.FromArgb(220, 53, 69);

        readonly SynchronizationContext ui;
        readonly Settings settings;
        readonly KeyFilter filter = new KeyFilter();
        readonly KeyboardHook hook;
        readonly BrightnessGuard brightness = new BrightnessGuard();
        readonly NotifyIcon tray;
        readonly OverlayForm overlay;
        readonly OwnedIcon unlockedIcon, lockedIcon;
        readonly ToolStripMenuItem miToggle, miKey, miOverlay, miStartup;
        readonly ManualResetEvent stopIpc = new ManualResetEvent(false);
        bool choosingKey, exiting, startupEnabled;

        public TrayApp(string startCommand)
        {
            ui = SynchronizationContext.Current;
            bool firstRun = !File.Exists(Settings.FilePath);
            settings = Settings.Load();

            int iconSize = SystemInformation.SmallIconSize.Width;
            unlockedIcon = Icons.Make(UnlockedColor, false, iconSize);
            lockedIcon = Icons.Make(LockedColor, true, iconSize);

            overlay = new OverlayForm();
            overlay.UnlockRequested += (s, e) => SetLocked(false);

            filter.Toggle = settings.Toggle;
            filter.Post = a => ui.Post(_ => a(), null);
            filter.Toggled = UpdateUi;
            hook = new KeyboardHook(filter);
            hook.Start();

            var menu = new ContextMenuStrip();
            miToggle = new ToolStripMenuItem("Lock keyboard", null, (s, e) => SetLocked(!filter.Locked));
            miToggle.Font = new Font(miToggle.Font, FontStyle.Bold);
            miKey = new ToolStripMenuItem("", null, (s, e) => ChooseKey());
            miOverlay = new ToolStripMenuItem("Show \"Keyboard locked\" box on screen", null, (s, e) =>
            {
                settings.ShowOverlay = !settings.ShowOverlay;
                SaveSettings();
                UpdateUi();
            });
            miStartup = new ToolStripMenuItem("Start with Windows", null, (s, e) => ToggleStartup());
            menu.Items.Add(miToggle);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miKey);
            menu.Items.Add(miOverlay);
            menu.Items.Add(miStartup);
            menu.Items.Add(new ToolStripMenuItem("Copy command for Xiaomi key", null, (s, e) => CopyCommand()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Help", null, (s, e) => ShowHelp()));
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => ExitApp()));
            menu.Opening += (s, e) => miStartup.Checked = startupEnabled;

            tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) SetLocked(!filter.Locked); };

            Application.ThreadException += (s, e) =>
            {
                filter.Locked = false;   // fail open: never leave the keyboard locked because of a bug
                try { UpdateUi(); } catch { }
                MessageBox.Show("Something went wrong, so the keyboard was unlocked.\n\n" + e.Exception.Message,
                    "Keyboard Lock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            SystemEvents.SessionSwitch += OnSessionSwitch;
            StartIpcListener();
            UpdateUi();
            StartTrayIconPromotion();

            SetUpAutostart(firstRun);
            if (firstRun)
            {
                SaveSettings();
                Balloon("Keyboard Lock is ready",
                    "Press " + settings.Toggle + " to lock or unlock the keyboard. It starts with Windows; " +
                    "change the key or that from the padlock menu.");
                if (startCommand != "show") RunCommand(startCommand);
            }
            else RunCommand(startCommand);
        }

        // Windows 11 hides new tray icons under the ^ arrow. Mark ours "show on taskbar", unless the
        // user has hidden it on purpose (IsPromoted = 0). Explorer creates the entry shortly after the
        // icon first appears, so retry for a few seconds. Explorer applies it the next time it starts.
        void StartTrayIconPromotion()
        {
            int attempts = 0;
            var timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += (s, e) =>
            {
                if (PromoteTrayIcon() || ++attempts >= 6)
                {
                    timer.Stop();
                    timer.Dispose();
                }
            };
            timer.Start();
        }

        static bool PromoteTrayIcon()
        {
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                {
                    if (root == null) return true;   // not Windows 11: nothing to do
                    bool found = false;
                    foreach (string name in root.GetSubKeyNames())
                        using (RegistryKey key = root.OpenSubKey(name, true))
                        {
                            string path = key == null ? null : key.GetValue("ExecutablePath") as string;
                            if (!string.Equals(path, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) continue;
                            found = true;
                            if (key.GetValue("IsPromoted") == null)
                                key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                        }
                    return found;
                }
            }
            catch { return true; }
        }

        void RunCommand(string command)
        {
            switch (command)
            {
                case "toggle": SetLocked(!filter.Locked); break;
                case "lock": SetLocked(true); break;
                case "unlock": SetLocked(false); break;
                case "exit": ExitApp(); break;
                case "show":
                    Balloon("Keyboard Lock is running",
                        settings.Toggle != null ? "Press " + settings.Toggle + " to lock or unlock the keyboard."
                                                : "Click the padlock icon to lock or unlock the keyboard.");
                    break;
            }
        }

        void SetLocked(bool locked)
        {
            if (locked && filter.IsLearning) return;
            filter.Locked = locked;
            UpdateUi();
        }

        void UpdateUi()
        {
            if (exiting) return;
            bool locked = filter.Locked;
            string key = settings.Toggle != null ? settings.Toggle.ToString() : null;
            tray.Icon = (locked ? lockedIcon : unlockedIcon).Icon;
            tray.Text = Tip(locked ? "Keyboard Lock: LOCKED" : "Keyboard Lock: unlocked", key);
            miToggle.Text = locked ? "Unlock keyboard" : "Lock keyboard";
            miKey.Text = key == null ? "Choose lock/unlock key..." : "Lock/unlock key: " + key + "   (change...)";
            miOverlay.Checked = settings.ShowOverlay;

            if (locked && settings.ShowOverlay)
            {
                overlay.SetHint(key != null ? "Press " + key + " or double-click here to unlock"
                                            : "Double-click here to unlock");
                overlay.ShowAtCorner();
            }
            else if (overlay.Visible) overlay.Hide();

            if (locked) brightness.Start();
            else brightness.Stop();
        }

        static string Tip(string state, string key)
        {
            string text = key == null ? state : state + "  (" + key + ")";
            return text.Length <= 63 ? text : state;   // NotifyIcon allows 63 characters
        }

        void ChooseKey()
        {
            if (choosingKey) return;
            choosingKey = true;
            try
            {
                SetLocked(false);
                HotKey picked = null;
                using (var dialog = new LearnKeyForm(filter))
                {
                    if (dialog.ShowDialog() == DialogResult.OK) picked = dialog.Result;
                }
                if (picked == null) return;

                string message = "Use  " + picked + "  to lock and unlock the keyboard?\n\n" +
                    "While Keyboard Lock is running, this key only locks/unlocks and won't do its usual job.";
                if (picked.IsTypingKey)
                    message += "\n\nWarning: " + picked + " is a normal typing key. A spare key or a combination " +
                               "such as Ctrl+Alt+K is a better choice.";
                if (MessageBox.Show(message, "Keyboard Lock", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                    != DialogResult.Yes) return;

                settings.Toggle = picked;
                filter.Toggle = picked;
                SaveSettings();
                UpdateUi();
                Balloon("Ready", "Press " + picked + " to lock or unlock the keyboard.");
            }
            finally { choosingKey = false; }
        }

        void SaveSettings()
        {
            try { settings.Save(); }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save settings.\n\n" + ex.Message, "Keyboard Lock",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            // Windows locked / signed in again: start fresh and unlocked, so the password can
            // always be typed and nobody comes back to a "broken" keyboard.
            ui.Post(_ =>
            {
                filter.ResetKeyState();
                SetLocked(false);
            }, null);
        }

        void StartIpcListener()
        {
            var handles = new WaitHandle[Ipc.Commands.Length + 1];
            for (int i = 0; i < Ipc.Commands.Length; i++) handles[i] = Ipc.Open(Ipc.Commands[i]);
            handles[Ipc.Commands.Length] = stopIpc;
            var listener = new Thread(() =>
            {
                while (true)
                {
                    int i = WaitHandle.WaitAny(handles);
                    if (i == Ipc.Commands.Length) break;
                    string command = Ipc.Commands[i];
                    ui.Post(_ => RunCommand(command), null);
                }
                for (int i = 0; i < Ipc.Commands.Length; i++) handles[i].Close();
            });
            listener.IsBackground = true;
            listener.Name = "Command listener";
            listener.Start();
        }

        // First run: turn on. Already on (or set up by an older version): refresh, so the task
        // always points at this exe. Runs in the background so startup isn't slowed down.
        void SetUpAutostart(bool firstRun)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool enabled = false;
                try
                {
                    enabled = Autostart.IsEnabled();
                    if (firstRun || enabled || Autostart.HasRunKeyEntry())
                    {
                        Autostart.Enable();
                        enabled = true;
                    }
                }
                catch { }
                ui.Post(__ => startupEnabled = enabled, null);
            });
        }

        void ToggleStartup()
        {
            try
            {
                if (startupEnabled) Autostart.Disable();
                else Autostart.Enable();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not change \"Start with Windows\".\n\n" + ex.Message, "Keyboard Lock",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            startupEnabled = Autostart.IsEnabled();
        }

        void CopyCommand()
        {
            string command = "\"" + Application.ExecutablePath + "\" --toggle";
            Clipboard.SetText(command);
            MessageBox.Show("Copied to the clipboard:\n\n" + command + "\n\n" +
                "Use this if the macro key isn't detected when choosing a key. In Xiaomi PC Manager (or any " +
                "tool that can open a program when a key is pressed), set the macro key to open this command. " +
                "Each press then locks or unlocks the keyboard.\n\n" +
                "--lock, --unlock and --exit work the same way.",
                "Keyboard Lock", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void ShowHelp()
        {
            string key = settings.Toggle != null ? settings.Toggle.ToString() : "your lock key (choose one in the menu)";
            MessageBox.Show(
                "Keyboard Lock stops the laptop keyboard from typing, so little hands can't press anything by accident.\n\n" +
                "HOW TO USE\n" +
                "•  Press " + key + " to lock, and again to unlock.\n" +
                "•  Or click the padlock icon in the taskbar tray.\n" +
                "•  While locked, double-click the \"Keyboard locked\" box to unlock with the mouse.\n\n" +
                "ALWAYS-WORKING WAYS OUT\n" +
                "•  Ctrl+Alt+Del can't be blocked. Use it to lock Windows or open Task Manager.\n" +
                "•  Locking Windows or signing in again unlocks the keyboard automatically.\n" +
                "•  Exiting this app, or it crashing, gives the keyboard back instantly. Nothing on your PC is changed.\n\n" +
                "GOOD TO KNOW\n" +
                "•  Brightness keys are handled by the laptop itself and can't be blocked, so while locked any brightness change is undone.\n" +
                "•  The keyboard light, touchpad on/off and power button are handled by the laptop itself and still work.\n" +
                "•  Keys aren't blocked while an app running as administrator is in front.\n" +
                "•  Macro key not detected? Use \"Copy command for Xiaomi key\" in the menu.",
                "Keyboard Lock - Help", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void Balloon(string title, string text)
        {
            tray.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);
        }

        void ExitApp()
        {
            if (exiting) return;
            exiting = true;
            filter.Locked = false;
            brightness.Dispose();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            stopIpc.Set();
            hook.Dispose();
            tray.Visible = false;
            tray.Dispose();
            overlay.Dispose();
            unlockedIcon.Dispose();
            lockedIcon.Dispose();
            ExitThread();
        }
    }

    // Small "Keyboard locked" box in the bottom-right corner. Never takes focus.
    sealed class OverlayForm : Form
    {
        readonly Font titleFont = new Font("Segoe UI Semibold", 11f);
        readonly Font hintFont = new Font("Segoe UI", 9f);
        readonly float scale;
        string hint = "";

        public event EventHandler UnlockRequested;

        public OverlayForm()
        {
            Text = "Keyboard Lock - keyboard locked";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(30, 30, 34);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
        }

        int S(float v) { return (int)Math.Round(v * scale); }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int round = 2;   // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND (Windows 11)
                Native.DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            }
            catch { }
        }

        public void SetHint(string text)
        {
            hint = text;
            Size title = TextRenderer.MeasureText("Keyboard locked", titleFont, Size.Empty, TextFormatFlags.NoPadding);
            Size sub = TextRenderer.MeasureText(hint, hintFont, Size.Empty, TextFormatFlags.NoPadding);
            Size = new Size(S(64) + Math.Max(title.Width, sub.Width) + S(20), S(66));
            Invalidate();
        }

        public void ShowAtCorner()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - S(16), area.Bottom - Height - S(16));
            if (!Visible) Show();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Icons.DrawPadlock(g, new RectangleF(S(16), S(16), S(34), S(34)), TrayApp.LockedColor, true);
            TextRenderer.DrawText(g, "Keyboard locked", titleFont, new Point(S(64), S(12)), Color.White,
                TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, hint, hintFont, new Point(S(64), S(37)), Color.FromArgb(185, 185, 195),
                TextFormatFlags.NoPadding);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (UnlockRequested != null) UnlockRequested(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                titleFont.Dispose();
                hintFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // "Press the key you want to use" dialog.
    sealed class LearnKeyForm : Form
    {
        readonly KeyFilter filter;
        public HotKey Result { get; private set; }

        public LearnKeyForm(KeyFilter filter)
        {
            this.filter = filter;
            float scale;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            Func<int, int> S = v => (int)Math.Round(v * scale);

            Text = "Keyboard Lock";
            Font = new Font("Segoe UI", 9.75f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(S(22), S(18), S(22), S(16)),
            };
            var title = new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 13f),
                Text = "Press the key you want to use",
                Margin = new Padding(0, 0, 0, S(10)),
            };
            var body = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(S(440), 0),
                Margin = new Padding(0, 0, 0, S(12)),
                Text = "Press it once now, for example the Xiaomi macro key. That key will then lock and " +
                       "unlock the keyboard. A combination such as Ctrl+Alt+K works too.\n\nPress Esc to cancel.",
            };
            var note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(S(440), 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 0, 0, S(14)),
                Text = "Nothing happens when you press the macro key? Then it doesn't reach Windows as a normal " +
                       "key. Cancel, and use \"Copy command for Xiaomi key\" from the tray menu instead.",
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0),
            };
            panel.Controls.AddRange(new Control[] { title, body, note, cancel });
            Controls.Add(panel);
            CancelButton = cancel;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            filter.BeginLearn(OnLearned);
        }

        void OnLearned(HotKey key)
        {
            if (IsDisposed || !Visible) return;
            Result = key;
            DialogResult = key == null ? DialogResult.Cancel : DialogResult.OK;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            filter.CancelLearn();
            base.OnFormClosed(e);
        }
    }

    sealed class OwnedIcon : IDisposable
    {
        public readonly Icon Icon;
        IntPtr handle;

        public OwnedIcon(IntPtr handle)
        {
            this.handle = handle;
            Icon = Icon.FromHandle(handle);
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            Icon.Dispose();
            Native.DestroyIcon(handle);
            handle = IntPtr.Zero;
        }
    }

    static class Icons
    {
        public static OwnedIcon Make(Color color, bool closed, int size)
        {
            using (Bitmap bmp = Render(color, closed, size)) return new OwnedIcon(bmp.GetHicon());
        }

        static Bitmap Render(Color color, bool closed, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                DrawPadlock(g, new RectangleF(0, 0, size, size), color, closed);
            }
            return bmp;
        }

        public static void DrawPadlock(Graphics g, RectangleF r, Color color, bool closed)
        {
            float w = r.Width, h = r.Height;
            float stroke = Math.Max(1.6f, w * 0.12f);
            var body = new RectangleF(r.X + w * 0.14f, r.Y + h * 0.45f, w * 0.72f, h * 0.52f);
            float radius = w * 0.22f;
            float cx = r.X + w / 2f;
            float arcTop = r.Y + stroke / 2f + (closed ? h * 0.08f : 0f);   // open = shackle lifted
            float legTop = arcTop + radius;
            float legBottom = body.Y + stroke / 2f;

            using (var pen = new Pen(color, stroke))
            using (var shackle = new GraphicsPath())
            {
                shackle.AddLine(cx - radius, closed ? legBottom : legTop + h * 0.05f, cx - radius, legTop);
                shackle.AddArc(cx - radius, arcTop, radius * 2, radius * 2, 180, 180);
                shackle.AddLine(cx + radius, legTop, cx + radius, legBottom);
                g.DrawPath(pen, shackle);
            }
            using (var brush = new SolidBrush(color))
            using (GraphicsPath bodyPath = RoundedRect(body, w * 0.1f))
                g.FillPath(brush, bodyPath);
            if (w >= 20)
            {
                float hole = w * 0.07f;
                float hy = body.Y + body.Height * 0.42f;
                using (var brush = new SolidBrush(Color.FromArgb(230, 255, 255, 255)))
                {
                    g.FillEllipse(brush, cx - hole, hy - hole, hole * 2, hole * 2);
                    g.FillRectangle(brush, cx - hole * 0.45f, hy, hole * 0.9f, body.Height * 0.25f);
                }
            }
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Writes a multi-size .ico (PNG entries) of the locked padlock, used as the exe icon.
        public static void ExportIco(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            var images = new List<byte[]>();
            foreach (int size in sizes)
                using (Bitmap bmp = Render(TrayApp.LockedColor, true, size))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    images.Add(ms.ToArray());
                }

            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images) w.Write(image);
            }
        }
    }
}
