# scripts/Check-XmlComments.ps1
#
# Build-time guard against the MSB4025 XML comment trap: a `--`
# inside an XML comment (`<!-- a -- b -->`) makes MSBuild refuse
# to load the project / the Wpf XAML compiler fail. This script
# scans every .xaml / .csproj / .props / .targets under -Path for
# such patterns and fails the build with a clear, actionable error
# if any are found -- so the issue surfaces as
#     "Found N XML comment(s) containing '--' (MSB4025 trap):
#        path/to/file.xaml:42  <!-- a -- b -->"
# instead of the cryptic MSB4025 line/column. Run via the
# CheckXmlComments MSBuild target in WinMeters.csproj before
# CoreCompile; opt out with -p:DisableXmlCommentCheck=true on the
# dotnet command line.
#
# Excludes build-artifact dirs (obj/, bin/, publish/) and old/
# unrelated dirs (.git/, Tests/, .Kilobit/) by directory name.

[CmdletBinding()]
param(
    [string]$Path = "."
)

$ErrorActionPreference = "Stop"

$includeGlobs = @("*.xaml", "*.csproj", "*.props", "*.targets")
$excludeDirs  = @("obj", "bin", "publish", ".git", "Tests", ".Kilobit")

$files = Get-ChildItem -Path $Path -Recurse -Include $includeGlobs -ErrorAction SilentlyContinue |
    Where-Object {
        # Reject any path whose any parent directory matches an exclude.
        $rel = $_.DirectoryName.Substring((Resolve-Path $Path).Path.Length).TrimStart('\','/')
        $parts = if ([string]::IsNullOrEmpty($rel)) { @() } else { $rel -split '[\\/]' }
        -not ($parts | Where-Object { $excludeDirs -contains $_ })
    }

$issues = New-Object System.Collections.Generic.List[object]

foreach ($file in $files) {
    try {
        $content = Get-Content -Path $file.FullName -Raw -ErrorAction Stop
    } catch {
        Write-Warning "Check-XmlComments: could not read $($file.FullName) -- skipping."
        continue
    }
    # DOTALL so '.' matches newlines (XML comments are frequently
    # multi-line). Non-greedy so the regex stops at the first
    # '-->' it sees, which is the correct behaviour for nested or
    # adjacent comments (XML disallows nested comments, so the
    # first '<!--' always pairs with the first '-->' after it).
    $matches = [regex]::Matches($content, '<!--(.*?)-->', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    foreach ($match in $matches) {
        $inner = $match.Groups[1].Value
        if ($inner -match '--') {
            $lineNumber = ($content.Substring(0, $match.Index) -split "`n").Count
            $snippet = $match.Value.Substring(0, [Math]::Min(80, $match.Value.Length))
            if ($match.Value.Length -gt 80) { $snippet += "..." }
            $issues.Add([PSCustomObject]@{
                File    = $file.FullName
                Line    = $lineNumber
                Snippet = $snippet
            })
        }
    }
}

if ($issues.Count -gt 0) {
    Write-Host ""
    Write-Host "##[error]Check-XmlComments: Found $($issues.Count) XML comment(s) containing '--' (MSB4025 trap):" -ForegroundColor Red
    foreach ($issue in $issues) {
        Write-Host "  $($issue.File):$($issue.Line)  $($issue.Snippet)" -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "  Fix: replace '--' inside XML comments with em-dash, parentheses, or rephrase." -ForegroundColor Yellow
    Write-Host "  Disable for one build: -p:DisableXmlCommentCheck=true" -ForegroundColor Yellow
    Write-Host ""
    exit 1
}

Write-Host "Check-XmlComments: 0 '--' in XML comments ($($files.Count) file(s) scanned)." -ForegroundColor Green
exit 0
