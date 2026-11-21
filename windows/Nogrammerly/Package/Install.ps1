# Installs Nogrammerly for the current user.
#
# With Developer Mode on, the app is registered as a package from its install folder. That gives it the
# package identity the Windows on-device AI requires, adds it to Start, and starts it when you sign in.
# Without Developer Mode it is installed as a plain app: everything works except the on-device AI.
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'

$target = Join-Path $env:LOCALAPPDATA 'Programs\Nogrammerly'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Nogrammerly.lnk'
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'Nogrammerly.lnk'

function Remove-Nogrammerly {
    Get-Process Nogrammerly -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-AppxPackage -Name Nogrammerly | Remove-AppxPackage
    Remove-Item $startMenu, $startup -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
}

function New-Shortcut($path) {
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($path)
    $shortcut.TargetPath = Join-Path $target 'Nogrammerly.exe'
    $shortcut.WorkingDirectory = $target
    $shortcut.Save()
}

if ($Uninstall) {
    Remove-Nogrammerly
    # Undo "Set up Word add-in": take the add-in out of Word and remove its certificate.
    # Windows asks before it removes a certificate from the trusted list.
    $wordKey = 'HKCU:\Software\Microsoft\Office\16.0\WEF\Developer'
    $addinId = '6f0c1f0e-6b1c-4f6e-9a57-2f1d5b7c8e41'
    $manifest = Join-Path $env:LOCALAPPDATA 'Nogrammerly\word-addin.xml'
    if ((Get-ItemProperty $wordKey -ErrorAction SilentlyContinue).$addinId -eq $manifest) {
        Remove-ItemProperty $wordKey -Name $addinId -ErrorAction SilentlyContinue
    }
    Remove-Item $manifest -ErrorAction SilentlyContinue
    Get-ChildItem Cert:\CurrentUser\My, Cert:\CurrentUser\Root |
        Where-Object { $_.FriendlyName -eq 'Nogrammerly add-in (this PC only)' } |
        Remove-Item -ErrorAction SilentlyContinue
    Write-Host 'Nogrammerly has been removed.'
    exit
}

Write-Host 'Installing Nogrammerly...'
Remove-Nogrammerly
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot '*') -Destination $target -Recurse -Force
# Files from a downloaded zip are marked as coming from the internet; clear that so Windows doesn't block them.
Get-ChildItem $target -Recurse -File | Unblock-File

# Tell Word where the Word add-in's manifest will be. The file isn't there until the user chooses
# "Set up Word add-in" in the app, and Word ignores the entry until then. It is written here because the
# app, once installed as a package, can't make registry changes that other apps see.
$wordKey = 'HKCU:\Software\Microsoft\Office\16.0\WEF\Developer'
$addinId = '6f0c1f0e-6b1c-4f6e-9a57-2f1d5b7c8e41'
$addinManifest = Join-Path $env:LOCALAPPDATA 'Nogrammerly\word-addin.xml'
$current = (Get-ItemProperty $wordKey -ErrorAction SilentlyContinue).$addinId
# Leave the entry alone if it points at another copy that exists, such as a developer's own.
if (-not $current -or -not (Test-Path -LiteralPath $current)) {
    # Only make the key if it is missing: making it again would wipe what other add-ins keep there.
    if (-not (Test-Path $wordKey)) { New-Item $wordKey -Force | Out-Null }
    Set-ItemProperty $wordKey -Name $addinId -Value $addinManifest
}

$devMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense -eq 1
$registered = $false
if ($devMode) {
    try {
        Add-AppxPackage -Register (Join-Path $target 'AppxManifest.xml')
        $registered = $true
    } catch {
        Write-Warning "Couldn't register the package, so on-device AI won't be available: $($_.Exception.Message)"
    }
}

if ($registered) {
    $family = (Get-AppxPackage -Name Nogrammerly).PackageFamilyName
    Start-Process "shell:AppsFolder\$family!Nogrammerly"
    Write-Host 'Done. Nogrammerly is running in the notification area (near the clock) and starts when you sign in.'
} else {
    New-Shortcut $startMenu
    New-Shortcut $startup
    Start-Process (Join-Path $target 'Nogrammerly.exe')
    Write-Host 'Done. Nogrammerly is running in the notification area (near the clock) and starts when you sign in.'
    if (-not $devMode) {
        Write-Host ''
        Write-Host 'On-device AI is off. On a Copilot+ PC, turn on Settings > System > For developers > Developer Mode,'
        Write-Host 'then run Install.cmd again to enable AI grammar checks and rewrites.'
    }
}
