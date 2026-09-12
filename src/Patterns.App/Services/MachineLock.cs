using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The operating system behind the show lock: each thing the lock holds, set and put back. Windows
/// for real (<see cref="WindowsMachineLock"/>), nothing elsewhere (<see cref="NoMachineLock"/>),
/// and a fake in the tests. Every call is best-effort: it answers with a state and words and
/// never throws into the desk.
/// </summary>
public interface IMachineLock
{
    /// <summary>Whether this machine has the settings at all (Windows).</summary>
    bool Available { get; }

    /// <summary>Toasts and banners off for this user (on: put back as they were).</summary>
    LockItemState Notifications(bool off);

    /// <summary>The sound scheme silenced — every event's sound and the default beep — or put back.</summary>
    LockItemState SystemSounds(bool off);

    /// <summary>Every audio session that is not this process's and not allowed, muted (or put back); how many are muted now, -1 when the sessions could not be read.</summary>
    int OtherAudio(bool mute, IReadOnlyList<string> allowedProcessNames);

    /// <summary>The Sticky, Filter and Toggle Keys shortcuts off, or put back.</summary>
    LockItemState Shortcuts(bool off);

    /// <summary>The machine kept awake, the display on, the screensaver off — or released.</summary>
    LockItemState KeepAwake(bool on);

    /// <summary>The Windows key swallowed, or let through.</summary>
    LockItemState WindowsKey(bool blocked);

    /// <summary>Whether Windows Update has a restart waiting.</summary>
    bool UpdateRestartPending();

    /// <summary>The process that owns the foreground window, by name; "" when unknown.</summary>
    string ForegroundApp();

    /// <summary>After a crash: what a previous lock changed, put back from the receipt it left; the words, or "" when there was none.</summary>
    string RestoreFromReceipt();
}

/// <summary>Not Windows: nothing to hold, said so.</summary>
public sealed class NoMachineLock : IMachineLock
{
    public bool Available => false;
    public LockItemState Notifications(bool off) => LockItemState.NotHere;
    public LockItemState SystemSounds(bool off) => LockItemState.NotHere;
    public int OtherAudio(bool mute, IReadOnlyList<string> allowedProcessNames) => -1;
    public LockItemState Shortcuts(bool off) => LockItemState.NotHere;
    public LockItemState KeepAwake(bool on) => LockItemState.NotHere;
    public LockItemState WindowsKey(bool blocked) => LockItemState.NotHere;
    public bool UpdateRestartPending() => false;
    public string ForegroundApp() => "";
    public string RestoreFromReceipt() => "";
}

/// <summary>
/// Windows. Everything a user may change without an administrator, each with its original kept in
/// a receipt beside the settings — written before the first change, read back by the next start
/// when a crash left the lock on — so the machine is always put back: the toast switch (the
/// user's own, and the per-user policy Explorer honours), the sound scheme's every event and the
/// default beep, each other app's audio session (through the same session interface the volume
/// mixer uses), the accessibility shortcuts (the way games do), the execution state and the
/// screensaver, and a low-level keyboard hook that swallows the Windows key. What needs an
/// administrator — pausing Windows Update, forbidding its restart — is only read here and left
/// to the one-time script.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMachineLock : IMachineLock
{
    public const string ReceiptFile = "showlock.receipt.json";

    private readonly string _baseDirectory;
    private readonly Dictionary<string, bool> _mutedBefore = new(StringComparer.Ordinal);
    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hook;

    public WindowsMachineLock(string baseDirectory) => _baseDirectory = baseDirectory;

    public bool Available => OperatingSystem.IsWindows();

    private string ReceiptPath => Path.Combine(_baseDirectory, ReceiptFile);

    // ---- the receipt: what was, so it can be again ------------------------------------------

    private sealed class Receipt
    {
        public DateTime AtUtc { get; set; }
        public Dictionary<string, string?> Registry { get; set; } = new();   // "hive\\key|value" → the original as a string, or null when absent
        public uint? StickyFlags { get; set; }
        public uint? FilterFlags { get; set; }
        public uint? ToggleFlags { get; set; }
        public bool? ScreenSaverActive { get; set; }
    }

    private Receipt Load()
    {
        try
        {
            if (File.Exists(ReceiptPath)) return JsonSerializer.Deserialize<Receipt>(File.ReadAllText(ReceiptPath)) ?? new Receipt { AtUtc = DateTime.UtcNow };
        }
        catch (Exception ex)
        {
            Log.Warn("The show lock's receipt could not be read.", ex);
        }
        return new Receipt { AtUtc = DateTime.UtcNow };
    }

    private void Save(Receipt receipt)
    {
        try
        {
            Directory.CreateDirectory(_baseDirectory);
            File.WriteAllText(ReceiptPath, JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn("The show lock's receipt could not be written.", ex);
        }
    }

    private void ClearReceiptIfEmpty(Receipt receipt)
    {
        if (receipt.Registry.Count > 0 || receipt.StickyFlags is not null || receipt.FilterFlags is not null || receipt.ToggleFlags is not null || receipt.ScreenSaverActive is not null) return;
        try { File.Delete(ReceiptPath); } catch { /* gone */ }
    }

    public string RestoreFromReceipt()
    {
        if (!File.Exists(ReceiptPath)) return "";
        var receipt = Load();
        var put = new List<string>();
        try
        {
            if (receipt.Registry.Count > 0)
            {
                foreach (var (path, original) in receipt.Registry) PutRegistry(path, original);
                put.Add(receipt.Registry.Any(k => k.Key.Contains("AppEvents", StringComparison.Ordinal)) ? "the sounds" : "");
                if (receipt.Registry.Any(k => k.Key.Contains("Notifications", StringComparison.Ordinal) || k.Key.Contains("Explorer", StringComparison.Ordinal))) put.Add("the notifications");
            }
            if (receipt.StickyFlags is { } sk) SetStickyFlags(sk);
            if (receipt.FilterFlags is { } fk) SetFilterFlags(fk);
            if (receipt.ToggleFlags is { } tk) SetToggleFlags(tk);
            if (receipt.StickyFlags is not null || receipt.FilterFlags is not null || receipt.ToggleFlags is not null) put.Add("the shortcut keys");
            if (receipt.ScreenSaverActive is { } ss)
            {
                SystemParametersInfo(SpiSetScreenSaveActive, ss ? 1u : 0u, IntPtr.Zero, SpifSendChange);
                put.Add("the screensaver");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The show lock's receipt could not be put back in full.", ex);
        }
        try { File.Delete(ReceiptPath); } catch { /* gone */ }
        var words = string.Join(", ", put.Where(w => w.Length > 0));
        return words.Length > 0 ? $"The previous run ended with the show lock on: {words} put back." : "";
    }

    // ---- notifications ---------------------------------------------------------------------

    private const string PushKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\PushNotifications|ToastEnabled";
    private const string PolicyKey = @"HKCU\Software\Policies\Microsoft\Windows\Explorer|NoToastApplicationNotification";

    public LockItemState Notifications(bool off)
    {
        try
        {
            var receipt = Load();
            if (off)
            {
                Remember(receipt, PushKey);
                Remember(receipt, PolicyKey);
                Save(receipt);
                PutRegistry(PushKey, "0");
                PutRegistry(PolicyKey, "1");
            }
            else
            {
                if (receipt.Registry.Remove(PushKey, out var push)) PutRegistry(PushKey, push);
                if (receipt.Registry.Remove(PolicyKey, out var policy)) PutRegistry(PolicyKey, policy);
                Save(receipt);
                ClearReceiptIfEmpty(receipt);
            }
            return LockItemState.Done;
        }
        catch (Exception ex)
        {
            Log.Warn("The show lock could not set the notifications.", ex);
            return LockItemState.Failed;
        }
    }

    // ---- system sounds ------------------------------------------------------------------------

    private const string BeepKey = @"HKCU\Control Panel\Sound|Beep";

    public LockItemState SystemSounds(bool off)
    {
        try
        {
            var receipt = Load();
            if (off)
            {
                using var apps = Registry.CurrentUser.OpenSubKey(@"AppEvents\Schemes\Apps");
                if (apps is not null)
                {
                    foreach (var app in apps.GetSubKeyNames())
                    {
                        using var appKey = apps.OpenSubKey(app);
                        if (appKey is null) continue;
                        foreach (var evt in appKey.GetSubKeyNames())
                        {
                            var path = $@"HKCU\AppEvents\Schemes\Apps\{app}\{evt}\.Current|";
                            Remember(receipt, path);
                        }
                    }
                }
                Remember(receipt, BeepKey);
                Save(receipt);
                foreach (var path in receipt.Registry.Keys.Where(k => k.Contains(@"AppEvents\Schemes\Apps", StringComparison.Ordinal)).ToList()) PutRegistry(path, "");
                PutRegistry(BeepKey, "no");
            }
            else
            {
                foreach (var path in receipt.Registry.Keys.Where(k => k.Contains("AppEvents", StringComparison.Ordinal) || k == BeepKey).ToList())
                {
                    receipt.Registry.Remove(path, out var original);
                    PutRegistry(path, original);
                }
                Save(receipt);
                ClearReceiptIfEmpty(receipt);
            }
            return LockItemState.Done;
        }
        catch (Exception ex)
        {
            Log.Warn("The show lock could not set the system sounds.", ex);
            return LockItemState.Failed;
        }
    }

    // ---- other apps' audio ------------------------------------------------------------------

    public int OtherAudio(bool mute, IReadOnlyList<string> allowedProcessNames)
    {
        try
        {
            var own = Environment.ProcessId;
            var muted = 0;
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        var pid = (int)session.GetProcessID;
                        if (pid == own || pid == 0) continue;
                        var id = session.GetSessionInstanceIdentifier;
                        if (mute)
                        {
                            if (ShowLockWords.IsAllowed(ProcessName(pid), allowedProcessNames)) continue;
                            if (!_mutedBefore.ContainsKey(id)) _mutedBefore[id] = session.SimpleAudioVolume.Mute;
                            if (!session.SimpleAudioVolume.Mute) session.SimpleAudioVolume.Mute = true;
                            muted++;
                        }
                        else if (_mutedBefore.TryGetValue(id, out var was))
                        {
                            session.SimpleAudioVolume.Mute = was;
                        }
                    }
                }
            }
            if (!mute) _mutedBefore.Clear();
            return muted;
        }
        catch (Exception ex)
        {
            Log.Warn("The show lock could not read the audio sessions.", ex);
            return -1;
        }
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    // ---- the accessibility shortcuts ------------------------------------------------------------

    public LockItemState Shortcuts(bool off)
    {
        try
        {
            var receipt = Load();
            if (off)
            {
                var sk = GetSticky();
                var fk = GetFilter();
                var tk = GetToggle();
                receipt.StickyFlags ??= sk.dwFlags;
                receipt.FilterFlags ??= fk.dwFlags;
                receipt.ToggleFlags ??= tk.dwFlags;
                Save(receipt);
                // Only the shortcut and its confirmation go; a feature the user has on stays on.
                if ((sk.dwFlags & SkfStickyKeysOn) == 0) SetStickyFlags(sk.dwFlags & ~(SkfHotKeyActive | SkfConfirmHotKey));
                if ((fk.dwFlags & FkfFilterKeysOn) == 0) SetFilterFlags(fk.dwFlags & ~(FkfHotKeyActive | FkfConfirmHotKey));
                if ((tk.dwFlags & TkfToggleKeysOn) == 0) SetToggleFlags(tk.dwFlags & ~(TkfHotKeyActive | TkfConfirmHotKey));
            }
            else
            {
                if (receipt.StickyFlags is { } s) SetStickyFlags(s);
                if (receipt.FilterFlags is { } f) SetFilterFlags(f);
                if (receipt.ToggleFlags is { } t) SetToggleFlags(t);
                receipt.StickyFlags = null;
                receipt.FilterFlags = null;
                receipt.ToggleFlags = null;
                Save(receipt);
                ClearReceiptIfEmpty(receipt);
            }
            return LockItemState.Done;
        }
        catch (Exception ex)
        {
            Log.Warn("The show lock could not set the shortcut keys.", ex);
            return LockItemState.Failed;
        }
    }

    // ---- awake ----------------------------------------------------------------------------------

    public LockItemState KeepAwake(bool on)
    {
        try
        {
            var receipt = Load();
            if (on)
            {
                SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
                if (receipt.ScreenSaverActive is null)
                {
                    var active = 0;
                    if (SystemParametersInfo(SpiGetScreenSaveActive, 0, ref active, 0)) receipt.ScreenSaverActive = active != 0;
                    Save(receipt);
                }
                SystemParametersInfo(SpiSetScreenSaveActive, 0, IntPtr.Zero, SpifSendChange);
            }
            else
            {
                SetThreadExecutionState(EsContinuous);
                if (receipt.ScreenSaverActive is { } was)
                {
                    SystemParametersInfo(SpiSetScreenSaveActive, was ? 1u : 0u, IntPtr.Zero, SpifSendChange);
                    receipt.ScreenSaverActive = null;
                    Save(receipt);
                    ClearReceiptIfEmpty(receipt);
                }
            }
            return LockItemState.Done;
        }
        catch (Exception ex)
        {
            Log.Warn("The show lock could not hold the machine awake.", ex);
            return LockItemState.Failed;
        }
    }

    // ---- the Windows key ------------------------------------------------------------------------

    public LockItemState WindowsKey(bool blocked)
    {
        try
        {
            if (blocked)
            {
                if (_hook != IntPtr.Zero) return LockItemState.Done;
                _hookProc = HookProc; // kept, or the collector takes the callback under the hook
                using var module = Process.GetCurrentProcess().MainModule;
                _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(module?.ModuleName), 0);
                return _hook == IntPtr.Zero ? LockItemState.Failed : LockItemState.Done;
            }
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            _hookProc = null;
            return LockItemState.Done;
        }
        catch (Exception ex)
        {
            Log.Warn("The show lock could not hook the Windows key.", ex);
            return LockItemState.Failed;
        }
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var vk = (uint)Marshal.ReadInt32(lParam);
            if (vk is VkLWin or VkRWin) return 1; // swallowed
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    // ---- Windows Update -------------------------------------------------------------------------

    public bool UpdateRestartPending()
    {
        try
        {
            using var au = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (au is not null) return true;
            using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            return cbs is not null;
        }
        catch
        {
            return false;
        }
    }

    public string ForegroundApp()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return "";
            GetWindowThreadProcessId(window, out var pid);
            return pid == 0 ? "" : ProcessName((int)pid);
        }
        catch
        {
            return "";
        }
    }

    // ---- the registry, by path ------------------------------------------------------------------

    /// <summary>"HKCU\\Software\\…\\Key|Value" → the value as a string, or null when absent.</summary>
    private static string? ReadRegistry(string path)
    {
        var (root, key, value) = Split(path);
        using var k = root.OpenSubKey(key);
        var v = k?.GetValue(value);
        return v is null ? null : Convert.ToString(v);
    }

    private static void PutRegistry(string path, string? original)
    {
        var (root, key, value) = Split(path);
        using var k = root.CreateSubKey(key, writable: true);
        if (k is null) return;
        if (original is null)
        {
            k.DeleteValue(value, throwOnMissingValue: false);
            return;
        }
        // The toast switches and the policy are numbers; the sounds and the beep are words.
        if (path == PushKey || path == PolicyKey) k.SetValue(value, int.TryParse(original, out var n) ? n : 0, RegistryValueKind.DWord);
        else k.SetValue(value, original, RegistryValueKind.String);
    }

    private static void Remember(Receipt receipt, string path)
    {
        if (!receipt.Registry.ContainsKey(path)) receipt.Registry[path] = ReadRegistry(path);
    }

    private static (RegistryKey Root, string Key, string Value) Split(string path)
    {
        var bar = path.LastIndexOf('|');
        var full = path[..bar];
        var value = path[(bar + 1)..];
        var slash = full.IndexOf('\\');
        var hive = full[..slash];
        var key = full[(slash + 1)..];
        var root = hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
        return (root, key, value);
    }

    // ---- Win32 ----------------------------------------------------------------------------------

    private const uint SpiGetFilterKeys = 0x0032;
    private const uint SpiSetFilterKeys = 0x0033;
    private const uint SpiGetToggleKeys = 0x0034;
    private const uint SpiSetToggleKeys = 0x0035;
    private const uint SpiGetStickyKeys = 0x003A;
    private const uint SpiSetStickyKeys = 0x003B;
    private const uint SpiGetScreenSaveActive = 0x0010;
    private const uint SpiSetScreenSaveActive = 0x0011;
    private const uint SpifSendChange = 0x0002;
    private const uint SkfStickyKeysOn = 0x1;
    private const uint SkfHotKeyActive = 0x4;
    private const uint SkfConfirmHotKey = 0x8;
    private const uint FkfFilterKeysOn = 0x1;
    private const uint FkfHotKeyActive = 0x4;
    private const uint FkfConfirmHotKey = 0x8;
    private const uint TkfToggleKeysOn = 0x1;
    private const uint TkfHotKeyActive = 0x4;
    private const uint TkfConfirmHotKey = 0x8;
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x1;
    private const uint EsDisplayRequired = 0x2;
    private const int WhKeyboardLl = 13;
    private const uint VkLWin = 0x5B;
    private const uint VkRWin = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct StickyKeys
    {
        public uint cbSize;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ToggleKeys
    {
        public uint cbSize;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterKeys
    {
        public uint cbSize;
        public uint dwFlags;
        public uint iWaitMSec;
        public uint iDelayMSec;
        public uint iRepeatMSec;
        public uint iBounceMSec;
    }

    private static StickyKeys GetSticky()
    {
        var sk = new StickyKeys { cbSize = (uint)Marshal.SizeOf<StickyKeys>() };
        SystemParametersInfo(SpiGetStickyKeys, sk.cbSize, ref sk, 0);
        return sk;
    }

    private static void SetStickyFlags(uint flags)
    {
        var sk = new StickyKeys { cbSize = (uint)Marshal.SizeOf<StickyKeys>(), dwFlags = flags };
        SystemParametersInfo(SpiSetStickyKeys, sk.cbSize, ref sk, 0);
    }

    private static FilterKeys GetFilter()
    {
        var fk = new FilterKeys { cbSize = (uint)Marshal.SizeOf<FilterKeys>() };
        SystemParametersInfo(SpiGetFilterKeys, fk.cbSize, ref fk, 0);
        return fk;
    }

    private static void SetFilterFlags(uint flags)
    {
        var fk = GetFilter();
        fk.dwFlags = flags;
        SystemParametersInfo(SpiSetFilterKeys, fk.cbSize, ref fk, 0);
    }

    private static ToggleKeys GetToggle()
    {
        var tk = new ToggleKeys { cbSize = (uint)Marshal.SizeOf<ToggleKeys>() };
        SystemParametersInfo(SpiGetToggleKeys, tk.cbSize, ref tk, 0);
        return tk;
    }

    private static void SetToggleFlags(uint flags)
    {
        var tk = new ToggleKeys { cbSize = (uint)Marshal.SizeOf<ToggleKeys>(), dwFlags = flags };
        SystemParametersInfo(SpiSetToggleKeys, tk.cbSize, ref tk, 0);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref StickyKeys value, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref FilterKeys value, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref ToggleKeys value, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? module);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
