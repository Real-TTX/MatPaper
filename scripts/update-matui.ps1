#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Copies the newest MatUi toolkit (the UI of all Mat apps) from a running MatDesign into this app.
.DESCRIPTION
    MatDesign serves the toolkit as an archive (/ui/matui.zip). MatPaper carries its own copy in
    src/MatPaper/wwwroot/matui (rule CODE-03: copy, do not link), so it works offline and as a PWA.
    The toolkit has a version of its own (MatDesign docs/VERSIONING.md): the files of one version belong together, so ALL the files of the archive are
    copied - the four files the app uses, the parts of the stylesheet, CHANGELOG.md and matui.manifest.json with the SHA-256 of every file - and a file
    that was changed by hand is overwritten. Afterwards "node <MatDesign>/tools/matui-check.js src/MatPaper/wwwroot/matui" says which version this is,
    whether a file was changed and what a newer version would take with it (exit code 0 means current and unchanged).
    The few icons that the toolkit's own sprite does not have come from MatDesign's optional icon library:
    scripts/matpaper-icons.txt lists them, and they are written into matpaper-icons.svg (ids mat-il-NAME), a file of MatPaper's own.
    Run this when MatDesign has a new release (read its changelog first: the Migration steps are the app's work), then rebuild and look at the pages.
    The version is the first line of matui.css.
.PARAMETER Url
    Address of MatDesign (default http://localhost:8060, the dev stack).
.EXAMPLE
    ./scripts/update-matui.ps1
.EXAMPLE
    ./scripts/update-matui.ps1 -Url https://design.example.com
#>
param(
    [string]$Url = 'http://localhost:8060'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'src/MatPaper/wwwroot/matui'
$base = $Url.TrimEnd('/')
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('matui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $zip = Join-Path $work 'matui.zip'
    Invoke-WebRequest -Uri ($base + '/ui/matui.zip') -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $work -Force
    $folder = Join-Path $work 'matui'
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    # all the files of the version, never a few of them (docs/VERSIONING.md of MatDesign)
    Copy-Item -Path (Join-Path $folder '*') -Destination $target -Recurse -Force

    # The icons of the library that MatPaper uses: one symbol each, in a sprite of its own.
    $wanted = Get-Content -Path (Join-Path $PSScriptRoot 'matpaper-icons.txt') |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_.Length -gt 0 -and -not $_.StartsWith('#') } |
        Sort-Object -Unique
    $library = (Invoke-WebRequest -Uri ($base + '/ui/iconlib/matui-iconlib.svg') -UseBasicParsing).Content
    if ($library -is [byte[]]) { $library = [System.Text.Encoding]::UTF8.GetString($library) }
    $symbols = @{}
    foreach ($m in [regex]::Matches($library, '<symbol id="mat-il-([a-z0-9-]+)"[^>]*>.*?</symbol>', 'Singleline')) {
        $symbols[$m.Groups[1].Value] = $m.Value
    }
    $missing = $wanted | Where-Object { -not $symbols.ContainsKey($_) }
    if ($missing) { throw ('Not in the icon library: ' + ($missing -join ', ')) }
    $sprite = '<svg xmlns="http://www.w3.org/2000/svg" class="mat-sprite" width="0" height="0" aria-hidden="true" focusable="false">' + "`n"
    foreach ($name in $wanted) { $sprite += '  ' + $symbols[$name] + "`n" }
    $sprite += '</svg>' + "`n"
    [System.IO.File]::WriteAllText((Join-Path $target 'matpaper-icons.svg'), $sprite, (New-Object System.Text.UTF8Encoding($false)))

    Write-Host ('Toolkit updated: ' + (Get-Content (Join-Path $target 'matui.css') -TotalCount 1)) -ForegroundColor Green
    Write-Host ('Library icons: ' + $wanted.Count) -ForegroundColor Green
}
finally {
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}
