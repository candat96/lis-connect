#!/usr/bin/env pwsh
# Build LIS Connect for Windows
# Publishes Lis.App and Lis.SeedTool as self-contained executables for Windows x64
# Creates a dist archive ready for deployment

$ErrorActionPreference = "Stop"

# Set up paths (nested Join-Path for PowerShell 5.1 compatibility)
$RepoRoot = Split-Path -Parent $PSScriptRoot
$DistDir = Join-Path (Join-Path $RepoRoot "dist") "LisConnect-win-x64"
$AppDir = Join-Path $DistDir "app"
$ToolDir = Join-Path $DistDir "seedtool"
$ZipPath = Join-Path (Join-Path $RepoRoot "dist") "LisConnect-win-x64.zip"

Write-Host "Building LIS Connect for Windows x64..."
Write-Host "Repository root: $RepoRoot"
Write-Host "Output directory: $DistDir"

# Clean old build
if (Test-Path $DistDir) {
    Remove-Item $DistDir -Recurse -Force
    Write-Host "Cleaned old build directory"
}

New-Item -ItemType Directory -Path $AppDir -Force | Out-Null
New-Item -ItemType Directory -Path $ToolDir -Force | Out-Null
Write-Host "Created output directories"

# Publish Lis.App
Write-Host "`nPublishing Lis.App..."
$AppProjPath = Join-Path $RepoRoot "src" "Lis.App" "Lis.App.csproj"
dotnet publish $AppProjPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $AppDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to publish Lis.App"
    exit 1
}

Write-Host "Successfully published Lis.App to $AppDir"

# Publish Lis.SeedTool
Write-Host "`nPublishing Lis.SeedTool..."
$ToolProjPath = Join-Path $RepoRoot "tools" "Lis.SeedTool" "Lis.SeedTool.csproj"
dotnet publish $ToolProjPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $ToolDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to publish Lis.SeedTool"
    exit 1
}

Write-Host "Successfully published Lis.SeedTool to $ToolDir"

# Copy seed data and documentation
Write-Host "`nCopying seed data and documentation..."
$SeedDataPath = Join-Path $RepoRoot "tools" "seed" "mau-danh-muc.xlsx"
if (Test-Path $SeedDataPath) {
    Copy-Item $SeedDataPath (Join-Path $DistDir "mau-danh-muc.xlsx")
    Write-Host "Copied seed data: mau-danh-muc.xlsx"
} else {
    Write-Warning "Seed data not found: $SeedDataPath"
}

$DocPath = Join-Path $RepoRoot "docs" "CHAY-THU-WINDOWS.md"
if (Test-Path $DocPath) {
    Copy-Item $DocPath (Join-Path $DistDir "CHAY-THU-WINDOWS.md")
    Write-Host "Copied documentation: CHAY-THU-WINDOWS.md"
} else {
    Write-Warning "Documentation not found: $DocPath"
}

# Create zip archive
Write-Host "`nCreating archive..."
$DistParentDir = Split-Path $DistDir
$DistDirName = Split-Path $DistDir -Leaf

Push-Location $DistParentDir
if (Test-Path $ZipPath) {
    Remove-Item $ZipPath -Force
}

Compress-Archive -Path $DistDirName -DestinationPath $ZipPath
Pop-Location

if (-not (Test-Path $ZipPath)) {
    Write-Error "Failed to create archive"
    exit 1
}

Write-Host "Successfully created archive: $ZipPath"

# Print summary
Write-Host "`n========================================="
Write-Host "Build Complete!"
Write-Host "========================================="
Write-Host "Distribution directory: $DistDir"
Write-Host "Archive path: $ZipPath"
Write-Host ""
Write-Host "Contents:"
Write-Host "  - app/LisConnect.exe (main application)"
Write-Host "  - seedtool/Lis.SeedTool.exe (seed tool)"
Write-Host "  - mau-danh-muc.xlsx (sample catalog data)"
Write-Host "  - CHAY-THU-WINDOWS.md (setup guide)"
Write-Host ""
Write-Host "Ready for deployment to Windows x64 systems."
