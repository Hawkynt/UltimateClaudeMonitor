# 🖥️ Claude Ultimate Background Monitor

![License](https://img.shields.io/github/license/Hawkynt/UltimateClaudeMonitor)
![Language](https://img.shields.io/github/languages/top/Hawkynt/UltimateClaudeMonitor?color=purple)
[![Last Commit](https://img.shields.io/github/last-commit/Hawkynt/UltimateClaudeMonitor?branch=main)![Activity](https://img.shields.io/github/commit-activity/y/Hawkynt/UltimateClaudeMonitor?branch=main)](https://github.com/Hawkynt/UltimateClaudeMonitor/commits/main)
[![GitHub release](https://img.shields.io/github/v/release/Hawkynt/UltimateClaudeMonitor)](https://github.com/Hawkynt/UltimateClaudeMonitor/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Hawkynt/UltimateClaudeMonitor/total)](https://github.com/Hawkynt/UltimateClaudeMonitor/releases)

A comprehensive real-time monitoring system for Claude Code instances that tracks token usage, costs, burn rates, and provides intelligent forecasting across multiple processes and accounts.

## 🎯 Purpose

Claude Ultimate Background Monitor automatically discovers and monitors all running Claude Code processes, providing detailed analytics on token consumption, cost tracking, and usage forecasting. It features account-aware monitoring that respects shared token limits across processes and provides early warnings when approaching rate limits.

## ⚙️ How it Works

The monitor uses a hybrid PowerShell/C# architecture:

1. **Process Discovery**: Uses WMI queries and Win32 APIs to detect `node.exe` processes running Claude Code
2. **Account Detection**: Parses `.claude.json` configuration files to extract OAuth account information
3. **Usage Tracking**: Analyzes JSONL log files to extract real-time token usage and costs
4. **Statistical Analysis**: Calculates M5, H1, D1 averages with trend analysis and forecasting
5. **Rate Limit Management**: Automatically detects rate limits and sends "continue" commands
6. **Cross-Process Monitoring**: Provides account-level token tracking shared across all processes

## 🔨 Build/Test/Run Guidelines

### Building
```powershell
# .NET compilation (optional, for development)
dotnet build

# PowerShell runtime compilation (recommended)
Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process
.\ClaudeMonitor.ps1
```

### Running
```powershell
# Start monitoring (default mode)
.\ClaudeMonitor.ps1

# CLI commands
.\ClaudeMonitor.ps1 report --days 7 --format table
.\ClaudeMonitor.ps1 accounts
.\ClaudeMonitor.ps1 --help
```

### Testing
The system requires no external test framework - it's designed to monitor live Claude Code processes. Test by:
1. Running Claude Code in different directories
2. Observing real-time statistics and account detection
3. Verifying accurate token counting and cost calculations

## 🏗️ High-Level Structure and Architectural Patterns

### Hybrid Architecture
- **PowerShell Host**: Dynamic C# compilation with unique class names based on content hash
- **Embedded C# Application**: Core monitoring logic with real-time statistics
- **Win32 API Integration**: Direct process memory access and console interaction

### Key Components

#### Data Models
- `AnthropicAccount`: Account-level tracking with 5-hour rate limit windows
- `SessionInfo`: Per-process statistics with time series analysis
- `ClaudeProcess`: Process metadata with console interaction capabilities
- `TimeSeriesStats`: Statistical analysis with trends and forecasting

#### Monitoring Engine
- **Process Discovery**: WMI-based Claude Code detection
- **Configuration Parsing**: `.claude.json` OAuth account extraction
- **Log Analysis**: JSONL parsing with incremental updates
- **Statistical Engine**: M5/H1/D1 averages with percentile calculations

#### Account Management
- **Type Detection**: Automatic detection of Free/Pro/Max/Enterprise accounts (still buggy)
- **Token Limits**: Dynamic limits based on detected account type (50K-1M tokens)
- **Cross-Process Sharing**: Account-wide token pools shared across instances
- **Rate Limit Handling**: Intelligent window tracking with auto-continue

## 📋 Full Feature Set

### ✅ Real-Time Monitoring
- Multi-process Claude Code discovery and tracking
- Live token usage and cost calculations
- Working directory and configuration detection
- Process uptime and memory usage monitoring

### ✅ Account Management  
- OAuth account detection from `.claude.json` files
- Automatic account type detection (Free/Pro/Max/Enterprise)
- Dynamic token limits based on subscription tier
- Email address and UUID tracking

### ✅ Statistical Analysis
- M5, H1, D1 time series with trend analysis
- P90 percentile calculations for usage spikes
- Linear regression for trend forecasting  
- Burn rate calculations (tokens per minute)

### ✅ Rate Limit Intelligence
- 5-hour rolling window tracking with confidence levels
- Automatic rate limit message detection and parsing
- Smart "continue" command scheduling with randomization
- Cross-process rate limit coordination

### ✅ Forecasting & Alerts
- Per-instance token depletion time predictions
- Account-aware forecasting (shared token pools)
- Early warning system for approaching limits
- Real-time trend indicators (Rising/Stable/Falling)

### ✅ CLI & Reporting
- Historical usage reports (7-day, 30-day, custom)
- Multiple output formats (table, CSV, JSON)
- Account discovery and listing
- Per-account cost analysis

### ✅ Display Features
- Color-coded real-time dashboard with emoji support
- Account summary with token remaining indicators
- Per-process detailed statistics
- ASCII/Unicode fallback for terminal compatibility

## 🚀 Planned Features

### 🔄 Enhanced Analytics
- Weekly usage pattern analysis
- Model-specific cost breakdowns
- Cache efficiency tracking and optimization
- Usage anomaly detection

### 🔄 Configuration Management
- Custom token limit overrides
- Configurable alert thresholds
- Multiple account profile support
- Export/import of monitoring settings

### 🔄 Integration Features
- Webhook notifications for rate limits
- Slack/Teams integration for team monitoring
- API endpoint for external monitoring systems
- Database export for long-term analytics

### 🔄 Advanced Forecasting
- Machine learning-based usage prediction
- Seasonal pattern recognition
- Multi-account resource optimization
- Cost optimization recommendations

## ⚠️ Known Bugs and Limitations

### Current Limitations
- **PowerShell Nullable Annotations**: Add-Type doesn't support nullable reference types, causing warnings in PowerShell mode
- **Token Limit Accuracy**: Uses estimated limits based on research rather than official API-provided limits- 
- **Token Cost Accuracy**: Uses hard-coded token prices
- **Windows Only**: Requires Windows for WMI queries and Win32 API access
- **Process Memory Access**: May fail on processes with elevated permissions

### Security Implications
- **Memory Access**: Reads environment variables from other processes (standard Win32 capability)
- **File Access**: Reads configuration and log files with shared file access
- **No Elevation Required**: Operates with standard user permissions
- **No Network Access**: Purely local monitoring, no external communications

### Known Issues
- Rate limit parsing may miss non-standard message formats
- Very large JSONL files (>100MB) may cause memory pressure
- Account type detection depends on available evidence in logs

## 📦 Dependencies

### .NET Runtime Dependencies
- .NET 8.0 Windows Runtime
- System.Management (WMI queries)
- System.Text.Json (configuration and log parsing)
- System.Windows.Forms (SendKeys for auto-continue)

### PowerShell Requirements  
- PowerShell Core 7+
- Execution policy bypass for dynamic compilation

### System Requirements
- Windows 10/11 or Windows Server 2019+
- Claude Code installed and configured
- Access to `~/.claude/` directory and configuration files