<#
================================================================================
 SmartBOQ Enterprise - Automated Client Packaging & Deployment Script
 Targets: .NET 10 Windows Desktop (WPF x64)
================================================================================
#>

$ErrorActionPreference = "Stop"

function Show-Banner {
    Clear-Host
    Write-Host "==================================================================" -ForegroundColor Cyan
    Write-Host "       SmartBOQ Enterprise - Client Distribution & Launcher       " -ForegroundColor Yellow
    Write-Host "==================================================================" -ForegroundColor Cyan
    Write-Host ""
}

Show-Banner

# ------------------------------------------------------------------------------
# 1. Environment & Path Detection
# ------------------------------------------------------------------------------
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $projectRoot "src\SmartBOQ.App\SmartBOQ.App.csproj"
$localExePath = Join-Path $projectRoot "SmartBOQ.App.exe"

$desktopPath = [Environment]::GetFolderPath("Desktop")
$outputPath  = Join-Path $desktopPath "SmartBOQ_Client"

$isDevEnvironment = (Test-Path $projectPath)

# ------------------------------------------------------------------------------
# 2. .NET 10 Windows Desktop Runtime Verification Gate
# ------------------------------------------------------------------------------
function Test-DotNet10DesktopRuntime {
    # Check 1: dotnet CLI installed runtimes
    try {
        $runtimes = & dotnet --list-runtimes 2>$null
        if ($LASTEXITCODE -eq 0 -and $runtimes) {
            foreach ($line in $runtimes) {
                if ($line -match "Microsoft\.WindowsDesktop\.App\s+10\.") {
                    return $true
                }
            }
        }
    } catch { }

    # Check 2: Physical shared runtime folder
    $sharedDesktopPath = "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App"
    if (Test-Path $sharedDesktopPath) {
        $v10 = Get-ChildItem -Path $sharedDesktopPath -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "10.*" }
        if ($v10) {
            return $true
        }
    }

    # Check 3: Windows 64-bit Registry
    try {
        $regPath = "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"
        if (Test-Path $regPath) {
            $props = (Get-ItemProperty -Path $regPath -ErrorAction SilentlyContinue).PSObject.Properties
            foreach ($p in $props) {
                if ($p.Name -like "10.*" -and $p.Value -eq 1) {
                    return $true
                }
            }
        }
    } catch { }

    return $false
}

function Ensure-DotNetRuntimeInstalled {
    $downloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0"

    while (-not (Test-DotNet10DesktopRuntime)) {
        Write-Host "==================================================================" -ForegroundColor Red
        Write-Host "  [!] REQUIRED RUNTIME MISSING: Microsoft .NET 10 Desktop Runtime" -ForegroundColor Yellow
        Write-Host "==================================================================" -ForegroundColor Red
        Write-Host "  The application requires .NET 10 Windows Desktop Runtime (x64)" -ForegroundColor White
        Write-Host "  to run the engineering UI and calculation engine." -ForegroundColor White
        Write-Host ""
        Write-Host "[INFO] Opening official Microsoft .NET download page in browser..." -ForegroundColor Green
        Write-Host "       URL: $downloadUrl" -ForegroundColor Gray
        Write-Host ""

        try {
            Start-Process $downloadUrl
        } catch {
            Write-Host "       Please open URL manually: $downloadUrl" -ForegroundColor White
        }

        # Optional Windows GUI Alert
        try {
            Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
            $msg = "Microsoft .NET 10 Windows Desktop Runtime (x64) is required.`n`nThe official download page has been opened in your browser.`n`nPlease install the runtime and click OK to continue."
            [System.Windows.Forms.MessageBox]::Show($msg, "Required Dependency - SmartBOQ", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
        } catch { }

        Write-Host "After installation finishes, press [ENTER] to re-check, or [Ctrl+C] to abort..." -ForegroundColor Cyan
        $null = Read-Host
        Show-Banner
    }

    Write-Host "[PASS] .NET 10 Windows Desktop Runtime is verified and active on this system." -ForegroundColor Green
    Write-Host ""
}

Ensure-DotNetRuntimeInstalled

# ------------------------------------------------------------------------------
# 3. Developer Packaging Mode (Publish & Prepare Distribution)
# ------------------------------------------------------------------------------
if ($isDevEnvironment) {
    Write-Host "[INFO] Configuring Deployment Paths:" -ForegroundColor White
    Write-Host "       Project Root: $projectRoot" -ForegroundColor Gray
    Write-Host "       Project File: $projectPath" -ForegroundColor Gray
    Write-Host "       Desktop Out : $outputPath" -ForegroundColor Gray
    Write-Host ""

    if (Test-Path $outputPath -PathType Leaf) {
        Remove-Item $outputPath -Force
    }
    if (-not (Test-Path $outputPath -PathType Container)) {
        New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
        Write-Host "[OK] Created desktop directory: $outputPath" -ForegroundColor Green
    } else {
        Write-Host "[OK] Desktop output directory ready: $outputPath" -ForegroundColor Green
    }

    Write-Host "[INFO] Publishing Single-File Compressed Release Package..." -ForegroundColor Cyan

    & dotnet publish "$projectPath" `
        -r win-x64 `
        -c Release `
        --self-contained false `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        --output "$outputPath"

    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] Publishing failed with exit code: $LASTEXITCODE" -ForegroundColor Red
        pause
        exit $LASTEXITCODE
    }

    Write-Host "[OK] SmartBOQ.App.exe published successfully (Single-File Compressed Package)." -ForegroundColor Green
    Write-Host ""


    Write-Host ""
    Write-Host "==================================================================" -ForegroundColor Green
    Write-Host "  CLIENT DISTRIBUTION PACKAGE CREATED SUCCESSFULLY!" -ForegroundColor White
    Write-Host "  Location: $outputPath" -ForegroundColor Cyan
    Write-Host "==================================================================" -ForegroundColor Green
    Write-Host ""

    $appExe = Join-Path $outputPath "SmartBOQ.App.exe"
}
else {
    # --------------------------------------------------------------------------
    # 4. Client Launcher Mode (Running inside Client Folder)
    # --------------------------------------------------------------------------
    $appExe = $localExePath
}

# ------------------------------------------------------------------------------
# 5. Launch Application
# ------------------------------------------------------------------------------
if (Test-Path $appExe) {
    if ($env:NON_INTERACTIVE -eq '1') {
        Write-Host "[INFO] Non-interactive execution finished." -ForegroundColor Gray
    }
    elseif ($isDevEnvironment) {
        Write-Host "Do you want to launch SmartBOQ Enterprise now? (Y/N, default Y): " -NoNewline -ForegroundColor Yellow
        $ans = Read-Host
        if ([string]::IsNullOrWhiteSpace($ans) -or $ans.Trim().ToUpper() -eq "Y") {
            Write-Host "[INFO] Launching SmartBOQ Enterprise..." -ForegroundColor Green
            Start-Process -FilePath $appExe -WorkingDirectory (Split-Path -Parent $appExe)
        }
    }
    else {
        Write-Host "[INFO] Launching SmartBOQ Enterprise..." -ForegroundColor Green
        Start-Process -FilePath $appExe -WorkingDirectory (Split-Path -Parent $appExe)
    }
} else {
    Write-Host "[ERROR] Could not find executable: $appExe" -ForegroundColor Red
    pause
}