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
using StreamReader = System.IO.StreamReader;

public sealed class __Program__ {
  static readonly TimeSpan CHECK_INTERVAL = TimeSpan.FromSeconds(5);
  static readonly TimeSpan DISPLAY_UPDATE = TimeSpan.FromSeconds(1);
  static readonly TimeSpan BREAK_INTERVAL = TimeSpan.FromMilliseconds(100);
  private const string _CLAUDE_CONFIGURATION_FILENAME = ".claude.json";

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

  // Account type detection based on found evidence
  public enum AccountType {
    Unknown,
    Free,
    Pro,
    Max,
    Enterprise
  }
  
  // Token limits per account type (based on research)
  public static readonly Dictionary<AccountType, uint> TokenLimits = new() {
    { AccountType.Unknown, 150000 },     // Default fallback
    { AccountType.Free, 50000 },         // Estimated based on ~20-30 messages/day
    { AccountType.Pro, 150000 },         // Estimated based on ~45 messages/5hr
    { AccountType.Max, 500000 },         // 20x Pro usage
    { AccountType.Enterprise, 1000000 }, // Large context window + unlimited
  };

  // info you can parse from .claude.json/oauthAccount
  public sealed class AnthropicAccount(string? uuid, string? emailAdress) {
    public static TimeSpan WindowLength = TimeSpan.FromHours(5);
    public const double INPUT_COST_PER_1K = 0.003, OUTPUT_COST_PER_1K = 0.015;
    
    public AccountType DetectedAccountType { get; private set; } = AccountType.Unknown;
    public uint TOKEN_LIMIT_PER_WINDOW => TokenLimits[this.DetectedAccountType];

    // Confidence of our inferred window start.
    private enum ConfidenceLevel {
      None,
      Assumed,
      Proven
    }

    // Data structure for time-based token usage buckets
    public record TimeBucket(DateTime BucketTime, long TotalTokens, float TotalCost, int Count);

    public string? Uuid => uuid;
    public string? EmailAdress => emailAdress;

    private DateTime _currentWindowStart;
    private ConfidenceLevel _confidence = ConfidenceLevel.None;

    // Account-level token tracking (shared across all processes)
    private readonly List<StatPoint> _accountStatPoints = new();

    // Account-level statistics (aggregated across all processes)
    public TimeSeriesStats? AccountM5Stats { get; private set; }
    public TimeSeriesStats? AccountH1Stats { get; private set; }
    public TimeSeriesStats? AccountD1Stats { get; private set; }

    public uint WindowInputTokens { get; private set; } = 0;
    public uint WindowOutputTokens { get; private set; } = 0;
    public uint WindowTotalTokens => this.WindowInputTokens + this.WindowOutputTokens;
    public float WindowCost { get; private set; } = 0;
    public uint TokensRemaining => this.TOKEN_LIMIT_PER_WINDOW - WindowTotalTokens;

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
        // Reset window tokens when window changes
        this._ResetWindowTokens();
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

    /// <summary>
    ///   Add token usage to account-level tracking (called when any process uses tokens)
    /// </summary>
    public void AddTokenUsage(DateTime timestamp, uint inputTokens, uint outputTokens, float cost) {
      // Only count tokens from current window
      if (timestamp >= this.CurrentWindowStart && timestamp <= this.CurrentWindowEnd) {
        this.WindowInputTokens += inputTokens;
        this.WindowOutputTokens += outputTokens;
        this.WindowCost += cost;

        // Add to account-level time series
        this._accountStatPoints.Add(new StatPoint(timestamp, inputTokens, outputTokens, cost));

        // Keep only last 24 hours for performance
        var cutoff = DateTime.Now.AddHours(-24);
        this._accountStatPoints.RemoveAll(p => p.Timestamp < cutoff);

        // Update account-level statistics
        this._UpdateAccountStatistics();
      }
    }

    /// <summary>
    ///   Update account type based on evidence found in logs or config
    /// </summary>
    public void UpdateAccountType(string evidence, AccountType suggestedType) {
      if (this.DetectedAccountType == AccountType.Unknown || suggestedType > this.DetectedAccountType) {
        this.DetectedAccountType = suggestedType;
        // Only show account type detection, not every update
      }
    }

    private void _CheckSchedule() =>
      this.NextContinueEvent = this.IsRateLimited
        ? this.CurrentWindowEnd.AddMinutes(1).AddSeconds(Random.Shared.Next(0, 60))
        : null;

    private void _ResetWindowTokens() {
      this.WindowInputTokens = 0;
      this.WindowOutputTokens = 0;
      this.WindowCost = 0;
      // Don't clear stat points as they span multiple windows
    }

    private void _UpdateAccountStatistics() {
      var now = DateTime.Now;

      this.AccountM5Stats = this._CalculateAccountTimeSeriesStats(TimeSpan.FromMinutes(5), now);
      this.AccountH1Stats = this._CalculateAccountTimeSeriesStats(TimeSpan.FromHours(1), now);
      this.AccountD1Stats = this._CalculateAccountTimeSeriesStats(TimeSpan.FromDays(1), now);
    }

    private TimeSeriesStats? _CalculateAccountTimeSeriesStats(TimeSpan interval, DateTime now) {
      var cutoff = now - interval;
      var relevantPoints = this._accountStatPoints.Where(p => p.Timestamp >= cutoff).ToList();

      if (relevantPoints.Count < 2)
        return null;

      // Group by time buckets for rate calculations
      var bucketSize = interval.TotalMinutes / 12; // 12 buckets per interval
      var buckets = relevantPoints
        .GroupBy(p => (int)((p.Timestamp - cutoff).TotalMinutes / bucketSize))
        .Select(g => new TimeBucket(
          cutoff.AddMinutes(g.Key * bucketSize),
          g.Sum(p => p.InputTokens + p.OutputTokens),
          g.Sum(p => p.Cost),
          g.Count()
        ))
        .OrderBy(b => b.BucketTime)
        .ToList();

      if (buckets.Count < 2)
        return null;

      // Calculate rates (tokens per minute)
      var rates = buckets.Select(b => b.TotalTokens / (float)bucketSize).ToList();

      var average = rates.Average();
      var min = rates.Min();
      var max = rates.Max();
      var p90 = this._CalculatePercentile(rates, 0.9f);
      var trend = this._CalculateTrend(buckets);
      var forecast = this._CalculateForecast(buckets, average, trend);

      return new TimeSeriesStats(average, min, max, p90, trend, forecast);
    }

    private float _CalculatePercentile(List<float> values, float percentile) {
      if (values.Count == 0) return 0;

      var sorted = values.OrderBy(x => x).ToList();
      var index = (int)(percentile * (sorted.Count - 1));
      return sorted[Math.Min(index, sorted.Count - 1)];
    }

    private float _CalculateTrend(List<TimeBucket> buckets) {
      if (buckets.Count < 3) return 0;

      // Simple linear regression slope calculation
      var n = buckets.Count;
      var sumX = 0f;
      var sumY = 0f;
      var sumXY = 0f;
      var sumXX = 0f;

      for (var i = 0; i < n; i++) {
        var x = i; // time index
        var y = buckets[i].TotalTokens;
        sumX += x;
        sumY += y;
        sumXY += x * y;
        sumXX += x * x;
      }

      var slope = (n * sumXY - sumX * sumY) / (n * sumXX - sumX * sumX);
      return slope; // tokens per bucket change rate
    }

    private float _CalculateForecast(List<TimeBucket> buckets, float average, float trend) {
      if (buckets.Count == 0) return 0;

      // Simple trend-based forecast
      var lastValue = buckets.Last().TotalTokens;
      var forecast = lastValue + trend;

      // Don't forecast negative values
      return Math.Max(0, forecast);
    }

    private static DateTime FloorToHour(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind);
  }

  public record StatPoint(DateTime Timestamp, uint InputTokens, uint OutputTokens, float Cost);

  public record TimeSeriesStats(
    float Average,
    float Min,
    float Max,
    float P90,
    float Trend, // tokens/minute change rate
    float Forecast // projected next interval
  );

  public class SessionInfo(AnthropicAccount account, DateTime sessionStart) {
    public AnthropicAccount Account { get; set; } = account;
    public DateTime SessionStart => sessionStart;
    public DateTime? LastKnownMessage { get; private set; }
    public DateTime? LastParsedTimestamp { get; set; }

    private readonly HashSet<string> _usedModels = new(StringComparer.InvariantCultureIgnoreCase);
    public IEnumerable<string> UsedModels => this._usedModels;
    public uint TotalInputTokens { get; set; }
    public uint TotalOutputTokens { get; set; }
    public float BurnRateTokensPerMinute { get; set; }
    public float TotalCost { get; set; }
    public float ProjectedTotalCost { get; set; }

    // Time series data for statistical analysis
    private readonly List<StatPoint> _statPoints = new();
    public IReadOnlyList<StatPoint> StatPoints => this._statPoints.AsReadOnly();

    // Cached statistics (recalculated on data updates)
    public TimeSeriesStats? M5Stats { get; private set; } // 5-minute averages
    public TimeSeriesStats? H1Stats { get; private set; } // 1-hour averages
    public TimeSeriesStats? D1Stats { get; private set; } // 1-day averages

    public void AddModel(string model) => this._usedModels.Add(model);

    public void IncomingMessage(DateTime when) {
      if (when > this.LastKnownMessage)
        this.LastKnownMessage = when;

      this.Account.IncomingMessage(when);
    }

    public void AddStatPoint(DateTime timestamp, uint inputTokens, uint outputTokens, float cost) {
      this._statPoints.Add(new StatPoint(timestamp, inputTokens, outputTokens, cost));

      // Keep only last 24 hours for performance
      var cutoff = DateTime.Now.AddHours(-24);
      this._statPoints.RemoveAll(p => p.Timestamp < cutoff);

      // Recalculate statistics
      this.UpdateStatistics();
    }

    private void UpdateStatistics() {
      var now = DateTime.Now;

      this.M5Stats = this.CalculateTimeSeriesStats(TimeSpan.FromMinutes(5), now);
      this.H1Stats = this.CalculateTimeSeriesStats(TimeSpan.FromHours(1), now);
      this.D1Stats = this.CalculateTimeSeriesStats(TimeSpan.FromDays(1), now);
    }

    public record TimeBucket(DateTime BucketTime, long TotalTokens, float TotalCost, int Count);

    private TimeSeriesStats? CalculateTimeSeriesStats(TimeSpan interval, DateTime now) {
      var cutoff = now - interval;
      var relevantPoints = this._statPoints.Where(p => p.Timestamp >= cutoff).ToList();

      if (relevantPoints.Count < 2)
        return null;

      // Group by time buckets for rate calculations
      var bucketSize = interval.TotalMinutes / 12; // 12 buckets per interval
      var buckets = relevantPoints
        .GroupBy(p => (int)((p.Timestamp - cutoff).TotalMinutes / bucketSize))
        .Select(g => new TimeBucket(cutoff.AddMinutes(g.Key * bucketSize), g.Sum(p => p.InputTokens + p.OutputTokens), g.Sum(p => p.Cost), g.Count()))
        .OrderBy(b => b.BucketTime)
        .ToList();

      if (buckets.Count < 2)
        return null;

      // Calculate rates (tokens per minute)
      var rates = buckets.Select(b => b.TotalTokens / (float)bucketSize).ToList();

      var average = rates.Average();
      var min = rates.Min();
      var max = rates.Max();
      var p90 = this.CalculatePercentile(rates, 0.9f);
      var trend = CalculateTrend(buckets);
      var forecast = CalculateForecast(buckets, average, trend);

      return new TimeSeriesStats(average, min, max, p90, trend, forecast);
    }

    private float CalculatePercentile(List<float> values, float percentile) {
      if (values.Count == 0) return 0;

      var sorted = values.OrderBy(x => x).ToList();
      var index = (int)(percentile * (sorted.Count - 1));
      return sorted[Math.Min(index, sorted.Count - 1)];
    }

    private float CalculateTrend(List<TimeBucket> buckets) {
      if (buckets.Count < 3) return 0;

      // Simple linear regression slope calculation
      var n = buckets.Count;
      var sumX = 0f;
      var sumY = 0f;
      var sumXY = 0f;
      var sumXX = 0f;

      for (var i = 0; i < n; i++) {
        var x = i; // time index
        var y = buckets[i].TotalTokens;
        sumX += x;
        sumY += y;
        sumXY += x * y;
        sumXX += x * x;
      }

      var slope = (n * sumXY - sumX * sumY) / (n * sumXX - sumX * sumX);
      return slope; // tokens per bucket change rate
    }

    private float CalculateForecast(List<TimeBucket> buckets, float average, float trend) {
      if (buckets.Count == 0) return 0;

      // Simple trend-based forecast
      var lastValue = buckets.Last().TotalTokens;
      var forecast = lastValue + trend;

      // Don't forecast negative values
      return Math.Max(0, forecast);
    }
  }

  public class ClaudeProcess(int pid, string name, string cmd, string cfg, FileInfo settings, DateTime started, string friendlyConfigurationName, AnthropicAccount account) {
    public int ProcessId => pid;
    public string ProcessName => name;
    public string CommandLine => cmd;
    public DateTime StartTime => started;
    public double WorkingSet { get; set; }
    public string ConfigPath => cfg;
    public FileInfo ClaudeSettingsFile => settings;
    public DirectoryInfo? WorkingDirectory { get; set; }
    public string FriendlyConfigurationName => friendlyConfigurationName;
    public IntPtr? ConsoleHandle { get; set; }
    public bool HasConsoleWindow => this.ConsoleHandle.HasValue;
    public SessionInfo Session { get; } = new SessionInfo(account, started);
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
      : Encoding.Unicode.GetString(ReadRemoteMemory(hProcess, this.Buffer, this.Length));
  }

  [StructLayout(LayoutKind.Sequential)]
  struct STRING {
    public ushort Length, MaximumLength;
    public IntPtr Buffer;

    public string ToString(IntPtr hProcess) => this.Buffer == IntPtr.Zero || this.Length == 0
      ? string.Empty
      : Encoding.ASCII.GetString(ReadRemoteMemory(hProcess, this.Buffer, this.Length));
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
        var claudeProcess = activeProcesses[pid];
        claudeProcess.WorkingSet = process.WorkingSet64;

        // for some reason we still don't know which account is associated, try to refresh
        if (claudeProcess.Session.Account.Uuid == null) {
          var file = claudeProcess.ClaudeSettingsFile;
          file.Refresh();
          if (!file.Exists)
            continue;

          claudeProcess.Session.Account = GetOrCreateAccount(file);
        }

        continue;
      }

      var (envVars, consoleHandle, workingDirectory) = GetProcessInformation(pid);
      var requestedConfigurationPath =
          envVars == null
            ? null
            : envVars.GetValueOrDefault("CLAUDE_CONFIG_DIR")
              ?? envVars.GetValueOrDefault("ANTHROPIC_CONFIG_DIR")
              ?? envVars.GetValueOrDefault("HOME")
        ;
      var realConfigurationPath =
          requestedConfigurationPath
          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
        ;

      FileInfo claudeSettingsFile = new(Path.Combine(requestedConfigurationPath ?? Path.Combine(realConfigurationPath, ".."), _CLAUDE_CONFIGURATION_FILENAME));

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

  static (Dictionary<string, string>? env, IntPtr? consoleHandle, DirectoryInfo? workingDirectory) GetProcessInformation(int processId) {
    try {
      var hProc = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);
      if (hProc == IntPtr.Zero) {
        Console.WriteLine("Debug: Failed to open process " + processId);
        return (null, null, null);
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
      if (contents == null)
        return (null, null);

      var jsonContent = FixMissingCommas(contents);
      var json = JsonDocument.Parse(jsonContent);
      
      // Enhanced parsing - removed debug output for cleaner display
      
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
          
          // Look for account/service tier information in JSONL and update account type
          if (root.TryGetProperty("message", out var debugMessageProp) &&
              debugMessageProp.TryGetProperty("usage", out var debugUsageProp) &&
              debugUsageProp.TryGetProperty("service_tier", out var serviceTierProp)) {
            var serviceTier = serviceTierProp.GetString();
            
            // Update account type based on service tier
            var detectedType = serviceTier switch {
              "standard" => AccountType.Pro,        // Standard tier likely means Pro
              "premium" => AccountType.Max,         // Premium tier likely means Max
              "enterprise" => AccountType.Enterprise,
              _ => AccountType.Unknown
            };
            
            if (detectedType != AccountType.Unknown) {
              session.Account.UpdateAccountType($"service_tier: {serviceTier}", detectedType);
            }
          }

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

          // Only process assistant messages (they contain usage data)
          if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "assistant")
            continue;

          // Look for usage data in message.usage (Claude's JSONL format)
          if (root.TryGetProperty("message", out var messageProp)) {
            // Extract model name from message
            string? model = null;
            if (messageProp.TryGetProperty("model", out var modelProp))
              model = modelProp.GetString();

            // Look for usage data in message.usage
            if (messageProp.TryGetProperty("usage", out var usageProp)) {
              var inputTokens = usageProp.TryGetProperty("input_tokens", out var inputProp) ? inputProp.GetUInt32() : 0;
              var outputTokens = usageProp.TryGetProperty("output_tokens", out var outputProp) ? outputProp.GetUInt32() : 0;
              var cacheCreation = usageProp.TryGetProperty("cache_creation_input_tokens", out var cacheProp) ? cacheProp.GetUInt32() : 0;
              var cacheRead = usageProp.TryGetProperty("cache_read_input_tokens", out var cacheReadProp) ? cacheReadProp.GetUInt32() : 0;

              // Only process if we have valid token counts
              if (inputTokens > 0 || outputTokens > 0) {
                if (model != null)
                  session.AddModel(model);

                session.TotalInputTokens += inputTokens;
                session.TotalOutputTokens += outputTokens;

                var cost = (inputTokens + cacheCreation) * (float)AnthropicAccount.INPUT_COST_PER_1K / 1000.0f + outputTokens * (float)AnthropicAccount.OUTPUT_COST_PER_1K / 1000.0f;
                session.TotalCost += cost;

                // Add to time series for statistics
                session.AddStatPoint(messageTime, inputTokens, outputTokens, cost);

                // Add to account-level tracking for cross-process forecasting
                session.Account.AddTokenUsage(messageTime, inputTokens, outputTokens, cost);
              }
            }

            // Check for rate limit messages in assistant content
            if (messageProp.TryGetProperty("content", out var contentArrayProp) && contentArrayProp.ValueKind == JsonValueKind.Array)
              foreach (var contentItem in contentArrayProp.EnumerateArray())
                if (contentItem.TryGetProperty("type", out var contentTypeProp) &&
                    contentTypeProp.GetString() == "text" &&
                    contentItem.TryGetProperty("text", out var textProp)) {
                  var text = textProp.GetString() ?? "";
                  
                  var rateLimitPattern = @"(\d+)-hour\s+limit\s+reached.*?resets\s+(\d+)(am|pm)";
                  var match = Regex.Match(text, rateLimitPattern, RegexOptions.IgnoreCase);

                  // Look for account plan information in rate limit messages
                  var planPattern = @"(Pro|Max|Enterprise|Team)\s+(plan|subscription|account)";
                  var planMatch = Regex.Match(text, planPattern, RegexOptions.IgnoreCase);
                  if (planMatch.Success) {
                    var planType = planMatch.Groups[1].Value.ToLower();
                    var detectedType = planType switch {
                      "pro" => AccountType.Pro,
                      "max" => AccountType.Max,
                      "enterprise" => AccountType.Enterprise,
                      "team" => AccountType.Enterprise,
                      _ => AccountType.Unknown
                    };
                    
                    if (detectedType != AccountType.Unknown) {
                      session.Account.UpdateAccountType($"rate limit message: {planMatch.Value}", detectedType);
                    }
                  }

                  if (match.Success) {
                    var limitHours = int.Parse(match.Groups[1].Value);
                    AnthropicAccount.WindowLength = TimeSpan.FromHours(limitHours);

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
                    break; // Only process the first rate limit message found
                  }
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

    var remainingMinutes = session.Account.TokensRemaining / session.BurnRateTokensPerMinute;
    if (remainingMinutes > 0)
      session.ProjectedTotalCost = session.TotalCost + remainingMinutes * session.BurnRateTokensPerMinute * (float)AnthropicAccount.OUTPUT_COST_PER_1K / 1000.0f;
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

    // Account-level aggregation
    var accounts = claudeProcesses.Select(p => p.Session.Account).GroupBy(a => a.Uuid ?? a.EmailAdress ?? "unknown").ToList();
    var accountSummary = string.Join(", ", accounts.Select(g => {
      var account = g.First();
      var remaining = account.TokensRemaining;
      var total = account.WindowTotalTokens;
      var accountTypeText = account.DetectedAccountType != AccountType.Unknown ? $" ({account.DetectedAccountType})" : "";
      return $"{account.EmailAdress ?? "Unknown"}{accountTypeText}: {remaining:N0}/{account.TOKEN_LIMIT_PER_WINDOW:N0}";
    }));

    WriteLineAnsi($"{GREEN}+--- SUMMARY ------------------------------------------------------------------+{RESET}");
    WriteLineAnsi($"| {WHITE}:computer: Configs: {discoveredConfigs} | :gear: Processes: {claudeProcesses.Count} | :chart: Total: {totalTokens:N0} tokens | :money: ${totalCost:F4} | :arrow: {totalContinues} continues{GREEN}|{RESET}");
    if (!string.IsNullOrEmpty(accountSummary))
      WriteLineAnsi($"| {WHITE}:hourglass: Account Limits: {accountSummary}{GREEN}|{RESET}");
    WriteLineAnsi($"{GREEN}+------------------------------------------------------------------------------+{RESET}");
    WriteLineAnsi("");
    WriteLineAnsi($"{RED}:warning: Press Ctrl+C to stop monitoring...{RESET}");
    return;


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
      var accountInfo = account.EmailAdress is { } email ? $" ({email})" : string.Empty;
      var projectText = $"{workingDirName} [{process.FriendlyConfigurationName}]{accountInfo} (PID:{process.ProcessId}, {process.WorkingSet / 1024 / 1024:0}MB) :clock: {uptimeStr}";

      var modelText = session.UsedModels.Count() switch {
        0 => "No Model",
        1 => session.UsedModels.First().Replace("claude-", "").Replace("-20", ""),
        var count => $"{count} models"
      };

      var usageText = session is { TotalInputTokens: 0, TotalOutputTokens: 0 } ? "No Usage" : GenerateUsageText(session, currentTime);

      var windowText = GenerateWindowText(session, account, currentTime);
      var windowColor = BRIGHT_RED;

      var statsText = GenerateStatsText(session);
      var forecastText = GenerateForecastText(session);

      WriteLineAnsi($"{GRAY}+------------------------------------------------------------------------------+{RESET}");
      WriteLineAnsi($"| {statusColor}{statusEmoji} {statusText,-74}{WHITE} |{RESET}");
      WriteLineAnsi($"| {BRIGHT_MAGENTA}:computer: {projectText,-78}{WHITE} |{RESET}");
      WriteLineAnsi($"| {CYAN}:robot: {modelText,-73}{WHITE} |{RESET}");
      WriteLineAnsi($"| {GREEN}:money: {usageText,-73}{WHITE} |{RESET}");
      WriteLineAnsi($"| {windowColor}:hourglass: {windowText,-78}{WHITE} |{RESET}");
      WriteLineAnsi($"| {BRIGHT_BLUE}:chart: {statsText,-73}{WHITE} |{RESET}");
      WriteLineAnsi($"| {BRIGHT_YELLOW}:target: {forecastText,-73}{WHITE} |{RESET}");
      WriteLineAnsi($"{GRAY}+------------------------------------------------------------------------------+{RESET}");
      WriteLineAnsi();
      return;

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

      static string GenerateWindowText(SessionInfo session, AnthropicAccount account, DateTime currentTime) {
        if (account.Uuid == null)
          return "No account data";

        var windowStart = account.CurrentWindowStart;
        var windowEnd = account.CurrentWindowEnd;
        var timeRemaining = windowEnd.Subtract(currentTime);

        var windowDisplay = $"{windowStart:HH:mm}-{windowEnd:HH:mm}";
        var timeRemainingText = timeRemaining.TotalMinutes < 60
          ? $"{timeRemaining.TotalMinutes:F1}min left"
          : $"{Math.Floor(timeRemaining.TotalHours)}h{timeRemaining.Minutes}m left";

        var tokensLeft = session.Account.TokensRemaining;

        return $"{windowDisplay} | {timeRemainingText} | {tokensLeft:N0} tokens left";
      }

      static string GenerateStatsText(SessionInfo session) {
        if (session.M5Stats == null && session.H1Stats == null)
          return "No statistics available";

        var parts = new List<string>();

        if (session.M5Stats != null) {
          var m5 = session.M5Stats;
          parts.Add($"M5: {m5.Average:F1}/min (P90:{m5.P90:F1})");
        }

        if (session.H1Stats != null) {
          var h1 = session.H1Stats;
          parts.Add($"H1: {h1.Average:F1}/min (P90:{h1.P90:F1})");
        }

        return string.Join(" | ", parts);
      }

      static string GenerateForecastText(SessionInfo session) {
        if (session.M5Stats == null)
          return "No forecast available";

        var m5 = session.M5Stats;
        var account = session.Account;
        var trendIndicator = m5.Trend switch {
          > 5 => "↗️ Rising",
          < -5 => "↘️ Falling",
          _ => "→ Stable"
        };

        var forecastText = $"Next 5min: {m5.Forecast:F1} tokens";

        // Use account-level remaining tokens for cross-process forecasting
        var remainingTokens = account.TokensRemaining;

        if (m5.Average > 0) {
          var minutesToDepletion = remainingTokens / m5.Average;
          var depletionTime = DateTime.Now.AddMinutes(minutesToDepletion);
          
          if (minutesToDepletion < 60) {
            return $"{forecastText} | WARN: Account limit in {minutesToDepletion:F0}min ({depletionTime:HH:mm})!";
          }
          
          var depletionTimeText = minutesToDepletion < 1440 // Less than 24 hours
            ? $"~{depletionTime:HH:mm}"
            : $"~{depletionTime:MM/dd HH:mm}";
            
          return $"{forecastText} | {trendIndicator} | Limit: {depletionTimeText} | {remainingTokens:N0} left";
        }

        return $"{forecastText} | {trendIndicator} | Account: {remainingTokens:N0} left";
      }
    }
  }

  static void HandleCliCommand(string[] args) {
    var command = args[0].ToLower();

    switch (command) {
      case "report":
      case "history":
        GenerateHistoricalReport(args);
        break;
      case "accounts":
        ListAccounts();
        break;
      case "stats":
        ShowAccountStats(args);
        break;
      case "cost":
        ShowCostAnalysis(args);
        break;
      case "help":
      case "--help":
      case "-h":
        ShowHelp();
        break;
      default:
        WriteLineAnsi($"{RED}Unknown command: {command}{RESET}");
        ShowHelp();
        break;
    }
  }

  static void ShowHelp() {
    WriteLineAnsi($"{GREEN}Claude Ultimate Monitor - CLI Commands{RESET}");
    WriteLineAnsi("");
    WriteLineAnsi($"{YELLOW}Usage:{RESET}");
    WriteLineAnsi("  ClaudeMonitor.exe                    - Start real-time monitoring");
    WriteLineAnsi("  ClaudeMonitor.exe report [options]   - Generate historical reports");
    WriteLineAnsi("  ClaudeMonitor.exe accounts           - List all discovered accounts");
    WriteLineAnsi("  ClaudeMonitor.exe stats [account]    - Show detailed account statistics");
    WriteLineAnsi("  ClaudeMonitor.exe cost [options]     - Cost analysis and projections");
    WriteLineAnsi("");
    WriteLineAnsi($"{YELLOW}Report Options:{RESET}");
    WriteLineAnsi("  --account <email|uuid>    - Filter by specific account");
    WriteLineAnsi("  --days <number>           - Number of days to analyze (default: 7)");
    WriteLineAnsi("  --format <table|json|csv> - Output format (default: table)");
    WriteLineAnsi("  --config <path>           - Specific Claude config directory");
    WriteLineAnsi("");
    WriteLineAnsi($"{YELLOW}Examples:{RESET}");
    WriteLineAnsi("  ClaudeMonitor.exe report --days 30");
    WriteLineAnsi("  ClaudeMonitor.exe report --account user@example.com --format json");
    WriteLineAnsi("  ClaudeMonitor.exe cost --account user@example.com");
    WriteLineAnsi("  ClaudeMonitor.exe stats");
  }

  static void ListAccounts() {
    WriteLineAnsi($"{GREEN}Discovering Claude Accounts...{RESET}");
    WriteLineAnsi("");

    var accounts = DiscoverAllAccounts();

    if (accounts.Count == 0) {
      WriteLineAnsi($"{RED}No Claude accounts found.{RESET}");
      WriteLineAnsi("Make sure you have used Claude Code at least once.");
      return;
    }

    WriteLineAnsi($"{CYAN}+--- DISCOVERED ACCOUNTS ------------------------------------------------------+{RESET}");
    WriteLineAnsi($"| {"Account",-30} | {"UUID",-36} | {"Config Path",-20} |{RESET}");
    WriteLineAnsi($"{CYAN}+------------------------------------------------------------------------------+{RESET}");

    foreach (var (configPath, account) in accounts) {
      var email = account.EmailAdress ?? "Unknown";
      var uuid = account.Uuid ?? "Unknown";
      var shortPath = configPath.Length > 20 ? "..." + configPath[^17..] : configPath;

      WriteLineAnsi($"| {email,-30} | {uuid,-36} | {shortPath,-20} |");
    }

    WriteLineAnsi($"{CYAN}+------------------------------------------------------------------------------+{RESET}");
    WriteLineAnsi($"Found {accounts.Count} account(s)");
  }

  static Dictionary<string, AnthropicAccount> DiscoverAllAccounts() {
    var accounts = new Dictionary<string, AnthropicAccount>();

    // Check default location
    var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    TryAddAccount(accounts, defaultPath);

    // TODO: Add discovery of custom config locations
    // This would scan current processes to discover alternate locations

    return accounts;
  }

  static void TryAddAccount(Dictionary<string, AnthropicAccount> accounts, string configPath) {
    try {
      var claudeSettingsPath = new FileInfo(Path.Combine(configPath, "..", _CLAUDE_CONFIGURATION_FILENAME));
      if (!claudeSettingsPath.Exists)
        claudeSettingsPath = new FileInfo(Path.Combine(configPath, _CLAUDE_CONFIGURATION_FILENAME));

      if (!claudeSettingsPath.Exists)
        return;

      var account = GetOrCreateAccount(claudeSettingsPath);
      if (account.Uuid != null || account.EmailAdress != null)
        accounts[configPath] = account;

    } catch {
      // Ignore discovery errors
    }
  }

  static void GenerateHistoricalReport(string[] args) {
    var options = ParseReportOptions(args);

    WriteLineAnsi($"{GREEN}Generating Historical Report...{RESET}");
    WriteLineAnsi($"Period: Last {options.Days} days");
    if (!string.IsNullOrEmpty(options.Account))
      WriteLineAnsi($"Account: {options.Account}");
    WriteLineAnsi("");

    var data = CollectHistoricalData(options);

    switch (options.Format.ToLower()) {
      case "json":
        OutputJsonReport(data);
        break;
      case "csv":
        OutputCsvReport(data);
        break;
      default:
        OutputTableReport(data);
        break;
    }
  }

  record ReportOptions(string? Account, int Days, string Format, string? ConfigPath);

  record HistoricalData(
    Dictionary<string, List<DailyUsage>> AccountUsage,
    Dictionary<string, AnthropicAccount> Accounts
  );

  record DailyUsage(DateTime Date, uint InputTokens, uint OutputTokens, float Cost, Dictionary<string, uint> ModelUsage);

  static ReportOptions ParseReportOptions(string[] args) {
    string? account = null;
    var days = 7;
    var format = "table";
    string? configPath = null;

    for (var i = 1; i < args.Length; i++)
      switch (args[i].ToLower()) {
        case "--account" when i + 1 < args.Length:
          account = args[++i];
          break;
        case "--days" when i + 1 < args.Length:
          if (int.TryParse(args[++i], out var d))
            days = d;
          break;
        case "--format" when i + 1 < args.Length:
          format = args[++i];
          break;
        case "--config" when i + 1 < args.Length:
          configPath = args[++i];
          break;
      }

    return new ReportOptions(account, days, format, configPath);
  }

  static HistoricalData CollectHistoricalData(ReportOptions options) {
    var accounts = DiscoverAllAccounts();
    var accountUsage = new Dictionary<string, List<DailyUsage>>();
    var cutoffDate = DateTime.Now.AddDays(-options.Days);

    foreach (var (configPath, account) in accounts) {
      // Filter by account if specified
      if (!string.IsNullOrEmpty(options.Account) &&
          account.EmailAdress != options.Account &&
          account.Uuid != options.Account)
        continue;

      var usage = AnalyzeHistoricalUsage(configPath, cutoffDate);
      if (usage.Count > 0) {
        var accountKey = account.EmailAdress ?? account.Uuid ?? configPath;
        accountUsage[accountKey] = usage;
      }
    }

    return new HistoricalData(accountUsage, accounts);
  }

  static List<DailyUsage> AnalyzeHistoricalUsage(string configPath, DateTime cutoffDate) {
    var dailyUsage = new Dictionary<DateTime, DailyUsage>();

    try {
      var jsonlFiles = GetAllJsonlFilesForProcess(new DirectoryInfo(configPath));

      foreach (var file in jsonlFiles) {
        var content = SafeReadAllText(file);
        if (content == null) continue;

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines) {
          if (string.IsNullOrWhiteSpace(line)) continue;

          try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("timestamp", out var timestampProp) ||
                !DateTime.TryParse(timestampProp.GetString(), out var messageTime) ||
                messageTime < cutoffDate)
              continue;

            if (!root.TryGetProperty("type", out var typeProp) ||
                typeProp.GetString() != "assistant")
              continue;

            if (root.TryGetProperty("message", out var messageProp) &&
                messageProp.TryGetProperty("usage", out var usageProp)) {
              var date = messageTime.Date;
              if (!dailyUsage.TryGetValue(date, out var dayUsage)) {
                dayUsage = new DailyUsage(date, 0, 0, 0, new Dictionary<string, uint>());
                dailyUsage[date] = dayUsage;
              }

              var inputTokens = usageProp.TryGetProperty("input_tokens", out var inputProp) ? inputProp.GetUInt32() : 0;
              var outputTokens = usageProp.TryGetProperty("output_tokens", out var outputProp) ? outputProp.GetUInt32() : 0;
              var cacheCreation = usageProp.TryGetProperty("cache_creation_input_tokens", out var cacheProp) ? cacheProp.GetUInt32() : 0;

              var model = "unknown";
              if (messageProp.TryGetProperty("model", out var modelProp))
                model = modelProp.GetString() ?? "unknown";

              var cost = (inputTokens + cacheCreation) * (float)AnthropicAccount.INPUT_COST_PER_1K / 1000.0f +
                         outputTokens * (float)AnthropicAccount.OUTPUT_COST_PER_1K / 1000.0f;

              // Update daily totals
              var updatedUsage = dayUsage with {
                InputTokens = dayUsage.InputTokens + inputTokens,
                OutputTokens = dayUsage.OutputTokens + outputTokens,
                Cost = dayUsage.Cost + cost
              };

              // Update model usage
              var modelUsage = new Dictionary<string, uint>(dayUsage.ModelUsage);
              modelUsage[model] = modelUsage.GetValueOrDefault(model) + inputTokens + outputTokens;
              updatedUsage = updatedUsage with { ModelUsage = modelUsage };

              dailyUsage[date] = updatedUsage;
            }
          } catch {
            // Continue processing other lines
          }
        }
      }
    } catch {
      // Handle file access errors
    }

    return dailyUsage.Values.OrderBy(d => d.Date).ToList();
  }

  static void OutputTableReport(HistoricalData data) {
    WriteLineAnsi($"{CYAN}+--- HISTORICAL USAGE REPORT --------------------------------------------------+{RESET}");
    WriteLineAnsi($"| {"Date",-12} | {"Account",-20} | {"Tokens",-10} | {"Cost",-8} | {"Models",-15} |{RESET}");
    WriteLineAnsi($"{CYAN}+------------------------------------------------------------------------------+{RESET}");

    foreach (var (accountKey, usage) in data.AccountUsage) {
      var shortAccount = accountKey.Length > 20 ? accountKey[..17] + "..." : accountKey;

      foreach (var day in usage) {
        var dayTotalTokens = day.InputTokens + day.OutputTokens;
        var topModel = day.ModelUsage.OrderByDescending(kv => kv.Value).FirstOrDefault().Key ?? "none";
        var shortModel = topModel.Replace("claude-", string.Empty).Replace("-20", string.Empty);

        WriteLineAnsi($"| {day.Date:yyyy-MM-dd} | {shortAccount,-20} | {dayTotalTokens,-10:N0} | ${day.Cost,-7:F2} | {shortModel,-15} |");
      }

      if (usage.Count <= 1)
        continue;

      var totalTokens = usage.Sum(d => d.InputTokens + d.OutputTokens);
      var totalCost = usage.Sum(d => d.Cost);
      WriteLineAnsi($"{GRAY}| {"TOTAL",-12} | {shortAccount,-20} | {totalTokens,-10:N0} | ${totalCost,-7:F2} | {"",-15} |{RESET}");
      WriteLineAnsi($"{CYAN}+------------------------------------------------------------------------------+{RESET}");
    }

    var grandTotalTokens = data.AccountUsage.SelectMany(kv => kv.Value).Sum(d => d.InputTokens + d.OutputTokens);
    var grandTotalCost = data.AccountUsage.SelectMany(kv => kv.Value).Sum(d => d.Cost);

    WriteLineAnsi($"{GREEN}| {"GRAND TOTAL",-12} | {"",-20} | {grandTotalTokens,-10:N0} | ${grandTotalCost,-7:F2} | {"",-15} |{RESET}");
    WriteLineAnsi($"{CYAN}+------------------------------------------------------------------------------+{RESET}");
  }

  static void OutputJsonReport(HistoricalData data) {
    // TODO: Implement JSON output using System.Text.Json
    WriteLineAnsi("JSON output not yet implemented");
  }

  static void OutputCsvReport(HistoricalData data) {
    WriteLineAnsi("Date,Account,InputTokens,OutputTokens,Cost,TopModel");

    foreach (var (accountKey, usage) in data.AccountUsage)
    foreach (var day in usage) {
      var topModel = day.ModelUsage.OrderByDescending(kv => kv.Value).FirstOrDefault().Key ?? "none";
      WriteLineAnsi($"{day.Date:yyyy-MM-dd},{accountKey},{day.InputTokens},{day.OutputTokens},{day.Cost:F4},{topModel}");
    }
  }

  static void ShowAccountStats(string[] args) {
    // TODO: Implement detailed account statistics
    WriteLineAnsi("Account statistics not yet implemented");
  }

  static void ShowCostAnalysis(string[] args) {
    // TODO: Implement cost analysis and projections
    WriteLineAnsi("Cost analysis not yet implemented");
  }

  public static void Main(string[] args) {
    Console.OutputEncoding = Encoding.UTF8;

    // Handle CLI commands
    if (args.Length > 0) {
      HandleCliCommand(args);
      return;
    }

    // Default monitoring mode
    WriteLineAnsi("Starting Ultimate Claude Background Monitor...");
    WriteLineAnsi("Auto-discovering Claude configurations...");
    WriteLineAnsi();

    var lastDisplayUpdate = DateTime.MinValue;
    var isRunning = true;

    Console.CancelKeyPress += (_, e) => {
      e.Cancel = true;
      if (!isRunning)
        return;

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
