# Exercise the packaged shell, download and on-quit NSIS install on a disposable Windows CI runner.
$ErrorActionPreference = 'Stop'
if ($env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:KUNLUN_DESKTOP_ORIGIN -ne 'https://kunlun-test.idalee.cn') {
    throw 'Run on the hosted Windows CI runner with the test installer.'
}
$release = (Resolve-Path 'release/test').Path
$resources = Join-Path $release 'win-unpacked/resources'
$config = Get-Content "$resources/app-update.yml" -Raw
$package = Get-Content 'package.json' -Raw
$version = ($package | ConvertFrom-Json).version
$older = $package | ConvertFrom-Json
$older.version = '0.0.1'
try {
    $older | ConvertTo-Json -Depth 10 | Set-Content 'package.json' -Encoding utf8
    node scripts/package.mjs --unpacked
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the older shell' }
} finally {
    [IO.File]::WriteAllText((Resolve-Path 'package.json'), $package)
}

# The installer and manifest retain their real bytes. Only the unpacked old shell uses a local test feed.
$port = 18741
($config -replace 'https://kunlun-test.idalee.cn/downloads/windows/', "http://127.0.0.1:$port/") | Set-Content "$resources/app-update.yml" -Encoding utf8
$server = Start-Process python -ArgumentList '-m', 'http.server', $port, '--bind', '127.0.0.1' -WorkingDirectory $release -PassThru
function Wait-Until($label, [scriptblock]$check) {
    for ($i = 0; $i -lt 90; $i++) {
        if (& $check) { return }
        Start-Sleep -Seconds 1
    }
    throw "Timed out waiting for $label"
}
try {
    Wait-Until 'update feed' { try { (Invoke-WebRequest "http://127.0.0.1:$port/latest.yml").StatusCode -eq 200 } catch { $false } }
    $old = Start-Process "$release/win-unpacked/kunlun-admin-test.exe" -PassThru
    $cache = ($config -split '\r?\n' | Where-Object { $_ -match '^updaterCacheDirName:' }) -replace '^updaterCacheDirName:\s*', ''
    $pending = Join-Path $env:LOCALAPPDATA ($cache.Trim("'`"") + '/pending/update-info.json')
    Wait-Until 'verified update download' {
        if ($old.HasExited) { throw "Older client exited before updating ($($old.ExitCode))" }
        (Test-Path $pending) -and ((Get-Content $pending -Raw | ConvertFrom-Json).fileName -eq "kunlun-admin-test-setup-$version.exe")
    }
    Wait-Until 'client window' { $old.Refresh(); $old.MainWindowHandle -ne 0 }
    if (-not $old.CloseMainWindow()) { throw 'Could not close the client normally' }
    Wait-Until 'new installed version' {
        Get-ItemProperty 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/*' -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayVersion -eq $version -and $_.DisplayName -like '*（测试）*' }
    }
    Write-Host "The older test client downloaded and installed $version on normal exit."
} finally {
    Stop-Process -Id $server.Id -ErrorAction SilentlyContinue
}
