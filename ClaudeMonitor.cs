/*
# This is basically .Net Core 9 C# code wrapped in a PowerShell loader.
# so this means you should heavily use the following language features where possible:
#   * target typed new (preferred over var)
#   * var
#   * expression bodies members
#   * generics
#   * anonymous classes
#   * record / record struct
#   * sealed and readonly
#   * primary constructors
#   * required and init fields
#   * ternary operator  ? :
#   * null-forgiving operator    !
#   * null-coalescing operator   ??
#   * null-propagation operator  ?.
#   * null-assignment operator   ??=
#   * switch expression
#   * pattern matching
#   * collection initializer
#   * tuple deconstruction (including tuple swap pattern)
#   * tuple naming
#   * range and indexer [^]
#   * spread operator [..]
#   * using declaration
#   * local methods
#   * nested types
#   * yield
#   * string interpolation $""
#   * raw string literals @"""
#  and we enforce some syntax rules
#   * K&R style brackets
#   * 2 spaces indentation
#   * omitting braces when possible
#   * use early returns, continue, break and shortcut operators
*/

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualBasic.ApplicationServices;
using StreamReader = System.IO.StreamReader;

public sealed class __Program__ {
  static readonly TimeSpan CHECK_INTERVAL = TimeSpan.FromSeconds(5);
  static readonly TimeSpan DISPLAY_UPDATE = TimeSpan.FromSeconds(1);
  static readonly TimeSpan BREAK_INTERVAL = TimeSpan.FromMilliseconds(100);
  const double INPUT_COST_PER_1K = 0.003, OUTPUT_COST_PER_1K = 0.015;
  private const uint TOKEN_LIMIT_PER_WINDOW = 150000;

  const string RESET = "\u001b[0m", RED = "\u001b[31m", GREEN = "\u001b[32m", YELLOW = "\u001b[33m";
  const string BLUE = "\u001b[34m", MAGENTA = "\u001b[35m", CYAN = "\u001b[36m", WHITE = "\u001b[37m", GRAY = "\u001b[90m";
  const string BRIGHT_RED = "\u001b[91m", BRIGHT_GREEN = "\u001b[92m", BRIGHT_YELLOW = "\u001b[93m";
  const string BRIGHT_BLUE = "\u001b[94m", BRIGHT_MAGENTA = "\u001b[95m", BRIGHT_CYAN = "\u001b[96m", BRIGHT_WHITE = "\u001b[97m";
  
  static readonly Dictionary<string, string> emojisUnicode = new() {
    { "check", "\u2713" }, { "cross", "\u2717" }, { "warning", "\u26A0" }, { "info", "\u2139" },
    { "clock", "\u23F0" }, { "gear", "\u2699" }, { "rocket", "\uD83D\uDE80" }, { "fire", "\uD83D\uDD25" },
    { "computer", "\uD83D\uDCBB" }, { "robot", "\uD83E\uDD16" }, { "money", "\uD83D\uDCB0" }, { "chart", "\uD83D\uDCCA" },
    { "hourglass", "\u23F3" }, { "lightning", "\u26A1" }, { "circle", "\u25CB" }, { "dot", "\u2022" },
    { "arrow", "\u25B6" }, { "star", "\u2B50" }, { "target", "\uD83C\uDFAF" }
  };

  private static readonly Dictionary<string, string> emojisAscii = new() {
    { "check", "[+]" }, { "cross", "[X]" }, { "warning", "[!]" }, { "info", "[i]" },
    { "clock", "[T]" }, { "gear", "[*]" },
    { "rocket", "[^]" }, { "fire", "[F]" }, { "computer", "[C]" }, { "robot", "[R]" },
    { "money", "[$]" }, { "chart", "[#]" }, { "hourglass", "[H]" }, { "lightning", "[L]" },
    { "circle", "[O]" }, { "dot", "[.]" }, { "arrow", "[>]" }, { "star", "[*]" }, { "target", "[@]" }
  };

  static readonly Dictionary<string, AnthropicAccount> _accounts = new();
  static readonly Dictionary<int, ClaudeProcess> _activeProcesses = new();

  // info you can parse from .claude.json/oauthAccount
  public sealed class AnthropicAccount(string? uuid, string? emailAdress) {
    public static TimeSpan WindowLength = TimeSpan.FromHours(5);

    // Confidence of our inferred window start.
    private enum ConfidenceLevel {
      None,
      Assumed,
      Proven
    }

    public string? Uuid => uuid;
    public string? EmailAdress => emailAdress;

    private DateTime _currentWindowStart;
    private ConfidenceLevel _confidence = ConfidenceLevel.None;

    /// <summary>
    ///   Tracks the inferred start of the current 5-hour rate limit window for this account.
    ///   Inference rules:
    ///   - First message anchors to its hour (assumed).
    ///   - Message outside the current 5h span starts a new window at its hour (assumed).
    ///   - Rate limit evidence proves the window is [nextStart-5h, nextStart) (proven),
    ///   which can retroactively shift our start earlier or later.
    /// </summary>
    public DateTime CurrentWindowStart {
      get => this._currentWindowStart;
      private set {
        this._currentWindowStart = value;
        this._CheckSchedule();
      }
    }

    private bool _isRateLimited;

    public bool IsRateLimited {
      get => this._isRateLimited;
      private set {
        this._isRateLimited = value;
        this._CheckSchedule();
      }
    }

    public DateTime CurrentWindowEnd => this.CurrentWindowStart.Add(WindowLength);

    public DateTime? NextContinueEvent { get; private set; }

    public DateTime? LastKnownMessage { get; private set; }

    public void IncomingMessage(DateTime when) {
      // Ignore out-of-order observations.
      if (this.LastKnownMessage != null && when < this.LastKnownMessage)
        return;

      this.LastKnownMessage = when;

      // Initialize if needed.
      if (this._confidence is ConfidenceLevel.None) {
        this.CurrentWindowStart = FloorToHour(when);
        this._confidence = ConfidenceLevel.Assumed;
        return;
      }

      // If the message is outside the active window, start a new assumed window at the message hour.
      if (when >= this.CurrentWindowEnd) {
        this.CurrentWindowStart = FloorToHour(when);
        this._confidence = ConfidenceLevel.Assumed;
        // Message proves we're sending; being rate-limited would contradict that.
        this.IsRateLimited = false;
      }
      // else: message falls inside the current window -> no change.
    }

    /// <summary>
    ///   Server-reported rate-limit event:
    ///   <paramref name="nextWindowStart" /> is when the next window begins.
    ///   Therefore the current (proven) window is [nextWindowStart - 5h, nextWindowStart).
    /// </summary>
    public void IncomingRateLimit(DateTime nextWindowStart) {
      var provenStart = nextWindowStart - WindowLength;
      if (this._confidence is ConfidenceLevel.None || this.CurrentWindowStart != provenStart)
        this.CurrentWindowStart = provenStart;

      this._confidence = ConfidenceLevel.Proven;
      this.IsRateLimited = true;
    }

    private void _CheckSchedule() =>
      this.NextContinueEvent = this.IsRateLimited
        ? this.CurrentWindowEnd.AddMinutes(1).AddSeconds(Random.Shared.Next(0, 60))
        : null;

    private static DateTime FloorToHour(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind);

  }

  public class SessionInfo(AnthropicAccount account,DateTime sessionStart) {
    public AnthropicAccount Account { get; set; } = account;
    public DateTime SessionStart => sessionStart;
    public DateTime? LastKnownMessage { get; private set; }
    public DateTime? LastParsedTimestamp { get; set; }

    public readonly HashSet<string> _usedModels = new();
    public IEnumerable<string> UsedModels => this._usedModels;
    public uint TotalInputTokens { get; set; }
    public uint TotalOutputTokens { get; set; }
    public float BurnRateTokensPerMinute { get; set; }
    public float TotalCost { get; set; }
    public float ProjectedTotalCost { get; set; }

    public void IncomingMessage(DateTime when) {
      if (when > this.LastKnownMessage)
        this.LastKnownMessage = when;

      this.Account.IncomingMessage(when);
    }

  }

  public class ClaudeProcess(int pid,string name,string cmd,string cfg,FileInfo settings,DateTime started, string friendlyConfigurationName,AnthropicAccount account) {
    public int ProcessId => pid;
    public string ProcessName => name;
    public string CommandLine => cmd;
    public DateTime StartTime => started;
    public double WorkingSet { get; set; }
    public string ConfigPath => cfg;
    public FileInfo ClaudeSettingsFile=> settings;
    public DirectoryInfo? WorkingDirectory { get; set; }
    public string FriendlyConfigurationName => friendlyConfigurationName;
    public IntPtr? ConsoleHandle { get; set; }
    public bool HasConsoleWindow => this.ConsoleHandle.HasValue;
    public SessionInfo Session { get; }=new SessionInfo(account,started);
    public DateTime? LastContinueSent { get; private set; }
    public int NumberOfContinuesSent { get; private set; }

    public void SendContinueToConsole() {
      if (this.ConsoleHandle is not { } handle)
        return;

      try {
        ShowWindow(handle, SW_RESTORE);
        Thread.Sleep(200);
        SetForegroundWindow(handle);
        Thread.Sleep(300);

        SendKeys.SendWait("continue");
        Thread.Sleep(100);
        SendKeys.SendWait("{ENTER}");

        this.LastContinueSent = DateTime.Now;
        ++this.NumberOfContinuesSent;
      } catch {
        ;
      }
    }
  }


  [StructLayout(LayoutKind.Sequential)]
  public struct RECT {
    public int Left, Top, Right, Bottom;
  }

  static byte[] ReadRemoteMemory(IntPtr hProcess, IntPtr remoteAddress, int size) {
    var buffer = new byte[size];

    if (!ReadProcessMemory(hProcess, remoteAddress, buffer, buffer.Length, out var bytesRead) || bytesRead.ToInt32() != size)
      throw new InvalidOperationException($"Failed to read remote {size} bytes of memory.");

    return buffer;
  }

  static T ReadRemoteStruct<T>(IntPtr hProcess, IntPtr remoteAddress) where T : struct {
    var buffer = ReadRemoteMemory(hProcess, remoteAddress, Marshal.SizeOf<T>());
    var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
    try {
      return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject())!;
    } finally {
      handle.Free();
    }
  }


  [StructLayout(LayoutKind.Sequential)]
  struct PROCESS_BASIC_INFORMATION {
    public IntPtr Reserved1, PebBaseAddress, Reserved2_0, Reserved2_1, UniqueProcessId, InheritedFromUniqueProcessId;
  }

  [StructLayout(LayoutKind.Sequential)]
  struct PEB {
    public byte InheritedAddressSpace, ReadImageFileExecOptions, BeingDebugged, BitField;
    public IntPtr Mutant, ImageBaseAddress, Ldr, ProcessParameters;
    public IntPtr SubSystemData, ProcessHeap, FastPebLock;
    public IntPtr AtlThunkSListPtr, IFEOKey, CrossProcessFlags;
    public IntPtr UserSharedInfoPtr, SystemReserved, AtlThunkSListPtr32;
    public IntPtr ApiSetMap;
  }

  [StructLayout(LayoutKind.Sequential)]
  struct UNICODE_STRING {
    public ushort Length, MaximumLength;
    public IntPtr Buffer;

    public string ToString(IntPtr hProcess) => this.Buffer == IntPtr.Zero || this.Length == 0 
      ? string.Empty 
      : Encoding.Unicode.GetString(ReadRemoteMemory(hProcess, this.Buffer, this.Length))
      ;
  }

  [StructLayout(LayoutKind.Sequential)]
  struct STRING {
    public ushort Length, MaximumLength;
    public IntPtr Buffer;

    public string ToString(IntPtr hProcess) => this.Buffer == IntPtr.Zero || this.Length == 0 
      ? string.Empty 
      : Encoding.ASCII.GetString(ReadRemoteMemory(hProcess, this.Buffer, this.Length))
      ;
  }

  [StructLayout(LayoutKind.Sequential)]
  struct CURDIR {
    public UNICODE_STRING DosPath;
    public IntPtr Handle;
  }

  [StructLayout(LayoutKind.Sequential)]
  struct RTL_DRIVE_LETTER_CURDIR {
    public ushort Flags;
    public ushort Length;
    public uint TimeStamp;
    public STRING DosPath;
  }

  [StructLayout(LayoutKind.Sequential)]
  struct RTL_USER_PROCESS_PARAMETERS {
    public uint MaximumLength;
    public uint Length;
    public uint Flags;
    public uint DebugFlags;
    public IntPtr ConsoleHandle;
    public uint ConsoleFlags;
    public IntPtr StandardInput;
    public IntPtr StandardOutput;
    public IntPtr StandardError;
    public CURDIR CurrentDirectory;
    public UNICODE_STRING DllPath;
    public UNICODE_STRING ImagePathName;
    public UNICODE_STRING CommandLine;
    public IntPtr Environment; // <-- pointer to environment block
    public uint StartingX;
    public uint StartingY;
    public uint CountX;
    public uint CountY;
    public uint CountCharsX;
    public uint CountCharsY;
    public uint FillAttribute;
    public uint WindowFlags;
    public uint ShowWindowFlags;
    public UNICODE_STRING WindowTitle;
    public UNICODE_STRING DesktopInfo;
    public UNICODE_STRING ShellInfo;

    public UNICODE_STRING RuntimeData;

    // Note: RTL_MAX_DRIVE_LETTERS is 32, but we'll use a fixed array for simplicity
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public RTL_DRIVE_LETTER_CURDIR[] CurrentDirectories;

    public UIntPtr EnvironmentSize;
    public UIntPtr EnvironmentVersion;
    public IntPtr PackageDependencyData;
    public uint ProcessGroupId;
    public uint LoaderThreads;
    public UNICODE_STRING RedirectionDllName; // REDSTONE4
    public UNICODE_STRING HeapPartitionName; // 19H1
    public IntPtr DefaultThreadpoolCpuSetMasks; // PULONGLONG
    public uint DefaultThreadpoolCpuSetMaskCount;
    public uint DefaultThreadpoolThreadMaximum;
    public uint HeapMemoryTypeMask; // WIN11
  }

  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

  [DllImport("user32.dll")]
  public static extern bool GetWindowRect(IntPtr hWnd, ref RECT lpRect);

  [DllImport("user32.dll")]
  public static extern bool SetForegroundWindow(IntPtr hWnd);

  [DllImport("user32.dll")]
  public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

  [DllImport("user32.dll")]
  public static extern IntPtr GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

  [DllImport("user32.dll")]
  public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

  [DllImport("user32.dll")]
  public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

  [DllImport("user32.dll")]
  public static extern int GetWindowTextLength(IntPtr hWnd);

  [DllImport("user32.dll")]
  public static extern bool IsWindowVisible(IntPtr hWnd);

  [DllImport("user32.dll")]
  public static extern IntPtr GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

  [DllImport("kernel32.dll", SetLastError = true)]
  static extern IntPtr GetStdHandle(int nStdHandle);

  [DllImport("kernel32.dll")]
  static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

  [DllImport("kernel32.dll")]
  static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

  [DllImport("kernel32.dll", SetLastError = true)]
  public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  static extern bool CloseHandle(IntPtr hObject);

  [DllImport("kernel32.dll", SetLastError = true)]
  static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

  [DllImport("ntdll.dll")]
  static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, uint processInformationLength, out uint returnLength);

  public const int SW_RESTORE = 9, SW_SHOW = 5;
  const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_VM_READ = 0x0010;

  static bool? ansiSupported, unicodeSupported;
  static void WriteAnsi(string text) {
    unicodeSupported ??= GetUnicodeSupport();
    ansiSupported ??= GetAnsiSupport();

    var emojiSet = unicodeSupported.Value ? emojisUnicode : emojisAscii;
    foreach (var emoji in emojiSet)
      text = text.Replace(":" + emoji.Key + ":", emoji.Value);
    
    if (!ansiSupported.Value)
      text = StripAnsiAndApplyColors(text);

    Console.Write(text);
  }

  static bool GetUnicodeSupport() {
    try {
      var encoding = Console.OutputEncoding;
      return encoding.CodePage == 65001 || encoding.CodePage == 1200 || encoding.CodePage == 1201 ||
             encoding.EncodingName.Contains("Unicode") ||
             encoding.EncodingName.Contains("UTF");
    } catch {
      return false;
    }
  }

  static bool GetAnsiSupport() {
    try {
      var handle = GetStdHandle(-11);
      GetConsoleMode(handle, out var mode);
      SetConsoleMode(handle, mode | 0x4);
      return true;
    } catch {
      return false;
    }
  }

  static string StripAnsiAndApplyColors(string text) {
    Dictionary<string, ConsoleColor> colorMap = new() {
      [GREEN] = ConsoleColor.Green,
      [RED] = ConsoleColor.Red,
      [YELLOW] = ConsoleColor.Yellow,
      [BLUE] = ConsoleColor.Blue,
      [MAGENTA] = ConsoleColor.Magenta,
      [CYAN] = ConsoleColor.Cyan,
      [WHITE] = ConsoleColor.White,
      [GRAY] = ConsoleColor.DarkGray,
      [BRIGHT_GREEN] = ConsoleColor.Green,
      [BRIGHT_RED] = ConsoleColor.Red,
      [BRIGHT_YELLOW] = ConsoleColor.Yellow,
      [BRIGHT_BLUE] = ConsoleColor.Blue,
      [BRIGHT_MAGENTA] = ConsoleColor.Magenta,
      [BRIGHT_CYAN] = ConsoleColor.Cyan
    };

    var result = new StringBuilder();
    var currentPos = 0;
    var colorCodes = colorMap.Keys.Concat([RESET]).ToList();

    while (currentPos < text.Length) {
      var nextColorPos = text.Length;
      var nextColor = "";

      foreach (var color in colorCodes) {
        var pos = text.IndexOf(color, currentPos);
        if (pos == -1 || pos >= nextColorPos)
          continue;

        nextColorPos = pos;
        nextColor = color;
      }

      if (nextColorPos > currentPos)
        result.Append(text.Substring(currentPos, nextColorPos - currentPos));

      if (nextColor != "") {
        if (nextColor == RESET)
          Console.ResetColor();
        else if (colorMap.TryGetValue(nextColor, out var color))
          Console.ForegroundColor = color;

        currentPos = nextColorPos + nextColor.Length;
      } else {
        break;
      }
    }

    return Regex.Replace(result.ToString(), @"\u001b\[[0-9;]*m", "");
  }

  static void WriteLineAnsi(string? text = null) => WriteAnsi(text + "\n");

  static void RefreshProcesses() {
    var activeProcesses = _activeProcesses;
    var processesKilled = _activeProcesses.Keys.ToHashSet();

    const string wmiQuery = "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'node.exe'";
    using var searcher = new ManagementObjectSearcher(wmiQuery);
    using var processes = searcher.Get();
    foreach (var wmiProcess in processes) {
      var cmd = wmiProcess["CommandLine"]?.ToString() ?? string.Empty;
      if (!cmd.Contains("claude-code"))
        continue;

      var pid = (int)(uint)wmiProcess["ProcessId"];
      var process = Process.GetProcessById(pid);

      // we already know this one
      if (processesKilled.Remove(pid)) {
        var claudeProcess= activeProcesses[pid];
        claudeProcess.WorkingSet = process.WorkingSet64;
        
        // for some reason we still don't know which account is associated, try to refresh
        if (claudeProcess.Session.Account.Uuid == null) {
          var file = claudeProcess.ClaudeSettingsFile;
          file.Refresh();
          if(!file.Exists)
            continue;

          claudeProcess.Session.Account = GetOrCreateAccount(file);
        }

        continue;
      }

      var (envVars,consoleHandle,workingDirectory) = GetProcessInformation(pid);
      var requestedConfigurationPath =
          envVars == null ? null:
          envVars.GetValueOrDefault("CLAUDE_CONFIG_DIR")
          ?? envVars.GetValueOrDefault("ANTHROPIC_CONFIG_DIR")
          ?? envVars.GetValueOrDefault("HOME")
        ;
      var realConfigurationPath =
          requestedConfigurationPath
          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
        ;

      FileInfo claudeSettingsFile = new(Path.Combine(requestedConfigurationPath ?? Path.Combine(realConfigurationPath, ".."), ".claude.json"));

      var account = GetOrCreateAccount(claudeSettingsFile);
      var friendlyName = requestedConfigurationPath == null ? "~" : Path.GetFileName(requestedConfigurationPath);

      activeProcesses.Add(pid, new(pid, process.ProcessName, cmd, realConfigurationPath, claudeSettingsFile, process.StartTime, friendlyName, account) {
        WorkingSet = process.WorkingSet64,
        WorkingDirectory = workingDirectory,
        ConsoleHandle = consoleHandle
      });
    }

    foreach (var pid in processesKilled)
      activeProcesses.Remove(pid);
  }

  static (Dictionary<string, string>? env,IntPtr? consoleHandle, DirectoryInfo? workingDirectory) GetProcessInformation(int processId) {
    try {
      var hProc = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);
      if (hProc == IntPtr.Zero) {
        Console.WriteLine("Debug: Failed to open process " + processId);
        return (null,null,null);
      }

      var pbi = new PROCESS_BASIC_INFORMATION();
      var status = NtQueryInformationProcess(hProc, 0, ref pbi, (uint)Marshal.SizeOf(pbi), out var retLen);
      if (status != 0) {
        Console.WriteLine("Debug: NtQueryInformationProcess failed for " + processId + ", status: " + status);
        CloseHandle(hProc);
        return (null, null, null);
      }

      // Read PEB (just enough to get ProcessParameters)
      var peb = new byte[IntPtr.Size * 6];
      if (!ReadProcessMemory(hProc, pbi.PebBaseAddress, peb, peb.Length, out var bytesRead)) {
        Console.WriteLine("Debug: Failed to read PEB for process " + processId);
        CloseHandle(hProc);
        return (null, null, null);
      }

      var procParamsAddr = IntPtr.Size == 8 ? (IntPtr)BitConverter.ToInt64(peb, IntPtr.Size * 4) : (IntPtr)BitConverter.ToInt32(peb, IntPtr.Size * 4);
      var rupp = ReadRemoteStruct<RTL_USER_PROCESS_PARAMETERS>(hProc, procParamsAddr);

      var currentDirectory = rupp.CurrentDirectory.DosPath.ToString(hProc);
      var env = ExtractEnvironmentBlock(hProc, rupp);

      CloseHandle(hProc);

      var result = new Dictionary<string, string>();
      foreach (var line in env) {
        var index = line.LastIndexOf('=');
        if (index < 0)
          continue;

        result.Add(line[..index], line[(index + 1)..]);
      }

      return (result, rupp.ConsoleHandle, new(currentDirectory));
    } catch (Exception ex) {
      Console.WriteLine("Debug: GetEnvironmentVariables error: " + ex.Message);
      return (null, null, null);
    }

    static string[] ExtractEnvironmentBlock(IntPtr hProcess, RTL_USER_PROCESS_PARAMETERS rupp) {
      var buffer = ReadRemoteMemory(hProcess, rupp.Environment, (int)rupp.EnvironmentSize);

      var strings = new List<string>();

      var offset = 0;
      while (offset < buffer.Length - 2) {
        // Find null-terminated strings
        var end = offset;
        while (end + 1 < buffer.Length && (buffer[end] != 0 || buffer[end + 1] != 0))
          end += 2;

        if (end == offset) // found double null (end of block)
          break;

        var s = Encoding.Unicode.GetString(buffer, offset, end - offset);
        strings.Add(s);

        offset = end + 2; // skip null terminator
      }

      return strings.ToArray();
    }
  }

  static IntPtr? GetProcessConsoleWindow(int processId) {
    try {
      var windows = new List<IntPtr>();

      EnumWindows((hWnd, lParam) => {
        try {
          GetWindowThreadProcessId(hWnd, out var windowProcessId);

          if (windowProcessId == processId && IsWindowVisible(hWnd)) {
            var className = new StringBuilder(256);
            GetClassName(hWnd, className, className.Capacity);
            var classNameStr = className.ToString();

            if (classNameStr is "ConsoleWindowClass" or "PseudoConsoleWindow")
              windows.Add(hWnd);
          }
        } catch { }

        return true;
      }, IntPtr.Zero);

      return windows.FirstOrDefault();
    } catch (Exception ex) {
      Console.WriteLine("Debug: GetProcessConsoleWindow error: " + ex.Message);
      return null;
    }
  }

  static (string? uuid, string? email) ReadOAuthAccountInfo(FileInfo claudeSettingsPath) {
    try {
      if (!claudeSettingsPath.Exists)
        return (null, null);

      var contents = SafeReadAllText(claudeSettingsPath);
      if (contents==null)
        return (null, null);

      var jsonContent = FixMissingCommas(contents);
      var json = JsonDocument.Parse(jsonContent);
      var root = json.RootElement.GetProperty("oauthAccount");
      var uuid = root.GetProperty("accountUuid").GetString();
      var email = root.GetProperty("emailAddress").GetString();

      return (uuid, email);
    } catch {
      return (null, null);
    }
  }

  static string? SafeReadAllText(FileInfo file) {
    try {
      using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
      using var reader = new StreamReader(stream);
      return reader.ReadToEnd();
    } catch {
      return null;
    }
  }

  static string FixMissingCommas(string brokenJson) {
    var pattern = @"(?<=[}\]0-9\""])\s*(\r?\n|\s)*\s*(?=""[a-zA-Z0-9_]+""\s*:)";
    return Regex.Replace(brokenJson, pattern, m => "," + m.Value);
  }

  static AnthropicAccount GetOrCreateAccount(FileInfo claudeSettingsPath) {
    var (uuid, email) = ReadOAuthAccountInfo(claudeSettingsPath);
    var accountKey = uuid ?? email ?? claudeSettingsPath.Directory?.Name ?? string.Empty;

    if (_accounts.TryGetValue(accountKey, out var account))
      return account;

    account = new AnthropicAccount(uuid, email);
    _accounts[accountKey] = account;
    return account;
  }


  static FileInfo[] GetAllJsonlFilesForProcess(DirectoryInfo configPath) {
    try {
      DirectoryInfo projectsPath = new(Path.Combine(configPath.FullName, "projects"));
      if (!projectsPath.Exists)
        return [];

      return projectsPath.GetFiles("*.jsonl", SearchOption.AllDirectories)
        .OrderByDescending(f => f.LastWriteTime)
        .Take(2)
        .ToArray();
    } catch {
      return [];
    }
  }

  static void ParseJsonlFilesForProcess(ClaudeProcess process) {
    var jsonlFiles = GetAllJsonlFilesForProcess(new DirectoryInfo(process.ConfigPath));
    var session = process.Session;
    var cwdExtracted = process.WorkingDirectory != null;

    foreach (var file in jsonlFiles) {
      var content = SafeReadAllText(file);
      if (content == null)
        continue;

      var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
      foreach (var line in lines) {
        if (string.IsNullOrWhiteSpace(line))
          continue;

        try {
          using var doc = JsonDocument.Parse(line);

          // 2 message types: from user and from ai
          //"{"parentUuid":null,"isSidechain":false,"userType":"external","cwd":"F:\\...\\Cipher","sessionId":"6151b6fd-...9a","version":"1.0.89","gitBranch":"NewFramework","type":"user","message":{"role":"user","content":"g...do"},"uuid":"42681ba2 - ...a94","timestamp":"2025 - 08 - 24T18:20:02.481Z"}"
          //"{"parentUuid":"4268...a94","isSidechain":false,"userType":"external","cwd":"F:\\...\\Cipher","sessionId":"6151b6fd-...9a","version":"1.0.89","gitBranch":"NewFramework","message":{"id":"msg_01HPiGsNaZuS8TZRMkhqaRE9","type":"message","role":"assistant","model":"claude-sonnet-4-20250514","content":[{"type":"text","text":"I'...d."}],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":4,"cache_creation_input_tokens":21300,"cache_read_input_tokens":0,"cache_creation":{"ephemeral_5m_input_tokens":21300,"ephemeral_1h_input_tokens":0},"output_tokens":1,"service_tier":"standard"}},"requestId":"req_011CSSznTkL6NzoeUM2nesuD","type":"assistant","uuid":"6e72994c-...93","timestamp":"2025-08-24T18:20:05.985Z"}"

          var root = doc.RootElement;

          // Extract timestamp first - we need it for session filtering
          if (!root.TryGetProperty("timestamp", out var timestampProp) ||
              !DateTime.TryParse(timestampProp.GetString(), out var messageTime))
            continue;

          // Skip messages from before this process started (they belong to other sessions)
          if (messageTime < process.StartTime)
            continue;

          // Skip messages we've already processed
          if (session.LastParsedTimestamp.HasValue && messageTime <= session.LastParsedTimestamp.Value)
            continue;

          // Extract working directory from cwd field
          if (!cwdExtracted && root.TryGetProperty("cwd", out var cwdProp)) {
            var cwd = cwdProp.GetString();
            if (!string.IsNullOrEmpty(cwd)) {
              DirectoryInfo dir = new(cwd);
              if (dir.Exists) {
                process.WorkingDirectory = dir;
                cwdExtracted = true;
              }
            }
          }

          // Update account tracking with this message
          session.IncomingMessage(messageTime);
          
          // Look for usage data in message.usage (Claude's JSONL format)
          if (root.TryGetProperty("message", out var messageProp)) {
            
            // Extract model name from message
            var model = "unknown";
            if (messageProp.TryGetProperty("model", out var modelProp))
              model = modelProp.GetString() ?? "unknown";

            // Look for usage data in message.usage
            if (messageProp.TryGetProperty("usage", out var usageProp)) {
              var inputTokens = usageProp.TryGetProperty("input_tokens", out var inputProp) ? inputProp.GetUInt32() : 0;
              var outputTokens = usageProp.TryGetProperty("output_tokens", out var outputProp) ? outputProp.GetUInt32() : 0;
              var cacheCreation = usageProp.TryGetProperty("cache_creation_input_tokens", out var cacheProp) ? cacheProp.GetUInt32() : 0;
              var cacheRead = usageProp.TryGetProperty("cache_read_input_tokens", out var cacheReadProp) ? cacheReadProp.GetUInt32() : 0;

              // Only process if we have valid token counts
              if (inputTokens > 0 || outputTokens > 0) {
                session._usedModels.Add(model);
                session.TotalInputTokens += inputTokens;
                session.TotalOutputTokens += outputTokens;

                var cost = (inputTokens + cacheCreation) * (float)INPUT_COST_PER_1K / 1000.0f + outputTokens * (float)OUTPUT_COST_PER_1K / 1000.0f;
                session.TotalCost += cost;
              }
            }
          }

          // Check for rate limit messages
          if (root.TryGetProperty("message", out var msgProp) &&
              msgProp.TryGetProperty("content", out var contentProp)) {
            
            var text = contentProp.GetString() ?? "";
            var rateLimitPattern = @"(\d+)-hour\s+limit\s+reached.*?resets\s+(\d+)(am|pm)";
            var match = Regex.Match(text, rateLimitPattern, RegexOptions.IgnoreCase);
            
            if (match.Success) {
              var limitHours = int.Parse(match.Groups[1].Value);
              AnthropicAccount.WindowLength=TimeSpan.FromHours(limitHours);

              var resetHour = int.Parse(match.Groups[2].Value);
              var isPM = match.Groups[3].Value.ToLower() == "pm";

              // Convert to 24-hour format
              switch (isPM) {
                case true when resetHour != 12:
                  resetHour += 12;
                  break;
                case false when resetHour == 12:
                  resetHour = 0;
                  break;
              }
              
              // Calculate reset time
              var resetTime = new DateTime(messageTime.Year, messageTime.Month, messageTime.Day, resetHour, 0, 0);
              if (resetTime <= messageTime)
                resetTime = resetTime.AddDays(1);
              
              session.Account.IncomingRateLimit(resetTime);
            }
          }

          // Update the last parsed timestamp
          session.LastParsedTimestamp = messageTime;
          
        } catch {
          // Continue processing other lines even if one line has invalid JSON
        }
      }
    }

    // Update burn rate calculations after parsing all data
    var sessionDuration = (DateTime.Now - session.SessionStart).TotalMinutes;
    if (!(sessionDuration > 0))
      return;

    session.BurnRateTokensPerMinute = (session.TotalInputTokens + session.TotalOutputTokens) / (float)sessionDuration;
    if (!(session.BurnRateTokensPerMinute > 0))
      return;

    var remainingMinutes = (TOKEN_LIMIT_PER_WINDOW - (session.TotalInputTokens + session.TotalOutputTokens)) / session.BurnRateTokensPerMinute;
    if (remainingMinutes > 0)
      session.ProjectedTotalCost = session.TotalCost + remainingMinutes * session.BurnRateTokensPerMinute * (float)OUTPUT_COST_PER_1K / 1000.0f;
  }

  /*
static void ParseJsonlFile(string filePath, AnthropicAccount account) {
  try {
    var lines = File.ReadAllLines(filePath);
    var cwdExtracted = false;
    // TODO: use a real json parser for that file
    foreach (var line in lines) {
      if (string.IsNullOrWhiteSpace(line))
        continue;

      try {
        // Extract working directory from cwd field
        if (!cwdExtracted) {
          var cwdMatch = Regex.Match(line, "\"cwd\"\\s*:\\s*\"([^\"]+)\"");
          if (cwdMatch.Success) {
            var cwd = cwdMatch.Groups[1].Value;
            if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) {
              stats.WorkingDirectory = cwd;
              cwdExtracted = true;
            }
          }
        }

        // Look for usage data in message.usage
        if (line.Contains("\"usage\"") && line.Contains("\"input_tokens\"")) {
          // Extract model name
          string model = "unknown";
          var modelMatch = Regex.Match(line, "\"model\"\\s*:\\s*\"([^\"]+)\"");
          if (modelMatch.Success) {
            model = modelMatch.Groups[1].Value;
          }

          // Extract token counts using improved regex patterns
          var inputTokens = GetJsonIntValue(line, "input_tokens");
          var outputTokens = GetJsonIntValue(line, "output_tokens");
          var cacheCreation = GetJsonIntValue(line, "cache_creation_input_tokens");
          var cacheRead = GetJsonIntValue(line, "cache_read_input_tokens");

          // Only process if we have valid token counts
          if (inputTokens > 0 || outputTokens > 0) {
            if (!stats.ModelBreakdown.ContainsKey(model))
              stats.ModelBreakdown[model] = new ModelUsage();

            var modelUsage = stats.ModelBreakdown[model];
            modelUsage.InputTokens += inputTokens;
            modelUsage.OutputTokens += outputTokens;
            modelUsage.CacheCreation += cacheCreation;
            modelUsage.CacheRead += cacheRead;
            ++modelUsage.Requests;

            stats.TotalInputTokens += inputTokens;
            stats.TotalOutputTokens += outputTokens;
            stats.TotalCacheCreation += cacheCreation;
            stats.TotalCacheRead += cacheRead;
            ++stats.TotalRequests;

            var cost = (inputTokens + cacheCreation) * INPUT_COST_PER_1K / 1000.0 + outputTokens * OUTPUT_COST_PER_1K / 1000.0;
            stats.TotalCost += cost;
            modelUsage.TotalCost += cost;
            stats.LastActivity = DateTime.Now;

            // Extract timestamp and update account window tracking
            var timestampMatch = Regex.Match(line, "\"timestamp\"\\s*:\\s*\"([^\"]+)\"");
            if (timestampMatch.Success && DateTime.TryParse(timestampMatch.Groups[1].Value, out var messageTime))
              account?.IncomingMessage(messageTime);
          }
        }
      } catch (Exception ex) {
        Console.WriteLine("Debug: JSON parsing error on line: " + ex.Message);
        // Continue processing other lines even if one line has invalid JSON
      }
    }
  } catch (Exception ex) {
    Console.WriteLine("Debug: ParseJsonlFile error: " + ex.Message);
  }
}

  */ 

static string GenerateUsageText(SessionInfo session, DateTime currentTime) {
  var totalTokens = session.TotalInputTokens + session.TotalOutputTokens;
  var sessionDuration = (currentTime - session.SessionStart).TotalMinutes;
  var baseText = $"{totalTokens:N0} tokens | ${session.TotalCost:F3} | {sessionDuration:F1}min";

  if (session.BurnRateTokensPerMinute <= 0)
    return baseText;

  var burnRateText = $" | {session.BurnRateTokensPerMinute:F1}/min";
  var projectionText = session.ProjectedTotalCost > 0 ? $" -> ${session.ProjectedTotalCost:F2}" : "";

  return $"{baseText}{burnRateText}{projectionText}";
}
  
  static string GetLatestLogFile(string configPath) {
    try {
      var projectsPath = Path.Combine(configPath, "projects");
      if (!Directory.Exists(projectsPath))
        return null;

      return Directory.GetDirectories(projectsPath)
        .SelectMany(dir => Directory.GetFiles(dir, "*.jsonl"))
        .OrderByDescending(File.GetLastWriteTime)
        .FirstOrDefault();
    } catch {
      return null;
    }
  }

  static void ShowStatus() {
    var claudeProcesses = _activeProcesses.Values;

    try {
      Console.Clear();
    } catch {
      WriteLineAnsi();
      WriteLineAnsi();
      WriteLineAnsi();
    }

    var currentTime = DateTime.Now;

    WriteLineAnsi($"{GREEN}============================================================================={RESET}");
    WriteLineAnsi($"{GREEN}             :robot:  ULTIMATE CLAUDE BACKGROUND MONITOR  :rocket:  {RESET}");
    WriteLineAnsi($"{GREEN}============================================================================={RESET}");
    WriteLineAnsi($"{YELLOW}:clock: Current Time: {currentTime:yyyy-MM-dd HH:mm:ss}{RESET}");
    WriteLineAnsi();

    if (claudeProcesses.Count == 0) {
      WriteLineAnsi($"{RED}:cross: No Claude processes found! Waiting for Claude Code to start...{RESET}");
      WriteLineAnsi();
      WriteLineAnsi($"{CYAN}+--- SUMMARY ------------------------------------------------------------------+{RESET}");
      WriteLineAnsi($"{CYAN}| {YELLOW}:hourglass: WAIT{CYAN} | Scanning for Claude Code processes... Press Ctrl+C to stop{WHITE} |{RESET}");
      WriteLineAnsi($"{CYAN}+------------------------------------------------------------------------------+{RESET}");
      WriteLineAnsi();
      WriteLineAnsi($"{RED}:warning: Monitoring continues - Press Ctrl+C to stop...{RESET}");
      return;
    }

    var instanceNumber = 1;
    foreach (var process in claudeProcesses) {
      ShowInstanceStatusBar(instanceNumber, process, currentTime);
      ++instanceNumber;
    }

    var totalCost = claudeProcesses.Sum(p => p.Session.TotalCost);
    var totalTokens = claudeProcesses.Sum(p => p.Session.TotalInputTokens + p.Session.TotalOutputTokens);
    var totalRequests = 0;
    var discoveredConfigs = claudeProcesses.Select(c => c.ConfigPath).Distinct().Count();
    var totalContinues = claudeProcesses.Sum(s => s.NumberOfContinuesSent);

    WriteLineAnsi($"{GREEN}+--- SUMMARY ------------------------------------------------------------------+{RESET}");
    WriteLineAnsi($"| {WHITE}:computer: Configs: {discoveredConfigs} | :gear: Processes: {claudeProcesses.Count} | :chart: Total: {totalTokens:N0} tokens | :money: ${totalCost:F4} | :arrow: {totalContinues} continues{GREEN}|{RESET}");
    WriteLineAnsi($"{GREEN}+------------------------------------------------------------------------------+{RESET}");
    WriteLineAnsi("");
    WriteLineAnsi($"{RED}:warning: Press Ctrl+C to stop monitoring...{RESET}");
  }

  static void ShowInstanceStatusBar(int number, ClaudeProcess process, DateTime currentTime) {
    var session = process.Session;
    var account = session.Account;
    var (statusEmoji, statusText, statusColor) = account.IsRateLimited switch {
      _ when account.Uuid == null => (":dot:", "NO DATA", GRAY),
      true => (":cross:", "RATE LIMITED", BRIGHT_RED),
      _ => session.LastKnownMessage switch {
        null => (":circle:", "AVAILABLE", CYAN),
        var lastActivity => (currentTime - lastActivity.Value).TotalMinutes switch {
          < 2 => (":lightning:", "ACTIVE NOW", BRIGHT_GREEN),
          < 30 => (":check:", "RECENT ACTIVITY", GREEN),
          _ => (":circle:", "IDLE", YELLOW)
        }
      }
    };

    var workingDir = process.WorkingDirectory;
    var workingDirName = workingDir?.Name;
    
    var uptime = (currentTime - process.StartTime).TotalMinutes;
    var uptimeStr = uptime < 60 ? $"{uptime:F1}min" : $"{uptime / 60:F1}h";
    var accountInfo = account.EmailAdress is {} email ? $" ({email})" : string.Empty;
    var projectText = $"{workingDirName} [{process.FriendlyConfigurationName}]{accountInfo} (PID:{process.ProcessId}, {process.WorkingSet/1024/1024:0}MB) :clock: {uptimeStr}";
    
    var modelText = session.UsedModels.Count() switch {
      0 => "No Model",
      1 => session.UsedModels.First().Replace("claude-", "").Replace("-20", ""),
      var count => $"{count} models"
    };

    var usageText = session is { TotalInputTokens: 0, TotalOutputTokens: 0 } ? "No Usage" :GenerateUsageText(session, currentTime);

    var windowText = "No window data";
    var windowColor = BLUE;

    /*
        
    TokenDepletion tokenDepletion = null;

    if (stats != null) {
      try {
        var accountKey = $"{account.Uuid}:{account.EmailAdress}";
        var rateLimitState = rateLimitStates.GetValueOrDefault(accountKey);

        // Use the new AnthropicAccount window tracking
        var windowStart = account.CurrentWindowStart;
        var windowEnd = account.CurrentWindowEnd;

        // Calculate window number based on how many 5-hour periods since session start
        var hoursSinceSessionStart = windowStart.Subtract(stats.SessionStart).TotalHours;
        var windowNumber = Math.Max(1, (int)Math.Floor(hoursSinceSessionStart / 5.0) + 1);

        // Set the window text with calculated values
        var windowDisplay = $"{windowStart:HH:mm}-{windowEnd:HH:mm}";
        windowText = $"{windowDisplay} | Window {windowNumber}";

        var timeRemaining = windowEnd.Subtract(currentTime);

        if (timeRemaining.TotalMinutes > 0) {
          var timeRemainingText = timeRemaining.TotalMinutes < 60
            ? $"{timeRemaining.TotalMinutes:F1}min left"
            : $"{Math.Floor(timeRemaining.TotalHours)}h{timeRemaining.Minutes}m left";
          windowText += $" :clock: {timeRemainingText}";
        }

        var currentTokens = stats.TotalInputTokens + stats.TotalOutputTokens;
        tokenDepletion = CalculateTokenDepletion(currentTokens, stats.BurnRateTokensPerMinute, 150000);
      } catch (Exception ex) {
        windowText = "Window calc error: " + ex.Message;
        windowColor = RED;
      }

      if (tokenDepletion?.ProjectedDepletionDisplay != "No burn rate data") {
        windowText += " :target: Tokens out in: " + tokenDepletion.ProjectedDepletionDisplay;

        if (tokenDepletion.MinutesToDepletion.HasValue) {
          if (tokenDepletion.MinutesToDepletion < 60)
            windowColor = BRIGHT_RED;
          else if (tokenDepletion.MinutesToDepletion < 300)
            windowColor = BRIGHT_YELLOW;
          else
            windowColor = BRIGHT_BLUE;
        }
      } else {
        windowText += " :chart: " + tokenDepletion?.TokensRemaining.ToString("N0") + " tokens left";
      }
    }
    */

    WriteLineAnsi($"{GRAY}+------------------------------------------------------------------------------+{RESET}");
    WriteLineAnsi($"| {statusColor}{statusEmoji} {statusText, -74}{WHITE} |{RESET}");
    WriteLineAnsi($"| {MAGENTA}:computer: {projectText, -78}{WHITE} |{RESET}");
    WriteLineAnsi($"| {CYAN}:robot: {modelText, -73}{WHITE} |{RESET}");
    WriteLineAnsi($"| {GREEN}:money: {usageText ,-73}{WHITE} |{RESET}");
    WriteLineAnsi($"| {windowColor}:hourglass: {windowText, -78}{WHITE} |{RESET}");
    WriteLineAnsi($"{GRAY}+------------------------------------------------------------------------------+{RESET}");
    WriteLineAnsi();
  }

  public static void Main() {
    Console.OutputEncoding = Encoding.UTF8;
    WriteLineAnsi("Starting Ultimate Claude Background Monitor...");
    WriteLineAnsi("Auto-discovering Claude configurations...");
    WriteLineAnsi();

    var lastDisplayUpdate = DateTime.MinValue;
    var isRunning = true;

    Console.CancelKeyPress += (_, e) => {
      e.Cancel = true;
      isRunning = false;
      WriteLineAnsi();
      WriteLineAnsi($"{YELLOW}Shutting down Ultimate Claude Monitor...{RESET}");
    };

    while (isRunning) {
      RefreshProcesses();

      var currentTime = DateTime.Now;
      var claudeProcesses = _activeProcesses;
      
      // Update statistics for all processes
      foreach (var process in claudeProcesses.Values)
        ParseJsonlFilesForProcess(process);
      
      // Send continue when it's time
      foreach (var process in claudeProcesses.Values) {

        var account = process.Session.Account;
        if (account is not { IsRateLimited: true, NextContinueEvent: not null })
          continue;

        var timeUntilContinue = (account.NextContinueEvent.Value - currentTime).TotalMinutes;
        if (timeUntilContinue <= 0)
          process.SendContinueToConsole();
      }

      // Display update
      if (currentTime >= lastDisplayUpdate + DISPLAY_UPDATE) {
        ShowStatus();
        lastDisplayUpdate = currentTime;
      }

      // Wait, but make sure no one has cancelled us
      for (var i = CHECK_INTERVAL.TotalMilliseconds / BREAK_INTERVAL.Milliseconds; i > 0 && isRunning; --i)
        Thread.Sleep(BREAK_INTERVAL);
    }

    WriteLineAnsi("Ultimate Claude Monitor stopped.");
  }
}
