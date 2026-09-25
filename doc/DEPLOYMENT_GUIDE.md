# SmartBOQ Deployment & Client Distribution Guide

This document describes the deployment pipeline, build flags, runtime verification gate, and client distribution guidelines for SmartBOQ Enterprise.

---

## 1. System Requirements

### Target Client Environment
* **Operating System**: Windows 10 (Build 19041+) or Windows 11 (64-bit architecture `win-x64`).
* **Runtime**: **Microsoft .NET 10 Windows Desktop Runtime (x64)**.
  * Specifically requires `Microsoft.WindowsDesktop.App` version `10.x` to render the WPF XAML visual tree.
* **Hardware**: Minimum 4 GB RAM, 200 MB free disk space.
* **Microsoft Excel**: Office 2016, Office 2019, Office 2021, or Microsoft 365 (64-bit or 32-bit).

---

## 2. Automated Publishing Pipeline (`publish.ps1` / `publish.cmd`)

The project includes an automated deployment script in English:

* **`publish.cmd`**: 1-click Windows command-line wrapper with UTF-8 support (`chcp 65001`) that invokes `publish.ps1` bypassing PowerShell execution policy restrictions.
* **`publish.ps1`**: The primary PowerShell automation engine.

### How to Run:
Double-click `publish.cmd` from the root directory or execute in terminal:
```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

---

## 3. The .NET 10 Runtime Verification Gate

Before initiating publication or launching the application, `publish.ps1` runs a 3-stage environment verification:

1. **CLI Runtime Probe**: Executes `dotnet --list-runtimes` checking for `Microsoft.WindowsDesktop.App 10.*`.
2. **Physical Path Inspection**: Scans `C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App` for version `10.*` folders.
3. **64-bit Registry Check**: Inspects `HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App`.

### Automated Missing-Runtime Handling:
If the runtime is missing:
* A formatted terminal warning and Windows GUI MessageBox are displayed.
* The system automatically launches the user's default browser directly to Microsoft's official download portal:
  `https://dotnet.microsoft.com/download/dotnet/10.0`
* The script pauses and allows the user to press `[ENTER]` to re-verify once installation is complete.

---

## 4. Single-File Publishing Flags Explained

SmartBOQ produces a single, compressed executable package using the following `dotnet publish` parameters:

```powershell
dotnet publish "src\SmartBOQ.App\SmartBOQ.App.csproj" `
    -r win-x64 `
    -c Release `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output "$env:USERPROFILE\Desktop\SmartBOQ_Client"
```

### Purpose of Key Flags:
* **`-r win-x64`**: Compiles specifically for Windows 64-bit architecture.
* **`--self-contained false`**: Keeps the package lightweight (~70 MB instead of ~220 MB) by relying on the verified .NET Desktop Runtime on the machine.
* **`-p:PublishSingleFile=true`**: Bundles all managed DLLs (ClosedXML, ExcelDataReader, MahApps, etc.) into `SmartBOQ.App.exe`.
* **`-p:EnableCompressionInSingleFile=true`**: Compresses the embedded bundle, reducing executable size by over 50%.
* **`-p:IncludeNativeLibrariesForSelfExtract=true`**: Embeds native unmanaged C++ libraries (such as SQLite `e_sqlite3.dll`, DirectX graphics compilers, and WPF native DLLs) inside the executable for automatic self-extraction.
* **`-p:DebugType=None -p:DebugSymbols=false`**: Strips all debugging symbols (`.pdb` files) for commercial release.

---

## 5. Client Folder Distribution Standards

When publishing, the output is placed strictly into:
`%USERPROFILE%\Desktop\SmartBOQ_Client\`

### Enforced Cleanliness Rules:
1. **Zero Desktop Shortcuts**: No `.lnk` shortcut files are created on the user's Desktop. Everything is contained within the folder.
2. **No Unwanted Sample Files**: Sample Excel workbooks (`*.xlsx`) are excluded from the distribution folder.
3. **No Script Files in Client Folder**: `publish.cmd` and `publish.ps1` remain in the developer workspace and are not bundled into the client distribution directory.
4. **Target Structure**:
   ```
   Desktop/
   └── SmartBOQ_Client/
       ├── SmartBOQ.App.exe   (Single-file compressed executable, ~70 MB)
       └── Resources/         (WPF runtime resources)
   ```
