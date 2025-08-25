# Ultimate Claude Background Monitor
# Monitors Claude instances, handles rate limits, sends auto-continue, tracks costs and tokens
# Run with: Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process; .\claude-ultimate-monitor.ps1

# Generate unique class name based on script content hash
$scriptContent = Get-Content "$PSScriptRoot\ClaudeMonitor.cs" -Raw
$hash = [System.Security.Cryptography.MD5]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($scriptContent))
$hashString = [System.BitConverter]::ToString($hash).Replace("-", "").Substring(0, 8)
$className = "ClaudeMonitorApp_$hashString"

Write-Host "Compiling C# monitoring application ($className)..." -ForegroundColor Yellow
Add-Type -ReferencedAssemblies @(
    "System.Windows.Forms", 
    "System.Management", 
    "System.Memory", 
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
    "System.Text.Json",
    "mscorlib"
) -TypeDefinition (Get-Content "$PSScriptRoot\ClaudeMonitor.cs" -Raw).Replace('__Program__', $className)
Write-Host "C# monitoring application compiled successfully!" -ForegroundColor Green

# Simple PowerShell wrapper that just calls into the gigantic C# monitoring application
try {
    Invoke-Expression "[$className]::Main()"
} catch {
    if ($_.Exception.GetType().Name -eq "PipelineStoppedException") {
        Write-Host "`nMonitoring stopped by user." -ForegroundColor Yellow
    } else {
        Write-Error "Unexpected error: $($_.Exception.Message)"
    }
}
