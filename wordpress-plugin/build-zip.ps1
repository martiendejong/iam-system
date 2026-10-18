# Builds dist/jengo-iam-sso-<version>.zip from the jengo-iam-sso/ folder.
# Entries use forward slashes and a single top-level "jengo-iam-sso/" folder, which is what
# WordPress (Plugins, Add New, Upload) and `wp plugin install` expect.
#
#   composer build-zip        or        powershell -File build-zip.ps1

param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$src = Join-Path $PSScriptRoot 'jengo-iam-sso'
$main = Get-Content (Join-Path $src 'jengo-iam-sso.php') -Raw
if ($main -notmatch '(?m)^\s*\*\s*Version:\s*(\S+)') { throw 'Version header not found in jengo-iam-sso.php' }
$version = $Matches[1]

# Never package a syntax error (php is optional on the machine that builds).
$php = Get-Command php -ErrorAction SilentlyContinue
if ($php) {
    Get-ChildItem $src -Recurse -Filter *.php | ForEach-Object {
        & $php.Source -l $_.FullName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "PHP syntax error in $($_.FullName)" }
    }
} else {
    Write-Warning 'php not found: skipped the syntax check.'
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$zip = Join-Path $OutDir "jengo-iam-sso-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }

$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $src -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($src.Length).TrimStart('\', '/') -replace '\\', '/'
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, "jengo-iam-sso/$relative", [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally {
    $archive.Dispose()
}

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Built $zip"
Write-Host "SHA256 $hash"
