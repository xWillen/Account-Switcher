using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using System.IO;

namespace WillenSwitcher;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };

    private readonly List<string> _startupWarnings = new();
    private TaskCompletionSource<string?>? _dialogTcs;
    private string _currentPlatform = "Steam";
    private bool _isBusy;

    public ObservableCollection<Account> AllAccounts { get; } = new();
    public ObservableCollection<Account> FilteredAccounts { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        BackgroundImage.Source = AssetImages.Load("bg.png");
        SteamNavIcon.Source = AssetImages.Load("steam.png");
        RiotNavIcon.Source = AssetImages.Load("riot.png");

        AccountsList.ItemsSource = FilteredAccounts;
        LoadAccountsFromJson();

        foreach (var account in AllAccounts.Where(a => a.Platform.Equals("Steam", StringComparison.OrdinalIgnoreCase)))
            UpdateSteamAccountDetails(account);

        RefreshFilteredList();
    }

    public void ReportError(string title, string message)
    {
        Dispatcher.BeginInvoke(new Action(() => _ = ShowCustomMessageAsync(title, message)));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        try { WriteAccountsFile(); }
        catch (Exception ex) { AppLog.Write(ex); }
        base.OnClosing(e);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        foreach (string warning in _startupWarnings.ToList())
            await ShowCustomMessageAsync("Notice", warning);
        _startupWarnings.Clear();

        await AutoDetectSteamAccounts();
        await AutoDetectRiotAccounts();
    }

    private async Task AutoDetectSteamAccounts()
    {
        var known = AllAccounts
            .Where(a => a.Platform.Equals("Steam", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Username)
            .ToList();

        List<Account> detected;
        try
        {
            detected = await Task.Run(() => DetectSteamAccounts(known));
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            await ShowCustomMessageAsync("Steam Detection Failed", ex.Message);
            return;
        }

        if (detected.Count == 0) return;

        foreach (var account in detected) AllAccounts.Add(account);
        RefreshFilteredList();
        await PersistAccountsAsync();
    }

    private List<Account> DetectSteamAccounts(List<string> known)
    {
        var names = GetSteamEntries().Select(entry => entry.AccountName).ToList();

        string? autoLogin = ReadRegistryValue(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "AutoLoginUser");
        if (!string.IsNullOrWhiteSpace(autoLogin)) names.Add(autoLogin.Trim());

        var result = new List<Account>();
        foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (known.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(CreateAccount("Steam", name));
        }
        return result;
    }

    private async Task AutoDetectRiotAccounts()
    {
        var known = AllAccounts.Select(a => (a.Platform, a.Username)).ToList();

        List<Account> detected;
        try
        {
            detected = await Task.Run(() => DetectActiveRiotAccounts(known));
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            return;
        }

        if (detected.Count == 0) return;

        foreach (var account in detected) AllAccounts.Add(account);
        await PersistAccountsAsync();
        RefreshFilteredList();
    }

    private List<Account> DetectActiveRiotAccounts(List<(string Platform, string Username)> known)
    {
        var result = new List<Account>();

        string username = GetRiotAccountName();
        if (string.IsNullOrWhiteSpace(username)) return result;

        bool exists = known.Any(k => k.Platform.Equals("Riot", StringComparison.OrdinalIgnoreCase)
                                     && k.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        if (exists) return result;

        var account = CreateAccount("Riot", username);
        try
        {
            SaveSessionSnapshot(account);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            return result;
        }

        result.Add(account);
        return result;
    }

    private Account CreateAccount(string platform, string username)
    {
        var account = new Account
        {
            Username = username,
            DisplayName = username,
            Platform = platform,
            Note = "Text Note!"
        };
        if (platform.Equals("Steam", StringComparison.OrdinalIgnoreCase)) UpdateSteamAccountDetails(account);
        return account;
    }

    private Task<string?> ShowDialogAsync(string title, string message, string defaultText, bool showInput, bool showCancel)
    {
        _dialogTcs?.TrySetResult(null);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialogTcs = tcs;

        DialogTitleText.Text = title;
        DialogMessageText.Text = message;
        DialogInputBox.Text = defaultText;
        DialogInputBox.Visibility = showInput ? Visibility.Visible : Visibility.Collapsed;
        DialogCancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        CustomDialogOverlay.Visibility = Visibility.Visible;

        if (showInput)
        {
            DialogInputBox.Focus();
            DialogInputBox.SelectAll();
        }
        else
        {
            DialogOkButton.Focus();
        }

        return tcs.Task;
    }

    private Task<string?> ShowCustomInputDialogAsync(string title, string message, string defaultText = "") =>
        ShowDialogAsync(title, message, defaultText, true, true);

    private Task ShowCustomMessageAsync(string title, string message) =>
        ShowDialogAsync(title, message, "", false, false);

    private async Task<bool> ShowCustomConfirmAsync(string title, string message) =>
        await ShowDialogAsync(title, message, "", false, true) != null;

    private void DialogOkButton_Click(object sender, RoutedEventArgs e)
    {
        CustomDialogOverlay.Visibility = Visibility.Collapsed;
        string result = DialogInputBox.Visibility == Visibility.Visible ? DialogInputBox.Text : string.Empty;
        _dialogTcs?.TrySetResult(result);
    }

    private void DialogCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CustomDialogOverlay.Visibility = Visibility.Collapsed;
        _dialogTcs?.TrySetResult(null);
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase || source is TextBoxBase || source is ScrollBar) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void PlatformNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string platform })
        {
            _currentPlatform = platform;
            PlatformTitleText.Text = platform + " Accounts";
            RefreshFilteredList();
        }
    }

    private void RefreshFilteredList()
    {
        FilteredAccounts.Clear();
        foreach (var account in AllAccounts.Where(a => a.Platform.Equals(_currentPlatform, StringComparison.OrdinalIgnoreCase)))
            FilteredAccounts.Add(account);
    }

    private async Task<bool> RunOperationAsync(Action work, string errorTitle)
    {
        if (_isBusy) return false;
        _isBusy = true;
        Mouse.OverrideCursor = Cursors.Wait;
        Exception? failure = null;
        try
        {
            await Task.Run(work);
        }
        catch (Exception ex)
        {
            failure = ex;
            AppLog.Write(ex);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _isBusy = false;
        }

        if (failure == null) return true;
        await ShowCustomMessageAsync(errorTitle, failure.Message);
        return false;
    }

    private string DetectAccountName(string platform) => platform switch
    {
        "Steam" => GetSteamAccountName(),
        "Riot" => GetRiotAccountName(),
        _ => ""
    };

    private static string? ReadRegistryValue(RegistryHive hive, RegistryView view, string subKey, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey, false);
            return key?.GetValue(valueName)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private string GetSteamAccountName()
    {
        try
        {
            string? autoLogin = ReadRegistryValue(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "AutoLoginUser");
            var entries = GetSteamEntries();
            var selected = entries.FirstOrDefault(e => !string.IsNullOrWhiteSpace(autoLogin) && e.AccountName.Equals(autoLogin, StringComparison.OrdinalIgnoreCase))
                           ?? entries.FirstOrDefault(e => e.MostRecent);
            return selected?.AccountName ?? autoLogin ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string GetSteamPath()
    {
        var candidates = new List<string?>
        {
            ReadRegistryValue(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath"),
            ReadRegistryValue(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"),
            ReadRegistryValue(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
        };

        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                string normalized = Path.GetFullPath(candidate.Replace('/', '\\'));
                if (Directory.Exists(normalized)) return normalized;
            }
            catch
            {
            }
        }
        return "";
    }

    private static string GetSteamExePath()
    {
        string? fromRegistry = ReadRegistryValue(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamExe");
        if (!string.IsNullOrWhiteSpace(fromRegistry))
        {
            try
            {
                string normalized = Path.GetFullPath(fromRegistry.Replace('/', '\\'));
                if (File.Exists(normalized)) return normalized;
            }
            catch
            {
            }
        }
        string steamPath = GetSteamPath();
        return string.IsNullOrEmpty(steamPath) ? "" : Path.Combine(steamPath, "steam.exe");
    }

    private List<SteamEntry> GetSteamEntries()
    {
        var entries = new List<SteamEntry>();
        try
        {
            string steamPath = GetSteamPath();
            if (string.IsNullOrEmpty(steamPath)) return entries;

            string path = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(path)) return entries;

            string text = ReadFileShared(path);
            var blocks = Regex.Matches(text, @"(?ms)^\s*""(?<id>\d{15,20})""\s*\{(?<body>.*?^)\s*\}");
            foreach (Match block in blocks)
            {
                string body = block.Groups["body"].Value;
                string? name = ReadVdfValue(body, "AccountName");
                if (string.IsNullOrWhiteSpace(name)) continue;
                entries.Add(new SteamEntry(
                    name,
                    ReadVdfValue(body, "PersonaName") ?? name,
                    ReadVdfValue(body, "MostRecent") == "1",
                    block.Groups["id"].Value,
                    ReadVdfValue(body, "AvatarUrl")));
            }
        }
        catch
        {
        }
        return entries;
    }

    private static string? ReadVdfValue(string block, string key)
    {
        var match = Regex.Match(block, @"(?im)^\s*""" + Regex.Escape(key) + @"""\s*""(?<value>[^""]*)""");
        return match.Success ? match.Groups["value"].Value : null;
    }

    private void UpdateSteamAccountDetails(Account account)
    {
        try
        {
            var entry = GetSteamEntries().FirstOrDefault(e => e.AccountName.Equals(account.Username, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;

            account.DisplayName = entry.PersonaName;

            string cache = Path.Combine(GetSteamPath(), "config", "avatarcache");
            string? image = Directory.Exists(cache)
                ? Directory.GetFiles(cache, entry.SteamId + ".*")
                    .Where(p => ImageExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                    .OrderByDescending(SafeLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;

            if (!string.IsNullOrWhiteSpace(image)) account.AvatarPath = image;
            else if (Uri.TryCreate(entry.AvatarUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                account.AvatarPath = uri.AbsoluteUri;
        }
        catch
        {
        }
    }

    private sealed record SteamEntry(string AccountName, string PersonaName, bool MostRecent, string SteamId, string? AvatarUrl);

    private static void WaitForProcessesExit(int timeoutMs, params string[] names)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            bool running = false;
            foreach (string name in names)
            {
                Process[] processes = Process.GetProcessesByName(name);
                if (processes.Length > 0) running = true;
                foreach (var process in processes) process.Dispose();
            }
            if (!running) return;
            Thread.Sleep(250);
        }
    }

    private static bool CloseSteam()
    {
        string exe = GetSteamExePath();
        Process[] running = Process.GetProcessesByName("steam");
        bool isRunning = running.Length > 0;
        foreach (var process in running) process.Dispose();

        if (isRunning && File.Exists(exe))
        {
            try
            {
                using var shutdown = Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false, CreateNoWindow = true });
            }
            catch
            {
            }
            WaitForProcessesExit(8000, "steam");
        }

        bool closed = KillProcesses("steam");
        KillProcesses("steamwebhelper");
        return closed;
    }

    private static void SetSteamAutoLogin(string username)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Valve\Steam", true);
            key.SetValue("AutoLoginUser", username, RegistryValueKind.String);
            key.SetValue("RememberPassword", 1, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            throw new InvalidOperationException("Steam registry settings could not be updated for the current Windows user.", ex);
        }
    }

    private static void LaunchSteam()
    {
        try
        {
            string exe = GetSteamExePath();
            if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Steam could not be started. Make sure Steam is installed.", ex);
        }
    }

    private static void SwitchSteam(Account account)
    {
        if (!CloseSteam())
            throw new InvalidOperationException("Steam could not be closed. Close it manually and try again.");
        SetSteamAutoLogin(account.Username);
        LaunchSteam();
    }

    private string GetRiotAccountName()
    {
        try
        {
            string data = GetRiotDataPath();
            string root = Path.GetDirectoryName(data) ?? data;
            string logs = Path.Combine(root, "Logs");
            string config = Path.Combine(root, "Config");
            var files = new List<string>();

            if (Directory.Exists(data))
            {
                string privateSettings = Path.Combine(data, "RiotClientPrivateSettings.yaml");
                if (File.Exists(privateSettings)) files.Add(privateSettings);
                files.AddRange(Directory.GetFiles(data, "*.yaml", SearchOption.AllDirectories));
                files.AddRange(Directory.GetFiles(data, "*.json", SearchOption.AllDirectories));
            }
            if (Directory.Exists(config))
                files.AddRange(Directory.GetFiles(config, "*", SearchOption.AllDirectories));
            if (Directory.Exists(logs))
                files.AddRange(Directory.GetFiles(logs, "*.log", SearchOption.AllDirectories).OrderByDescending(SafeLastWriteTimeUtc).Take(10));

            string logMarker = Path.DirectorySeparatorChar + "Logs" + Path.DirectorySeparatorChar;

            foreach (string path in files.Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(p => p.EndsWith("RiotClientPrivateSettings.yaml", StringComparison.OrdinalIgnoreCase))
                         .ThenByDescending(SafeLastWriteTimeUtc))
            {
                string text;
                try { text = ReadFileShared(path); }
                catch { continue; }

                string? gameName = MatchNamedValue(text, @"""game_name""\s*:\s*""([^""]+)""")
                                   ?? MatchNamedValue(text, @"(?im)game_name:\s*""?([^""\r\n]+)");
                string? tagLine = MatchNamedValue(text, @"""tag_line""\s*:\s*""([^""]+)""")
                                  ?? MatchNamedValue(text, @"(?im)tag_line:\s*""?([^""\r\n]+)");
                if (Plausible(gameName) && Plausible(tagLine)) return gameName!.Trim() + "#" + tagLine!.Trim();

                string? display = MatchNamedValue(text, @"(?im)displayName:\s*""?([^""\r\n]+)")
                                  ?? MatchNamedValue(text, @"""game_name""\s*:\s*""([^""]+)""");
                if (Plausible(display)) return display!.Trim();

                if (path.Contains(logMarker, StringComparison.OrdinalIgnoreCase))
                {
                    string? logged = MatchNamedValue(text, @"(?im)display name update received:\s*([^\r\n,]+)")
                                     ?? MatchNamedValue(text, @"(?im)Logged in as\s+([^\r\n,]+)")
                                     ?? MatchNamedValue(text, @"(?im)Riot ID:\s*([^\r\n,]+)")
                                     ?? MatchNamedValue(text, @"(?im)Player\s+([^\r\n#]+#[A-Za-z0-9]+)");
                    if (Plausible(logged)) return CleanRiotDisplayName(logged!);
                }
            }
        }
        catch
        {
        }
        return "";
    }

    private static string CleanRiotDisplayName(string value)
    {
        string cleaned = value.Trim();
        const string marker = "display name update received:";
        int markerIndex = cleaned.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0) cleaned = cleaned.Substring(markerIndex + marker.Length).Trim();
        cleaned = cleaned.Trim('"', '\'');
        int colon = cleaned.LastIndexOf(':');
        if (colon >= 0 && !cleaned.Contains('#')) cleaned = cleaned.Substring(colon + 1).Trim();
        return cleaned;
    }

    private static string GetRiotDataPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Data");

    private static bool CloseRiot()
    {
        bool closed = KillProcesses("RiotClientServices", "RiotClientUx", "RiotClientUxRender", "Riot Client", "VALORANT", "VALORANT-Win64-Shipping", "LeagueClient", "LeagueClientUx");
        Thread.Sleep(500);
        return closed;
    }

    private void SwitchRiot(Account account)
    {
        string snapshot = Path.Combine(GetSessionPath("Riot", account.Username), "Data");
        if (!Directory.Exists(snapshot))
            throw new InvalidOperationException("No saved session exists for this Riot account. Use Login New Account, sign in, then press Add Account.");

        if (!CloseRiot())
            throw new InvalidOperationException("Riot Client could not be closed. Close it manually and try again.");

        string currentName = GetRiotAccountName();
        bool alreadyActive = !string.IsNullOrWhiteSpace(currentName) && currentName.Equals(account.Username, StringComparison.OrdinalIgnoreCase);

        if (!alreadyActive)
        {
            if (!string.IsNullOrWhiteSpace(currentName)) SaveRiotSessionSnapshot(currentName);
            ClearRiotActiveSession();
            CopyDirectory(snapshot, GetRiotDataPath());
        }

        StartRiotClient();
    }

    private void SaveRiotSessionSnapshot(string accountName)
    {
        string data = GetRiotDataPath();
        if (!Directory.Exists(data)) return;

        string sessionRoot = GetSessionPath("Riot", accountName);
        string target = Path.Combine(sessionRoot, "Data");
        string staging = Path.Combine(sessionRoot, "Data.tmp");

        DeleteDirectoryWithRetry(staging);
        CopyDirectory(data, staging);
        SwapDirectory(staging, target);
    }

    private void ClearRiotActiveSession() => DeleteDirectoryWithRetry(GetRiotDataPath());

    private static void StartRiotClient()
    {
        var candidates = new List<string>();
        string installs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "RiotClientInstalls.json");
        try
        {
            if (File.Exists(installs))
            {
                using JsonDocument document = JsonDocument.Parse(ReadFileShared(installs));
                foreach (string keyName in new[] { "rc_default", "rc_live" })
                {
                    if (document.RootElement.TryGetProperty(keyName, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                    {
                        string installPath = value.GetString() ?? "";
                        if (string.IsNullOrWhiteSpace(installPath)) continue;
                        candidates.Add(Path.GetFileName(installPath).Equals("RiotClientServices.exe", StringComparison.OrdinalIgnoreCase)
                            ? installPath
                            : Path.Combine(installPath, "RiotClientServices.exe"));
                    }
                }
            }
        }
        catch
        {
        }

        candidates.Add(@"C:\Riot Games\Riot Client\RiotClientServices.exe");
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games", "Riot Client", "RiotClientServices.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Riot Games", "Riot Client", "RiotClientServices.exe"));

        string? exe = candidates.FirstOrDefault(File.Exists);
        if (exe == null)
            throw new InvalidOperationException("Riot Client was not found on this computer.");

        try
        {
            Process.Start(new ProcessStartInfo(exe, "--launch-product=riot_client --launch-patchline=live") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Riot Client could not be started.", ex);
        }
    }

    private static string ReadFileShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static bool Plausible(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string trimmed = value.Trim();
        if (trimmed.Length > 64) return false;

        string[] blocked = { "token", "puuid", "unknown", "not set", "not_set" };
        if (blocked.Any(b => trimmed.Contains(b, StringComparison.OrdinalIgnoreCase))) return false;
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)) return false;
        return !trimmed.StartsWith("is not", StringComparison.OrdinalIgnoreCase);
    }

    private static string? MatchNamedValue(string text, string pattern)
    {
        var match = Regex.Match(text, pattern);
        return match.Success ? match.Groups[1].Value.Trim().Trim('"', '\'') : null;
    }

    private bool IsAlreadyAdded(string platform, string username) =>
        AllAccounts.Any(a => a.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase) && a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    private static bool IsSupportedPlatform(string platform) =>
        platform.Equals("Steam", StringComparison.OrdinalIgnoreCase) || platform.Equals("Riot", StringComparison.OrdinalIgnoreCase);

    private static bool KillProcesses(params string[] names)
    {
        bool allClosed = true;
        foreach (string name in names)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                Process[] processes = Process.GetProcessesByName(name);
                if (processes.Length == 0) break;
                foreach (Process process in processes)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
                Thread.Sleep(300);
            }

            Process[] remaining = Process.GetProcessesByName(name);
            if (remaining.Length > 0) allClosed = false;
            foreach (var process in remaining) process.Dispose();
        }
        return allClosed;
    }

    private static string GetSessionPath(string platform, string accountName) =>
        Path.Combine(StoragePaths.SessionsDirectory, platform, SafeFolderName(accountName));

    private static string SafeFolderName(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return value.TrimEnd('.', ' ');
    }

    private void SaveSessionSnapshot(Account account)
    {
        if (account.Platform.Equals("Riot", StringComparison.OrdinalIgnoreCase)) SaveRiotSessionSnapshot(account.Username);
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch { }
            }
        }
        catch
        {
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        if (!Directory.Exists(path)) return;

        Exception? last = null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException ex)
            {
                last = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
            }
            Thread.Sleep(600);
        }

        throw new InvalidOperationException("A session folder is still in use by another process. Close the game launcher completely and try again.", last);
    }

    private static void SwapDirectory(string staging, string target)
    {
        DeleteDirectoryWithRetry(target);
        Directory.Move(staging, target);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
            CopyFileShared(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void CopyFileShared(string source, string destination)
    {
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        string platform = _currentPlatform;
        string username;

        _isBusy = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            username = await Task.Run(() => DetectAccountName(platform));
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _isBusy = false;
        }

        if (string.IsNullOrWhiteSpace(username) && platform == "Riot")
        {
            string? entered = await ShowCustomInputDialogAsync("Riot ID", "Enter Riot ID (e.g. Name#TAG):");
            if (entered == null) return;
            username = entered.Trim();
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            await ShowCustomMessageAsync("Sign In Required", "Sign in to your " + platform + " account first (make sure the launcher is logged in).");
            return;
        }

        if (IsAlreadyAdded(platform, username))
        {
            await ShowCustomMessageAsync("Already Added", "This account (" + username + ") is already added!");
            return;
        }

        var account = CreateAccount(platform, username);

        bool saved = await RunOperationAsync(() => SaveSessionSnapshot(account), "Could Not Save Session");
        if (!saved) return;

        AllAccounts.Add(account);
        RefreshFilteredList();
        await PersistAccountsAsync();
    }

    private async void LoginNewAccount_Click(object sender, RoutedEventArgs e)
    {
        string platform = _currentPlatform;
        await RunOperationAsync(() => LoginNewCore(platform), "Action Failed");
    }

    private void LoginNewCore(string platform)
    {
        if (platform == "Steam")
        {
            if (!CloseSteam())
                throw new InvalidOperationException("Steam could not be closed. Close it manually and try again.");
            SetSteamAutoLogin("");
            LaunchSteam();
        }
        else if (platform == "Riot")
        {
            if (!CloseRiot())
                throw new InvalidOperationException("Riot Client could not be closed. Close it manually and try again.");
            string currentName = GetRiotAccountName();
            if (!string.IsNullOrWhiteSpace(currentName)) SaveRiotSessionSnapshot(currentName);
            ClearRiotActiveSession();
            StartRiotClient();
        }
    }

    private async void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Account account }) return;

        bool confirmed = await ShowCustomConfirmAsync("Delete Account", "Remove \"" + account.DisplayName + "\" and its saved session from this app?");
        if (!confirmed) return;

        AllAccounts.Remove(account);
        RefreshFilteredList();
        await PersistAccountsAsync();

        string sessionPath = GetSessionPath(account.Platform, account.Username);
        await Task.Run(() =>
        {
            try { DeleteDirectoryWithRetry(sessionPath); }
            catch (Exception ex) { AppLog.Write(ex); }
        });
    }

    private async void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Account account }) return;
        await RunOperationAsync(() => SwitchAccountCore(account), "Switch Failed");
    }

    private void SwitchAccountCore(Account account)
    {
        switch (account.Platform.ToLowerInvariant())
        {
            case "steam":
                SwitchSteam(account);
                break;
            case "riot":
                SwitchRiot(account);
                break;
        }
    }

    private async void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Account }) return;
        if (await PersistAccountsAsync())
            await ShowCustomMessageAsync("Saved", "Note saved successfully!");
    }

    private void WriteAccountsFile()
    {
        Directory.CreateDirectory(StoragePaths.Root);
        string json = JsonSerializer.Serialize(AllAccounts, JsonOptions);
        string temp = StoragePaths.AccountsFile + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, StoragePaths.AccountsFile, true);
    }

    private async Task<bool> PersistAccountsAsync()
    {
        try
        {
            WriteAccountsFile();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            await ShowCustomMessageAsync("Save Error", "Could not save accounts: " + ex.Message);
            return false;
        }
    }

    private void LoadAccountsFromJson()
    {
        try
        {
            if (!File.Exists(StoragePaths.AccountsFile)) return;

            string json = File.ReadAllText(StoragePaths.AccountsFile);
            var accounts = JsonSerializer.Deserialize<List<Account>>(json);
            if (accounts == null) return;

            foreach (var account in accounts.Where(a => a != null
                                                        && !string.IsNullOrWhiteSpace(a.Username)
                                                        && !string.IsNullOrWhiteSpace(a.Platform)
                                                        && IsSupportedPlatform(a.Platform)))
                AllAccounts.Add(account);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            try { File.Move(StoragePaths.AccountsFile, StoragePaths.AccountsFile + ".corrupt", true); }
            catch { }
            _startupWarnings.Add("The saved accounts file could not be read and was moved to accounts.json.corrupt. Your list starts empty.");
        }
    }
}

public class Account
{
    private string _avatarPath = "";
    private ImageSource? _avatarSource;

    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Note { get; set; } = "";

    public string AvatarPath
    {
        get => _avatarPath;
        set
        {
            _avatarPath = value ?? "";
            _avatarSource = null;
        }
    }

    [JsonIgnore]
    public ImageSource? AvatarSource => _avatarSource ??= ResolveAvatar();

    [JsonIgnore]
    public ImageSource? FrameSource =>
        Platform.Equals("Steam", StringComparison.OrdinalIgnoreCase) ? AssetImages.Load("cat_frame.png") : null;

    private ImageSource? ResolveAvatar()
    {
        ImageSource? custom = LoadCustomAvatar();
        return custom ?? AssetImages.Load(PlatformIconName());
    }

    private string PlatformIconName() => Platform.ToLowerInvariant() switch
    {
        "steam" => "steam.png",
        "riot" => "riot.png",
        _ => ""
    };

    private ImageSource? LoadCustomAvatar()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(AvatarPath)) return null;

            if (AvatarPath.StartsWith("/Assets/", StringComparison.OrdinalIgnoreCase))
                return AssetImages.Load(Path.GetFileName(AvatarPath));

            if (Uri.TryCreate(AvatarPath, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return new BitmapImage(uri);

            if (!File.Exists(AvatarPath)) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(AvatarPath, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}

internal static class StoragePaths
{
    static StoragePaths()
    {
        string baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDirectory)) baseDirectory = Path.GetTempPath();

        Root = Path.Combine(baseDirectory, "AccountSwitcher");
        try
        {
            Directory.CreateDirectory(Root);
        }
        catch
        {
            Root = Path.Combine(Path.GetTempPath(), "AccountSwitcher");
            try { Directory.CreateDirectory(Root); } catch { }
        }
    }

    public static string Root { get; private set; }
    public static string AccountsFile => Path.Combine(Root, "accounts.json");
    public static string SessionsDirectory => Path.Combine(Root, "SavedSessions");
}

internal static class AssetImages
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Load(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        return Cache.GetOrAdd(fileName, Create);
    }

    private static ImageSource? Create(string fileName)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri("pack://application:,,,/Assets/" + fileName, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}