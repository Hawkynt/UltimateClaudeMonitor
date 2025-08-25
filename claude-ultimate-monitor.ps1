# Ultimate Claude Background Monitor
# Monitors Claude instances, handles rate limits, sends auto-continue, tracks costs and tokens
# Run with: Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process; .\claude-ultimate-monitor.ps1
# This is basically .Net Core 9 C# code wrapped in a PowerShell loader.

# Add required assemblies
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Runtime.InteropServices
Add-Type -AssemblyName System.Management

# Generate unique class name based on script content hash
$scriptContent = Get-Content $PSCommandPath -Raw
$hash = [System.Security.Cryptography.MD5]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($scriptContent))
$hashString = [System.BitConverter]::ToString($hash).Replace("-", "").Substring(0, 8)
$className = "ClaudeMonitorApp_$hashString"

Write-Host "Compiling C# monitoring application ($className)..." -ForegroundColor Yellow
Add-Type -ReferencedAssemblies @(
    "System.Windows.Forms", 
    "System.Management", 
    "System.Collections",
    "System.Core",
    "System",
    "System.Threading.Tasks",
    "System.Diagnostics.Process",
    "System.Text.RegularExpressions",
    "System.Linq",
    "System.Console",
    "System.Threading.Thread",
    "System.ComponentModel.Primitives",
    "mscorlib"
) -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

public sealed class $className {
    const int CHECK_INTERVAL_SECONDS = 15;
    const int DISPLAY_UPDATE_SECONDS = 30;
    const string RESET = "\u001b[0m", RED = "\u001b[31m", GREEN = "\u001b[32m", YELLOW = "\u001b[33m";
    const string BLUE = "\u001b[34m", MAGENTA = "\u001b[35m", CYAN = "\u001b[36m", WHITE = "\u001b[37m", GRAY = "\u001b[90m";
    const string BRIGHT_RED = "\u001b[91m", BRIGHT_GREEN = "\u001b[92m", BRIGHT_YELLOW = "\u001b[93m";
    const string BRIGHT_BLUE = "\u001b[94m", BRIGHT_MAGENTA = "\u001b[95m", BRIGHT_CYAN = "\u001b[96m", BRIGHT_WHITE = "\u001b[97m";
    const double INPUT_COST_PER_1K = 0.003, OUTPUT_COST_PER_1K = 0.015;
    
    static readonly Dictionary<string, string> emojisUnicode = new Dictionary<string, string> {
        {"check", "\u2713"}, {"cross", "\u2717"}, {"warning", "\u26A0"}, {"info", "\u2139"},
        {"clock", "\u23F0"}, {"gear", "\u2699"}, {"rocket", "\uD83D\uDE80"}, {"fire", "\uD83D\uDD25"},
        {"computer", "\uD83D\uDCBB"}, {"robot", "\uD83E\uDD16"}, {"money", "\uD83D\uDCB0"}, {"chart", "\uD83D\uDCCA"},
        {"hourglass", "\u23F3"}, {"lightning", "\u26A1"}, {"circle", "\u25CB"}, {"dot", "\u2022"},
        {"arrow", "\u25B6"}, {"star", "\u2B50"}, {"target", "\uD83C\uDFAF"}
    };
    
    static readonly Dictionary<string, string> emojisAscii = new Dictionary<string, string> {
        {"check", "[+]"}, {"cross", "[X]"}, {"warning", "[!]"}, {"info", "[i]"},
        {"clock", "[T]"}, {"gear", "[*]"},
        {"rocket", "[^]"}, {"fire", "[F]"}, {"computer", "[C]"}, {"robot", "[R]"},
        {"money", "[$]"}, {"chart", "[#]"}, {"hourglass", "[H]"}, {"lightning", "[L]"},
        {"circle", "[O]"}, {"dot", "[.]"}, {"arrow", "[>]"}, {"star", "[*]"}, {"target", "[@]"}
    };
    
    static bool? ansiSupported, unicodeSupported;
    static bool running = true;
    static readonly Dictionary<string, SessionStats> sessionStats = new Dictionary<string, SessionStats>();
    static readonly Dictionary<string, DateTime> lastContinueSent = new Dictionary<string, DateTime>();
    static readonly Dictionary<string, ConfigInfo> discoveredConfigs = new Dictionary<string, ConfigInfo>();
    static readonly Dictionary<string, RateLimitState> rateLimitStates = new Dictionary<string, RateLimitState>();

    public class SessionStats {
        public int TotalRequests { get; set; }
        public int TotalInputTokens { get; set; }
        public int TotalOutputTokens { get; set; }
        public int TotalCacheCreation { get; set; }
        public int TotalCacheRead { get; set; }
        public DateTime SessionStart { get; set; }
        public DateTime? LastActivity { get; set; }
        public double TotalCost { get; set; }
        public int RateLimitCount { get; set; }
        public int ContinuesSent { get; set; }
        public DateTime? LastContinueSent { get; set; }
        public double BurnRateTokensPerMinute { get; set; }
        public double BurnRateCostPerHour { get; set; }
        public List<BurnRateEntry> BurnRateHistory { get; set; }
        public double ProjectedTotalCost { get; set; }
        public int ProjectedTotalTokens { get; set; }
        public string WorkingDirectory { get; set; }
        public string CurrentProject { get; set; }
        public Dictionary<string, ModelUsage> ModelBreakdown { get; set; }
        public string WarningLevel { get; set; }
        
        public SessionStats() {
            BurnRateHistory = new List<BurnRateEntry>();
            WorkingDirectory = "";
            CurrentProject = "";
            ModelBreakdown = new Dictionary<string, ModelUsage>();
            WarningLevel = "OK";
        }
    }

    public class BurnRateEntry {
        public DateTime Timestamp { get; set; }
        public double TokensPerMinute { get; set; }
        public double CostPerHour { get; set; }
        
        public BurnRateEntry(DateTime timestamp, double tokensPerMinute, double costPerHour) {
            Timestamp = timestamp;
            TokensPerMinute = tokensPerMinute;
            CostPerHour = costPerHour;
        }
    }
    
    public class ModelUsage {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CacheCreation { get; set; }
        public int CacheRead { get; set; }
        public int Requests { get; set; }
        public double TotalCost { get; set; }
    }

    public class ConfigInfo {
        public string Name { get; set; }
        public string Color { get; set; }
        public string Path { get; set; }
        
        public ConfigInfo(string name, string color, string path) {
            Name = name;
            Color = color;
            Path = path;
        }
    }

    public class RateLimitState {
        public bool IsRateLimited { get; set; }
        public DateTime? ResetTime { get; set; }
        public int? RateLimitHours { get; set; }
        public DateTime? LastRateLimitTime { get; set; }
        public string RateLimitMessage { get; set; }
        public DateTime? LastLogActivity { get; set; }
        
        public RateLimitState() {
            RateLimitMessage = "";
        }
    }
    
    public class RateLimitCheckResult {
        public bool IsRateLimited { get; set; }
        public DateTime? ResetTime { get; set; }
        public string Message { get; set; }
        public int? RateLimitHours { get; set; }
        
        public RateLimitCheckResult(bool isRateLimited, DateTime? resetTime, string message, int? rateLimitHours) {
            IsRateLimited = isRateLimited;
            ResetTime = resetTime;
            Message = message;
            RateLimitHours = rateLimitHours;
        }
    }

    public class AnthropicAccount {
      private DateTime _currentWindowStart;
      public DateTime CurrentWindowStart { 
        get { return this._currentWindowStart; }
        set {
          this._currentWindowStart = value;
          this._CheckSchedule();
        }
      }
      
      private bool _isRateLimited;
      public bool IsRateLimited {
        get { return this._isRateLimited; }
        set {
          this._isRateLimited = value;
          this._CheckSchedule();
        }
      }
      
      public DateTime CurrentWindowEnd { get { return CurrentWindowStart.AddHours(5); } }
      private DateTime? _nextContinueEvent;
      public DateTime? NextContinueEvent { get { return this._nextContinueEvent; } private set { this._nextContinueEvent = value; } }
      
      private void _CheckSchedule() {
        if(!this._isRateLimited) {
          this._nextContinueEvent = null;
          return;
        }
        
        this._nextContinueEvent = this.CurrentWindowEnd.AddMinutes(1).AddSeconds(new Random().Next(0,60));
      }
      
      private readonly List<SessionStats> _openSessions = new List<SessionStats>();
      public IList<SessionStats> OpenSessions { get { return this._openSessions; } }
      
    }

    public class ClaudeProcess {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; }
        public string CommandLine { get; set; }
        public DateTime StartTime { get; set; }
        public double WorkingSet { get; set; }
        public string ConfigPath { get; set; }
        public string WorkingDirectory { get; set; }
        public string ProjectName { get; set; }
        public IntPtr? ConsoleHandle { get; set; }
        public bool HasConsoleWindow { get; set; }
        public string DetectionReason { get; set; }
        public AnthropicAccount Account { get; set; }
        
        public ClaudeProcess() {
            ProcessName = "";
            CommandLine = "";
            ConfigPath = "";
            WorkingDirectory = "";
            ProjectName = "";
            DetectionReason = "";
        }
    }

    public class StatusInfo {
        public bool IsRateLimited { get; set; }
        public DateTime? LastRateLimitTime { get; set; }
        public DateTime? ResetTime { get; set; }
        public int? RateLimitHours { get; set; }  // Store the hour duration from rate limit message
        public DateTime? RecentActivity { get; set; }
        public string RateLimitMessage { get; set; }
        public int NewTokensThisCheck { get; set; }
        public int NewRequestsThisCheck { get; set; }
        public bool ApproachingRateLimit { get; set; }
        public SessionStats Statistics { get; set; }
        
        public StatusInfo() {
            RateLimitMessage = "";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left, Top, Right, Bottom;
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
    }

    [StructLayout(LayoutKind.Sequential)]
    struct STRING {
        public ushort Length, MaximumLength;
        public IntPtr Buffer;
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

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, ref RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, uint processInformationLength, out uint returnLength);

    public const int SW_RESTORE = 9, SW_SHOW = 5;
    const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_VM_READ = 0x0010;

    static void WriteAnsi(string text) {
        unicodeSupported = unicodeSupported ?? GetUnicodeSupport();
        var emojiSet = unicodeSupported == true ? emojisUnicode : emojisAscii;
        foreach (var emoji in emojiSet)
            text = text.Replace(":" + emoji.Key + ":", emoji.Value);

        ansiSupported = ansiSupported ?? GetAnsiSupport();
        if (ansiSupported == false)
            text = StripAnsiAndApplyColors(text);
        Console.Write(text);
    }

    static bool GetUnicodeSupport() {
        try {
            var encoding = Console.OutputEncoding;
            return encoding.CodePage == 65001 || encoding.CodePage == 1200 || encoding.CodePage == 1201 ||
                   encoding.EncodingName.Contains("Unicode") ||
                   encoding.EncodingName.Contains("UTF");
        } catch { return false; }
    }

    static bool GetAnsiSupport() {
        try {
            var handle = GetStdHandle(-11);
            GetConsoleMode(handle, out var mode);
            SetConsoleMode(handle, mode | 0x4);
            return true;
        } catch { return false; }
    }

    static string StripAnsiAndApplyColors(string text) {
        Dictionary<string, ConsoleColor> colorMap = new() {
            [GREEN]          = ConsoleColor.Green, 
            [RED]            = ConsoleColor.Red, 
            [YELLOW]         = ConsoleColor.Yellow,
            [BLUE]           = ConsoleColor.Blue, 
            [MAGENTA]        = ConsoleColor.Magenta, 
            [CYAN]           = ConsoleColor.Cyan,
            [WHITE]          = ConsoleColor.White, 
            [GRAY]           = ConsoleColor.DarkGray,
            [BRIGHT_GREEN]   = ConsoleColor.Green,
            [BRIGHT_RED]     = ConsoleColor.Red,
            [BRIGHT_YELLOW]  = ConsoleColor.Yellow,
            [BRIGHT_BLUE]    = ConsoleColor.Blue,
            [BRIGHT_MAGENTA] = ConsoleColor.Magenta,
            [BRIGHT_CYAN]    = ConsoleColor.Cyan
        };

        var result = new StringBuilder();
        var currentPos = 0;
        var colorCodes = colorMap.Keys.Concat(new[] { RESET }).ToList();

        while (currentPos < text.Length) {
            var nextColorPos = text.Length;
            var nextColor = "";

            foreach (var color in colorCodes) {
                var pos = text.IndexOf(color, currentPos);
                if (pos != -1 && pos < nextColorPos) {
                    nextColorPos = pos;
                    nextColor = color;
                }
            }

            if (nextColorPos > currentPos)
                result.Append(text.Substring(currentPos, nextColorPos - currentPos));

            if (nextColor != "") {
                if (nextColor == RESET)
                    Console.ResetColor();
                else if (colorMap.TryGetValue(nextColor, out var color))
                    Console.ForegroundColor = color;

                currentPos = nextColorPos + nextColor.Length;
            } else break;
        }

        return Regex.Replace(result.ToString(), @"\u001b\[[0-9;]*m", "");
    }

    static void WriteLineAnsi(string text) { WriteAnsi(text + "\n"); }
    
    public static void RunMonitor() {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Starting Ultimate Claude Background Monitor...");
        Console.WriteLine("Auto-discovering Claude configurations...");
        Console.WriteLine();
        
        var lastDisplayUpdate = DateTime.MinValue;
        
        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true;
            running = false;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\nShutting down Ultimate Claude Monitor...");
            Console.ResetColor();
        };
        
        while (running) {
            try {
                var currentTime = DateTime.Now;
                var claudeProcesses = GetClaudeProcesses();
                var processStatuses = new Dictionary<string, StatusInfo>();
                
                foreach (var process in claudeProcesses) {
                    var processKey = "Process_" + process.ProcessId;
                    var status = UpdateProcessStats(process);
                    if (status != null)
                        processStatuses[processKey] = status;
                }
                
                foreach (var process in claudeProcesses) {
                    var processKey = "Process_" + process.ProcessId;
                    if (!processStatuses.TryGetValue(processKey, out var status))
                      continue;
                    
                    if (status.IsRateLimited && status.ResetTime.HasValue) {
                        var timeUntilReset = (status.ResetTime.Value - currentTime).TotalMinutes;
                        
                        // Send continue shortly after the rate limit has expired (1 minute after reset)
                        if (timeUntilReset <= -1 && process.HasConsoleWindow) {
                            var success = SendContinueToConsole(process.ConsoleHandle!.Value, process.ProcessId, process.ConfigPath);
                            if (success)
                                lastDisplayUpdate = DateTime.MinValue;
                        }
                    }
                }
                
                if (lastDisplayUpdate == DateTime.MinValue || (currentTime - lastDisplayUpdate).TotalSeconds > DISPLAY_UPDATE_SECONDS) {
                    ShowStatus(claudeProcesses, processStatuses);
                    lastDisplayUpdate = currentTime;
                }
                
                for (int i = 0; i < CHECK_INTERVAL_SECONDS && running; ++i)
                    Thread.Sleep(1000);
            } catch (Exception ex) {
                Console.WriteLine("Error in main loop: " + ex.Message);
                Thread.Sleep(5000);
            }
        }
        
        Console.WriteLine("Ultimate Claude Monitor stopped.");
    }

    public static SessionWindow CalculateSessionWindow(DateTime sessionStart, DateTime currentTime, DateTime? rateLimitResetTime = null, int? rateLimitHours = null) {
        // If we have rate limit information, use it to determine the true window
        if (rateLimitResetTime.HasValue && rateLimitHours.HasValue) {
            var trueWindowStart = rateLimitResetTime.Value.AddHours(-rateLimitHours.Value);
            var trueWindowEnd = rateLimitResetTime.Value;
            
            // Calculate which block this would be based on original session start
            var hoursFromSessionStart = trueWindowStart.Subtract(sessionStart).TotalHours;
            var blockNumber = Math.Max(0, (int)Math.Floor(hoursFromSessionStart / 5.0));
            
            return new SessionWindow {
                WindowStart = trueWindowStart,
                WindowEnd = trueWindowEnd,
                WindowNumber = blockNumber + 1
            };
        }
        
        // Original logic when no rate limit info available
        var hoursSinceStart = currentTime.Subtract(sessionStart).TotalHours;
        var currentBlock = (int)Math.Floor(hoursSinceStart / 5.0);
        var windowStart = sessionStart.AddHours(currentBlock * 5);
        var windowEnd = windowStart.AddHours(5);
        
        return new SessionWindow {
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            WindowNumber = currentBlock + 1
        };
    }

    public class SessionWindow {
        public DateTime WindowStart { get; set; }
        public DateTime WindowEnd { get; set; }
        public int WindowNumber { get; set; }
        public TimeSpan TimeRemaining { get { return WindowEnd.Subtract(DateTime.Now); } }
        public string WindowDisplay { get { return WindowStart.ToString("HH:mm") + "-" + WindowEnd.ToString("HH:mm"); } }
    }

    public static TokenDepletion CalculateTokenDepletion(int currentTokens, double tokensPerMinute, int tokenLimit) {
        var tokensRemaining = tokenLimit - currentTokens;
        
        if (tokensPerMinute <= 0)
            return new TokenDepletion { ProjectedDepletionDisplay = "No burn rate data", TokensRemaining = tokensRemaining };
        
        if (tokensRemaining <= 0)
            return new TokenDepletion {
                ProjectedDepletionTime = DateTime.Now,
                ProjectedDepletionDisplay = "EXCEEDED",
                TokensRemaining = tokensRemaining,
                MinutesToDepletion = 0
            };
        
        var minutesToDepletion = tokensRemaining / tokensPerMinute;
        var depletionTime = DateTime.Now.AddMinutes(minutesToDepletion);
        
        string depletionDisplay;
        if (minutesToDepletion < 60) {
            depletionDisplay = Math.Round(minutesToDepletion, 1) + "min";
        } else if (minutesToDepletion < 1440) {
            depletionDisplay = ((int)Math.Floor(minutesToDepletion / 60)) + "h" + ((int)Math.Round(minutesToDepletion % 60)) + "m";
        } else {
            depletionDisplay = ((int)Math.Floor(minutesToDepletion / 1440)) + "d" + ((int)Math.Round((minutesToDepletion % 1440) / 60)) + "h";
        }
        
        return new TokenDepletion {
            ProjectedDepletionTime = depletionTime,
            ProjectedDepletionDisplay = depletionDisplay,
            TokensRemaining = tokensRemaining,
            MinutesToDepletion = minutesToDepletion
        };
    }

    public class TokenDepletion {
        public DateTime? ProjectedDepletionTime { get; set; }
        public string ProjectedDepletionDisplay { get; set; }
        public int TokensRemaining { get; set; }
        public double? MinutesToDepletion { get; set; }
        
        public TokenDepletion() {
            ProjectedDepletionDisplay = "";
        }
    }
    
    public static DateTime FindBillingWindowStart(string logFilePath) {
        if (!File.Exists(logFilePath)) return RoundDownToHour(DateTime.Now);
            
        try {
            var lines = File.ReadAllLines(logFilePath);
            DateTime? mostRecentRateLimitTime = null, activityAfterRateLimit = null;
            var fallbackEarliestTime = DateTime.Now;
            var rateLimitPattern = new Regex(@"rate.{0,20}limit|limit.{0,20}reach|too.{0,10}many.{0,10}request|exceeded.*limit", RegexOptions.IgnoreCase);
            
            foreach (var line in lines) {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                try {
                    if (line.Contains("\"timestamp\"") && line.Contains("\"message\"")) {
                        var timestampMatch = Regex.Match(line, "\"timestamp\"\\s*:\\s*\"([^\"]+)\"");
                        if (timestampMatch.Success && DateTime.TryParse(timestampMatch.Groups[1].Value, out var timestamp)) {
                            if (timestamp < fallbackEarliestTime)
                                fallbackEarliestTime = timestamp;
                            
                            if (rateLimitPattern.IsMatch(line)) {
                                mostRecentRateLimitTime = timestamp;
                                activityAfterRateLimit = null;
                            }
                            
                            if (mostRecentRateLimitTime.HasValue && 
                                line.Contains("\"usage\"") &&
                                timestamp > mostRecentRateLimitTime.Value &&
                                !activityAfterRateLimit.HasValue) {
                                activityAfterRateLimit = timestamp;
                            }
                        }
                    }
                } catch { }
            }
            
            var actualStart = activityAfterRateLimit ?? mostRecentRateLimitTime ?? fallbackEarliestTime;
            return RoundDownToHour(actualStart);
        } catch { return RoundDownToHour(DateTime.Now); }
    }
    
    static RateLimitCheckResult CheckRateLimitStatus(string logFilePath) {
        try {
            if (!File.Exists(logFilePath)) return new RateLimitCheckResult(false, null, "", null);
            
            var lines = File.ReadAllLines(logFilePath);
            DateTime? mostRecentResetTime = null;
            string rateLimitMessage = "";
            DateTime? lastSuccessfulMessage = null;
            int? rateLimitHours = null;
            
            // Pattern to match Claude's rate limit messages like "5-hour limit reached ∙ resets 4pm"
            var rateLimitResetPattern = new Regex(@"(\d+)-hour\s+limit\s+reached.*?resets\s+(\d+)(am|pm)", RegexOptions.IgnoreCase);
            
            DateTime? mostRecentRateLimitMessageTime = null;
            
            foreach (var line in lines) {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                try {
                    var timestampMatch = Regex.Match(line, "\"timestamp\"\\s*:\\s*\"([^\"]+)\"");
                    if (!timestampMatch.Success || !DateTime.TryParse(timestampMatch.Groups[1].Value, out var timestamp)) {
                        continue;
                    }
                    
                    // Look for rate limit reset messages
                    var resetMatch = rateLimitResetPattern.Match(line);
                    if (resetMatch.Success) {
                        var limitHours = int.Parse(resetMatch.Groups[1].Value); // Extract the hour limit (e.g., 5 from "5-hour limit")
                        var resetHour = int.Parse(resetMatch.Groups[2].Value);
                        var isPM = resetMatch.Groups[3].Value.ToLower() == "pm";
                        
                        // Convert to 24-hour format
                        if (isPM && resetHour != 12) resetHour += 12;
                        else if (!isPM && resetHour == 12) resetHour = 0;
                        
                        // Calculate reset time
                        var resetTime = new DateTime(timestamp.Year, timestamp.Month, timestamp.Day, resetHour, 0, 0);
                        
                        // If reset time is before message time, it's the next day
                        if (resetTime <= timestamp) {
                            resetTime = resetTime.AddDays(1);
                        }
                        
                        // Keep track of the most recent rate limit message and its reset time
                        if (!mostRecentRateLimitMessageTime.HasValue || timestamp > mostRecentRateLimitMessageTime.Value) {
                            mostRecentRateLimitMessageTime = timestamp;
                            mostRecentResetTime = resetTime;
                            rateLimitHours = limitHours;
                            rateLimitMessage = resetMatch.Value;
                            // Reset the successful message tracker when we find a newer rate limit
                            lastSuccessfulMessage = null;
                        }
                    }
                    
                    // Check for successful messages after the most recent rate limit message
                    if (mostRecentRateLimitMessageTime.HasValue && line.Contains("\"usage\"") && timestamp > mostRecentRateLimitMessageTime.Value) {
                        if (!lastSuccessfulMessage.HasValue || timestamp > lastSuccessfulMessage.Value) {
                            lastSuccessfulMessage = timestamp;
                        }
                    }
                } catch { }
            }
            
            // Determine current rate limit status
            if (mostRecentResetTime.HasValue) {
                var now = DateTime.Now;
                
                // If current time is past reset time, rate limit has expired
                if (now >= mostRecentResetTime.Value)
                    return new RateLimitCheckResult(false, null, "", null);
                
                // If we're before the reset time, we're still rate limited
                // (Successful messages don't clear rate limits - they could be from other devices)
                return new RateLimitCheckResult(true, mostRecentResetTime.Value, rateLimitMessage, rateLimitHours);
            }
            
            return new RateLimitCheckResult(false, null, "", null);
        } catch { 
            return new RateLimitCheckResult(false, null, "", null); 
        }
    }
    
    static DateTime RoundDownToHour(DateTime dateTime) { 
        return new DateTime(dateTime.Year, dateTime.Month, dateTime.Day, dateTime.Hour, 0, 0, dateTime.Kind); 
    }

    static List<ClaudeProcess> GetClaudeProcesses() {
        var claudeProcesses = new List<ClaudeProcess>();
        
        try {
            var processes = Process.GetProcessesByName("node");
            
            foreach (var process in processes) {
                try {
                    var wmiQuery = "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + process.Id;
                    using var searcher = new ManagementObjectSearcher(wmiQuery);
                    using var collection = searcher.Get();
                    
                    foreach (ManagementObject obj in collection) {
                        var commandLine = obj["CommandLine"]?.ToString();
                        
                        if (!string.IsNullOrEmpty(commandLine) && commandLine.Contains("claude-code")) {
                            
                            // Get environment variables once for this process
                            var envVars = GetEnvironmentVariables(process.Id);
                            
                            // Extract config path from environment variables
                            var configPath = envVars.GetValueOrDefault("CLAUDE_CONFIG_DIR") ??
                                           envVars.GetValueOrDefault("ANTHROPIC_CONFIG_DIR") ??
                                           envVars.GetValueOrDefault("CLAUDE_HOME") ??
                                           Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                            
                            // Extract working directory from environment variables
                            var workingDirectory = envVars.GetValueOrDefault("PWD") ??
                                                 envVars.GetValueOrDefault("CD") ??
                                                 envVars.GetValueOrDefault("INIT_CWD") ??
                                                 "Unknown";
                            
                            InitializeConfigStats(configPath);
                            var friendlyName = Path.GetFileName(configPath);
                            if (string.IsNullOrEmpty(friendlyName)) friendlyName = "Claude";
                            
                            discoveredConfigs.TryAdd(configPath, new(friendlyName, "Cyan", configPath));
                            var consoleWindow = GetProcessConsoleWindow(process.Id);
                            
                            claudeProcesses.Add(new ClaudeProcess {
                                ProcessId = process.Id,
                                ProcessName = process.ProcessName,
                                CommandLine = commandLine,
                                StartTime = process.StartTime,
                                WorkingSet = Math.Round(process.WorkingSet64 / (1024.0 * 1024.0), 2),
                                ConfigPath = configPath,
                                WorkingDirectory = workingDirectory,
                                ProjectName = friendlyName,
                                ConsoleHandle = consoleWindow,
                                HasConsoleWindow = consoleWindow.HasValue,
                                DetectionReason = "Environment Variables"
                            });
                        }
                    }
                } catch { }
            }
        } catch { }
        
        return claudeProcesses;
    }

    static Dictionary<string, string> GetEnvironmentVariables(int processId) {
        var result = new Dictionary<string, string>();
        
        try {
            var hProc = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);
            if (hProc == IntPtr.Zero) {
                Console.WriteLine("Debug: Failed to open process " + processId);
                return result;
            }

            var pbi = new PROCESS_BASIC_INFORMATION();
            var status = NtQueryInformationProcess(hProc, 0, ref pbi, (uint)Marshal.SizeOf(pbi), out var retLen);
            if (status != 0) {
                Console.WriteLine("Debug: NtQueryInformationProcess failed for " + processId + ", status: " + status);
                CloseHandle(hProc);
                return result;
            }

            // Read PEB (just enough to get ProcessParameters)
            var peb = new byte[IntPtr.Size * 6];
            if (!ReadProcessMemory(hProc, pbi.PebBaseAddress, peb, peb.Length, out var bytesRead)) {
                Console.WriteLine("Debug: Failed to read PEB for process " + processId);
                CloseHandle(hProc);
                return result;
            }

            var procParamsAddr = IntPtr.Size == 8 ? 
                (IntPtr)BitConverter.ToInt64(peb, IntPtr.Size * 4) : 
                (IntPtr)BitConverter.ToInt32(peb, IntPtr.Size * 4);

            // Read RTL_USER_PROCESS_PARAMETERS
            var size = Marshal.SizeOf(typeof(RTL_USER_PROCESS_PARAMETERS));
            var ruppBytes = new byte[size];
            if (!ReadProcessMemory(hProc, procParamsAddr, ruppBytes, size, out bytesRead)) {
                Console.WriteLine("Debug: Failed to read process parameters for " + processId);
                CloseHandle(hProc);
                return result;
            }

            var handle = GCHandle.Alloc(ruppBytes, GCHandleType.Pinned);
            var rupp = Marshal.PtrToStructure<RTL_USER_PROCESS_PARAMETERS>(handle.AddrOfPinnedObject());
            handle.Free();

            // Read environment block
            var envBlock = new byte[64 * 1024];
            if (!ReadProcessMemory(hProc, rupp.Environment, envBlock, envBlock.Length, out bytesRead)) {
                Console.WriteLine("Debug: Failed to read environment block for " + processId);
                CloseHandle(hProc);
                return result;
            }

            CloseHandle(hProc);

  
            // For now, let's try reading more of the environment block without early termination
            // Windows environment blocks end with double null, but let's try a larger chunk first
            var envLength = Math.Min(4096, (int)bytesRead); // Read first 4KB instead of searching for terminator
            
            var envString = Encoding.Unicode.GetString(envBlock, 0, envLength);
            var envVars = envString.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var envVar in envVars) {
                if (string.IsNullOrEmpty(envVar)) continue;
                
                var equalIndex = envVar.IndexOf('=');
                if (equalIndex > 0 && equalIndex < envVar.Length - 1) {
                    var key = envVar.Substring(0, equalIndex);
                    var value = envVar.Substring(equalIndex + 1);
                    
                    result[key] = value;
                }
            }
            
        } catch (Exception ex) {
            Console.WriteLine("Debug: GetEnvironmentVariables error: " + ex.Message);
        }

        return result;
    }

    static T BytesToStruct<T>(byte[] bytes) where T : struct {
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try {
            return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
        } finally {
            handle.Free();
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
                        
                        if (classNameStr == "ConsoleWindowClass" || classNameStr == "PseudoConsoleWindow")
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

    static void InitializeConfigStats(string configPath) {
        sessionStats.TryAdd(configPath, new SessionStats { SessionStart = DateTime.Now });
        rateLimitStates.TryAdd(configPath, new RateLimitState());
    }
    
    static void UpdateRateLimitStatus(string logFilePath, string configKey) {
        if (!File.Exists(logFilePath) || !rateLimitStates.ContainsKey(configKey)) return;
        
        try {
            var rateLimitState = rateLimitStates[configKey];
            var result = CheckRateLimitStatus(logFilePath);
            var isRateLimited = result.IsRateLimited;
            var resetTime = result.ResetTime;
            var message = result.Message;
            var rateLimitHours = result.RateLimitHours;
            
            rateLimitState.IsRateLimited = isRateLimited;
            rateLimitState.ResetTime = resetTime;
            rateLimitState.RateLimitMessage = message;
            rateLimitState.RateLimitHours = rateLimitHours;
            
            if (isRateLimited && resetTime.HasValue) {
                rateLimitState.LastRateLimitTime = DateTime.Now; // When we detected the rate limit
            }
        } catch {
            // Silently handle rate limit update errors
        }
    }

    static StatusInfo UpdateConfigStats(string configPath) {
        var logFile = GetLatestLogFile(configPath);
        if (string.IsNullOrEmpty(logFile)) return null;
            
        try {
            InitializeConfigStats(configPath);
            if (sessionStats.TryGetValue(configPath, out var stats)) {
                var actualSessionStart = FindBillingWindowStart(logFile);
                stats.SessionStart = actualSessionStart;
            }
            
            // Update rate limit status before creating status info
            UpdateRateLimitStatus(logFile, configPath);
            
            var status = new StatusInfo();
            var rateLimitState = rateLimitStates[configPath];
            
            status.IsRateLimited = rateLimitState.IsRateLimited;
            status.LastRateLimitTime = rateLimitState.LastRateLimitTime;
            status.ResetTime = rateLimitState.ResetTime;
            status.RateLimitHours = rateLimitState.RateLimitHours;
            status.RateLimitMessage = rateLimitState.RateLimitMessage;
            
            if (stats != null && File.Exists(logFile)) {
                ParseJsonlFile(logFile, stats);
                status.Statistics = stats;
            }
            
            return status;
        } catch { return null; }
    }

    static StatusInfo UpdateProcessStats(ClaudeProcess process) {
        var configPath = process.ConfigPath;
        var processKey = "Process_" + process.ProcessId;
        var mostRecentJsonl = GetMostRecentJsonlForProcess(configPath, process);
        if (string.IsNullOrEmpty(mostRecentJsonl)) {
            return null;
        }
            
        try {
            if (!sessionStats.ContainsKey(processKey)) {
                sessionStats[processKey] = new SessionStats {
                    SessionStart = DateTime.Now.AddHours(-1),
                    WorkingDirectory = process.WorkingDirectory
                };
            }
            
            rateLimitStates.TryAdd(processKey, new RateLimitState());
            
            var actualSessionStart = FindBillingWindowStart(mostRecentJsonl);
            sessionStats[processKey].SessionStart = actualSessionStart;
            
            // Update rate limit status before creating status info
            UpdateRateLimitStatus(mostRecentJsonl, processKey);
            
            var status = new StatusInfo();
            var rateLimitState = rateLimitStates[processKey];
            
            status.IsRateLimited = rateLimitState.IsRateLimited;
            status.LastRateLimitTime = rateLimitState.LastRateLimitTime;
            status.ResetTime = rateLimitState.ResetTime;
            status.RateLimitMessage = rateLimitState.RateLimitMessage;
            
            var stats = sessionStats[processKey];
            if (File.Exists(mostRecentJsonl)) {
                ParseJsonlFile(mostRecentJsonl, stats);
                status.Statistics = stats;
            }
            
            return status;
        } catch { return null; }
    }

    static string GetMostRecentJsonlForProcess(string configPath, ClaudeProcess process) {
        try {
            var projectsPath = Path.Combine(configPath, "projects");
            if (!Directory.Exists(projectsPath)) return null;
                
            return Directory.GetFiles(projectsPath, "*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
        } catch { return null; }
    }

    static void ParseJsonlFile(string filePath, SessionStats stats) {
        try {
            var lines = File.ReadAllLines(filePath);
            var cwdExtracted = false;
            
            foreach (var line in lines) {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
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
                        
                        // Extract token counts with regex parsing
                        var inputTokens = GetJsonIntValueRegex(line, "input_tokens");
                        var outputTokens = GetJsonIntValueRegex(line, "output_tokens");
                        var cacheCreation = GetJsonIntValueRegex(line, "cache_creation_input_tokens");
                        var cacheRead = GetJsonIntValueRegex(line, "cache_read_input_tokens");
                        
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
    
    static int GetJsonIntValueRegex(string jsonLine, string propertyName) {
        var pattern = "\"" + propertyName + "\"\\s*:\\s*(\\d+)";
        var match = Regex.Match(jsonLine, pattern);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var value)) {
            return value;
        }
        return 0;
    }

    static string GetLatestLogFile(string configPath) {
        try {
            var projectsPath = Path.Combine(configPath, "projects");
            if (!Directory.Exists(projectsPath)) return null;
                
            return Directory.GetDirectories(projectsPath)
                .SelectMany(dir => Directory.GetFiles(dir, "*.jsonl"))
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
        } catch { return null; }
    }

    static bool SendContinueToConsole(IntPtr windowHandle, int processId, string configPath) {
        try {
            ShowWindow(windowHandle, SW_RESTORE);
            Thread.Sleep(200);
            SetForegroundWindow(windowHandle);
            Thread.Sleep(300);
            
            SendKeys.SendWait("continue");
            Thread.Sleep(100);
            SendKeys.SendWait("{ENTER}");
            
            SessionStats stats;
            if (sessionStats.TryGetValue(configPath, out stats)) {
                ++stats.ContinuesSent;
                stats.LastContinueSent = DateTime.Now;
            }
            
            lastContinueSent[configPath] = DateTime.Now;
            return true;
        } catch { return false; }
    }
    
    static void ShowStatus(List<ClaudeProcess> claudeProcesses, Dictionary<string, StatusInfo> processStatuses) {
        try { Console.Clear(); } catch { Console.WriteLine("\n\n\n"); }
        
        var currentTime = DateTime.Now;
        
        WriteLineAnsi(GREEN + "============================================" + RESET);
        WriteLineAnsi(GREEN + "  :robot:  ULTIMATE CLAUDE BACKGROUND MONITOR  :rocket:  " + RESET);
        WriteLineAnsi(GREEN + "============================================" + RESET);
        WriteLineAnsi(YELLOW + ":clock: Current Time: " + currentTime.ToString("yyyy-MM-dd HH:mm:ss") + RESET);
        WriteLineAnsi("");
        
        if (claudeProcesses.Count == 0) {
            WriteLineAnsi(RED + ":cross: No Claude processes found! Waiting for Claude Code to start..." + RESET);
            WriteLineAnsi("");
            WriteLineAnsi(CYAN + "+--- SUMMARY ------------------------------------------------------------------+" + RESET);
            WriteLineAnsi(CYAN + "| " + YELLOW + ":hourglass: WAIT" + CYAN + " | Scanning for Claude Code processes... Press Ctrl+C to stop" + WHITE + " |" + RESET);
            WriteLineAnsi(CYAN + "+------------------------------------------------------------------------------+" + RESET);
            WriteLineAnsi("");
            WriteLineAnsi(RED + ":warning: Monitoring continues - Press Ctrl+C to stop..." + RESET);
            return;
        }
        
        var processesByConfig = claudeProcesses.GroupBy(p => p.ConfigPath).ToDictionary(g => g.Key, g => g.ToList());
        var instanceNumber = 1;
        
        foreach (var process in claudeProcesses) {
            var configPath = process.ConfigPath;
            var configInfo = discoveredConfigs.GetValueOrDefault(configPath);
            var baseName = configInfo?.Name ?? Path.GetFileName(configPath);
            if (string.IsNullOrEmpty(baseName)) baseName = "Unknown";
            
            var processCount = claudeProcesses.Count(p => p.ConfigPath == configPath);
            var configName = processCount > 1 ? baseName + " #" + instanceNumber : baseName;
            
            var processKey = "Process_" + process.ProcessId;
            var status = processStatuses.GetValueOrDefault(processKey);
            var stats = sessionStats.GetValueOrDefault(processKey);
            
            ShowInstanceStatusBar(configName, configPath, new List<ClaudeProcess> { process }, status, stats, currentTime);
            ++instanceNumber;
        }
        
        var totalCost = sessionStats.Values.Sum(s => s.TotalCost);
        var totalTokens = sessionStats.Values.Sum(s => s.TotalInputTokens + s.TotalOutputTokens);
        var totalRequests = sessionStats.Values.Sum(s => s.TotalRequests);
        var totalContinues = sessionStats.Values.Sum(s => s.ContinuesSent);
        
        WriteLineAnsi(GREEN + "+--- SUMMARY ------------------------------------------------------------------+" + RESET);
        var summaryText = ":computer: Configs: " + discoveredConfigs.Count + " | :gear: Processes: " + claudeProcesses.Count + " | :chart: Total: " + totalTokens.ToString("N0") + " tokens | :money: $" + totalCost.ToString("F4") + " | :arrow: " + totalContinues + " continues";
        
        var textLength = summaryText.Length;
        var emojiSet = unicodeSupported == true ? emojisUnicode : emojisAscii;
        foreach (var emoji in emojiSet) {
            var placeholder = ":" + emoji.Key + ":";
            var count = (summaryText.Length - summaryText.Replace(placeholder, "").Length) / placeholder.Length;
            textLength -= count * (placeholder.Length - emoji.Value.Length);
        }
        
        WriteLineAnsi("| " + WHITE + summaryText + GREEN + new string(' ', Math.Max(0, 77 - textLength)) + "|" + RESET);
        WriteLineAnsi(GREEN + "+------------------------------------------------------------------------------+" + RESET);
        WriteLineAnsi("");
        WriteLineAnsi(RED + ":warning: Press Ctrl+C to stop monitoring..." + RESET);
    }

    static void ShowInstanceStatusBar(string configName, string configPath, List<ClaudeProcess> processes, 
                                     StatusInfo status, SessionStats stats, DateTime currentTime) {
        // Determine status display
        var statusEmoji = ":dot:";
        var statusText = "NO DATA";
        var statusColor = GRAY;
        
        if (status != null) {
            if (status.IsRateLimited) {
                statusEmoji = ":cross:";
                statusText = "RATE LIMITED";
                statusColor = BRIGHT_RED;
            } else if (status.ApproachingRateLimit) {
                statusEmoji = ":warning:";
                statusText = "APPROACHING LIMIT";
                statusColor = BRIGHT_YELLOW;
            } else if (status.RecentActivity.HasValue) {
                var timeSinceActivity = (currentTime - status.RecentActivity.Value).TotalMinutes;
                if (timeSinceActivity < 2) {
                    statusEmoji = ":lightning:";
                    statusText = "ACTIVE NOW";
                    statusColor = BRIGHT_GREEN;
                } else if (timeSinceActivity < 30) {
                    statusEmoji = ":check:";
                    statusText = "RECENT ACTIVITY";
                    statusColor = GREEN;
                } else {
                    statusEmoji = ":circle:";
                    statusText = "IDLE";
                    statusColor = YELLOW;
                }
            } else {
                statusEmoji = ":circle:";
                statusText = "AVAILABLE";
                statusColor = CYAN;
            }
        }
        
        var processInfo = processes.First();
        var workingDir = stats?.WorkingDirectory ?? processInfo.WorkingDirectory ?? "Unknown";
        if (workingDir.Contains("\\")) {
            var parts = workingDir.Split('\\');
            workingDir = parts[parts.Length - 1];
        }
        
        // Show Claude config directory location
        var claudeConfigDir = "~/.claude";
        if (!string.IsNullOrEmpty(processInfo.ConfigPath)) {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var defaultClaudePath = Path.Combine(userProfile, ".claude");
            
            if (!processInfo.ConfigPath.Equals(defaultClaudePath, StringComparison.OrdinalIgnoreCase)) {
                // Not default location, show the directory name
                claudeConfigDir = Path.GetFileName(processInfo.ConfigPath);
                if (string.IsNullOrEmpty(claudeConfigDir)) {
                    // If ConfigPath ends with backslash, get parent directory name
                    var trimmedPath = processInfo.ConfigPath.TrimEnd('\\', '/');
                    claudeConfigDir = Path.GetFileName(trimmedPath);
                }
                if (string.IsNullOrEmpty(claudeConfigDir)) claudeConfigDir = "custom";
            }
        }
        
        var uptime = (currentTime - processInfo.StartTime).TotalMinutes;
        var uptimeStr = uptime < 60 ? Math.Round(uptime, 1) + "min" : Math.Round(uptime / 60, 1) + "h";
        var projectText = workingDir + " [" + claudeConfigDir + "] (PID:" + processInfo.ProcessId + ", " + processInfo.WorkingSet + "MB) :clock: " + uptimeStr;
        
        var modelText = "No Model";
        if (stats?.ModelBreakdown != null && stats.ModelBreakdown.Count > 0) {
            if (stats.ModelBreakdown.Count == 1) {
                var modelName = stats.ModelBreakdown.Keys.First();
                modelText = modelName.Replace("claude-", "").Replace("-20", "");
            } else {
                modelText = stats.ModelBreakdown.Count + " models";
            }
        }
        
        var usageText = "No Usage";
        if (stats != null) {
            var totalTokens = stats.TotalInputTokens + stats.TotalOutputTokens;
            if (totalTokens > 0) {
                var sessionDuration = (currentTime - stats.SessionStart).TotalMinutes;
                usageText = totalTokens.ToString("N0") + " tokens | $" + stats.TotalCost.ToString("F3") + " | " + sessionDuration.ToString("F1") + "min";
                
                if (stats.BurnRateTokensPerMinute > 0) {
                    usageText += " | " + stats.BurnRateTokensPerMinute.ToString("F1") + "/min";
                    if (stats.ProjectedTotalCost > 0)
                        usageText += " -> $" + stats.ProjectedTotalCost.ToString("F2");
                }
            }
        }
        
        var windowText = "No window data";
        var windowColor = BLUE;
        TokenDepletion tokenDepletion = null;
        
        if (stats != null) {
            try {
                // Temporary fix: if we have reset time but no hours, assume 5 hours (most common)
                var effectiveRateLimitHours = status?.RateLimitHours ?? (status?.ResetTime.HasValue == true ? 5 : (int?)null);
                var sessionWindow = CalculateSessionWindow(stats.SessionStart, currentTime, status?.ResetTime, effectiveRateLimitHours);
                windowText = sessionWindow.WindowDisplay + " | Window " + sessionWindow.WindowNumber;
                
                var timeRemaining = sessionWindow.TimeRemaining;
                if (timeRemaining.TotalMinutes > 0) {
                    var timeRemainingText = timeRemaining.TotalMinutes < 60 ? 
                        Math.Round(timeRemaining.TotalMinutes, 1) + "min left" :
                        Math.Floor(timeRemaining.TotalHours) + "h" + timeRemaining.Minutes + "m left";
                    windowText += " :clock: " + timeRemainingText;
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
        
        WriteLineAnsi(GRAY + "+------------------------------------------------------------------------------+" + RESET);
        WriteLineAnsi("| " + statusColor + statusEmoji + " " + statusText.PadRight(74) + WHITE + " |" + RESET);
        WriteLineAnsi("| " + MAGENTA + ":computer: " + projectText.PadRight(78) + WHITE + " |" + RESET);
        WriteLineAnsi("| " + CYAN + ":robot: " + modelText.PadRight(73) + WHITE + " |" + RESET);
        WriteLineAnsi("| " + GREEN + ":money: " + usageText.PadRight(73) + WHITE + " |" + RESET);
        WriteLineAnsi("| " + windowColor + ":hourglass: " + windowText.PadRight(78) + WHITE + " |" + RESET);
        WriteLineAnsi(GRAY + "+------------------------------------------------------------------------------+" + RESET);
        WriteLineAnsi("");
    }
}
"@
Write-Host "C# monitoring application compiled successfully!" -ForegroundColor Green

# Simple PowerShell wrapper that just calls into the gigantic C# monitoring application
try {
    Invoke-Expression "[$className]::RunMonitor()"
} catch {
    if ($_.Exception.GetType().Name -eq "PipelineStoppedException") {
        Write-Host "`nMonitoring stopped by user." -ForegroundColor Yellow
    } else {
        Write-Error "Unexpected error: $($_.Exception.Message)"
    }
}
