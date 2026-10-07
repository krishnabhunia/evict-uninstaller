# Runs only on a disposable Windows CI runner. Never launches the packaged Evict app.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SetupPath,
    [Parameter(Mandatory = $true)][string]$PackagedExe
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) {
    throw 'Installer smoke requires a disposable GitHub Actions Windows runner and RUNNER_TEMP.'
}
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$package = (Resolve-Path -LiteralPath $PackagedExe).Path
$expectedHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
$tempBase = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\', '/')
$fixture = [IO.Path]::GetFullPath((Join-Path $tempBase ('evict-installer-smoke-' + [guid]::NewGuid().ToString('N'))))
$install = Join-Path $fixture 'Installed'
$destination = Join-Path $install 'Evict.exe'
$mockSource = Join-Path $fixture 'MockSource'
$mockOutput = Join-Path $fixture 'MockOutput'
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{B7E7C1F4-3D2A-4F6B-9A1E-5C0D2E8F7A11}_is1'
$views = @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()

function Assert-FixturePath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if (-not $absolute.StartsWith($fixture + '\', [StringComparison]::OrdinalIgnoreCase) -and
        -not $absolute.Equals($fixture, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a path outside this fixture: $absolute"
    }
    if (-not $fixture.StartsWith($tempBase + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fixture must be beneath RUNNER_TEMP.'
    }
    return $absolute
}

function Assert-NoExistingInstall {
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine)) {
        foreach ($view in $views) {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
            try {
                foreach ($keyPath in @('Software\Evict', $uninstallKey)) {
                    $key = $base.OpenSubKey($keyPath)
                    if ($null -ne $key) {
                        $key.Dispose()
                        throw "Refusing smoke test on an existing Evict registration: $hive/$view/$keyPath"
                    }
                }
            } finally { $base.Dispose() }
        }
    }
    $created = $false
    $mutex = [Threading.Mutex]::new($false, 'Local\EvictUninstaller.SingleInstance', [ref]$created)
    try {
        if (-not $created) { throw 'Refusing smoke test while an existing Evict single-instance mutex exists.' }
    } finally { $mutex.Dispose() }
}

function Assert-NoInstanceMutex {
    $created = $false
    $mutex = [Threading.Mutex]::new($false, 'Local\EvictUninstaller.SingleInstance', [ref]$created)
    try {
        if (-not $created) { throw 'Released-instance fixture still has a single-instance mutex.' }
    } finally { $mutex.Dispose() }
}

function Wait-FixtureProcess([Diagnostics.Process]$Process, [int]$Seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while (-not $Process.WaitForExit(1000)) {
        if ([DateTime]::UtcNow -ge $deadline) {
            # Only the recorded fixture PID, never global image-name/taskkill termination.
            $Process.Kill($false)
            $Process.WaitForExit(5000) | Out-Null
            throw "Fixture process PID $($Process.Id) exceeded $Seconds seconds."
        }
    }
    return $Process.ExitCode
}

function Invoke-Setup([string]$Case, [bool]$ShouldSucceed) {
    $log = Join-Path $fixture ($Case + '-setup.log')
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', '/CURRENTUSER',
                   '/TASKS=""', ('/DIR="' + $install + '"'), ('/LOG="' + $log + '"'))
    $process = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $null = $process.Handle # retain the exact process identity through exit/PID reuse
    $processes.Add($process)
    $code = Wait-FixtureProcess $process 120
    Write-Host "$Case Setup exit code: $code"
    if (($ShouldSucceed -and $code -ne 0) -or (-not $ShouldSucceed -and $code -eq 0)) {
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 100 | Write-Host }
        throw "$Case returned unexpected Setup exit code $code. Log: $log"
    }
    return $log
}

function Assert-PackagedExe([string]$Case) {
    if (-not (Test-Path -LiteralPath $destination -PathType Leaf) -or
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $expectedHash) {
        throw "$Case did not install the exact packaged Evict.exe."
    }
}

function Start-Mock([string]$Mode) {
    # Native .NET apphost at the exact destination exercises its mapped-image lock.
    foreach ($file in Get-ChildItem -LiteralPath $mockOutput -File) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $install $file.Name) -Force
    }
    $ready = Join-Path $fixture ($Mode + '-ready.txt')
    $received = Join-Path $fixture ($Mode + '-received.txt')
    $process = Start-Process -FilePath $destination -ArgumentList @($Mode, ('"' + $ready + '"'), ('"' + $received + '"')) -WindowStyle Hidden -PassThru
    $null = $process.Handle # retain the exact process identity through exit/PID reuse
    $processes.Add($process)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $ready)) {
        if ($process.HasExited) { throw "Mock exited before readiness: $($process.ExitCode)" }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Mock readiness timed out.' }
        Start-Sleep -Milliseconds 100
    }
    return @{ Process = $process; Received = $received }
}

function Remove-FixtureRegistrations {
    # Preflight proved the keys absent; cleanup still requires their exact fixture install path.
    foreach ($view in $views) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
        try {
            foreach ($entry in @(@{ Key = 'Software\Evict'; Value = 'InstallDir' },
                                 @{ Key = $uninstallKey; Value = 'InstallLocation' })) {
                $key = $base.OpenSubKey($entry.Key)
                if ($null -eq $key) { continue }
                try { $recorded = [string]$key.GetValue($entry.Value, '') } finally { $key.Dispose() }
                if (-not $recorded) { throw "Missing fixture ownership value: $($entry.Key)" }
                $recordedPath = [IO.Path]::GetFullPath($recorded).TrimEnd('\', '/')
                if (-not $recordedPath.Equals($install, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Refusing to remove a registration outside this fixture: $($entry.Key)"
                }
                $base.DeleteSubKeyTree($entry.Key, $false)
            }
        } finally { $base.Dispose() }
    }
}

Assert-NoExistingInstall
Assert-FixturePath $fixture | Out-Null
New-Item -ItemType Directory -Path $fixture, $mockSource -Force | Out-Null
try {
    Write-Host 'Installer smoke: fresh silent install without production app launch.'
    Invoke-Setup 'fresh' $true | Out-Null
    Assert-PackagedExe 'Fresh install'

    $project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>Evict</AssemblyName>
    <UseAppHost>true</UseAppHost>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@
    $program = @'
using System.IO.Pipes;
using System.Text;

if (args.Length != 3) return 2;
using var mutex = new Mutex(true, @"Local\EvictUninstaller.SingleInstance", out var created);
if (!created) return 3;
if (args[0] == "released")
{
    // Matches the self-update handoff: IPC/mutex disappear before the image is unmapped.
    // Release on the acquiring thread before the first await, then close the named object.
    mutex.ReleaseMutex();
    mutex.Dispose();
    File.WriteAllText(args[1], Environment.ProcessId.ToString());
    await Task.Delay(TimeSpan.FromSeconds(3));
    return 0;
}
if (args[0] == "no-pipe")
{
    File.WriteAllText(args[1], Environment.ProcessId.ToString());
    await Task.Delay(TimeSpan.FromMinutes(2));
    return 4;
}
using var pipe = new NamedPipeServerStream("EvictUninstaller.Args.v1", PipeDirection.In, 1,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
File.WriteAllText(args[1], Environment.ProcessId.ToString());
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
await pipe.WaitForConnectionAsync(timeout.Token);
using var bytes = new MemoryStream();
await pipe.CopyToAsync(bytes, timeout.Token); // must receive EOF when Setup closes its handle
var payload = Encoding.UTF8.GetString(bytes.ToArray());
File.WriteAllText(args[2], payload);
return payload == "--exit" ? 0 : 5;
'@
    Set-Content -LiteralPath (Join-Path $mockSource 'Evict.csproj') -Value $project -Encoding utf8
    Set-Content -LiteralPath (Join-Path $mockSource 'Program.cs') -Value $program -Encoding utf8
    & dotnet build (Join-Path $mockSource 'Evict.csproj') -c Release -o $mockOutput --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $mockOutput 'Evict.exe'))) {
        throw 'Could not build the isolated IPC mock.'
    }

    Write-Host 'Installer smoke: direct pipe, EOF, process wait and mapped-image replacement.'
    $mock = Start-Mock 'pipe'
    $log = Invoke-Setup 'pipe' $true
    $code = Wait-FixtureProcess $mock.Process 10
    if ($code -ne 0 -or -not (Test-Path -LiteralPath $mock.Received) -or
        [IO.File]::ReadAllText($mock.Received) -cne '--exit') {
        throw 'Setup did not deliver exactly --exit and wait for the mock to exit.'
    }
    if ((Get-Content -LiteralPath $log -Raw) -notmatch 'Asking Evict to exit gracefully: PID') {
        throw 'Setup did not execute the direct-pipe exit path.'
    }
    Assert-PackagedExe 'IPC replacement'

    Write-Host 'Installer smoke: released mutex while apphost remains mapped must wait for exit.'
    $mock = Start-Mock 'released'
    Assert-NoInstanceMutex
    if ($mock.Process.HasExited) { throw 'Released-instance fixture exited before Setup started.' }
    $log = Invoke-Setup 'released' $true
    if (-not $mock.Process.HasExited) {
        throw 'Setup completed while the released-instance fixture PID was still running.'
    }
    $code = Wait-FixtureProcess $mock.Process 10
    if ($code -ne 0) { throw "Released-instance fixture exited unexpectedly: $code" }
    Assert-PackagedExe 'Released-mutex replacement'

    Write-Host 'Installer smoke: locked destination without mutex must abort unchanged.'
    $before = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    $lock = [IO.FileStream]::new($destination, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $log = Invoke-Setup 'locked' $false
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $before) {
            throw 'Setup changed a locked destination.'
        }
        if ((Get-Content -LiteralPath $log -Raw) -notmatch 'Destination not ready:') {
            throw 'Setup did not report the pre-copy destination failure.'
        }
    } finally { $lock.Dispose() }

    Write-Host 'Installer smoke: uncontactable mutex owner must abort promptly unchanged.'
    $mock = Start-Mock 'no-pipe'
    $before = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $log = Invoke-Setup 'no-pipe' $false
    if ($watch.Elapsed.TotalSeconds -gt 30 -or $mock.Process.HasExited -or
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $before) {
        throw 'Setup did not abort safely for an uncontactable instance.'
    }
    if ((Get-Content -LiteralPath $log -Raw) -notmatch 'Evict exit pipe unavailable; Windows error') {
        throw 'Setup did not log its bounded pipe-contact failure.'
    }
    $mock.Process.Kill($false)
    $mock.Process.WaitForExit(5000) | Out-Null
    Write-Host 'Installer smoke passed: fresh, IPC replacement, released-mutex replacement, locked image and unavailable pipe.'
} finally {
    foreach ($process in $processes) {
        try {
            if (-not $process.HasExited) {
                $process.Kill($false)
                $process.WaitForExit(5000) | Out-Null
            }
        } finally { $process.Dispose() }
    }
    Remove-FixtureRegistrations
    $safeFixture = Assert-FixturePath $fixture
    if (Test-Path -LiteralPath $safeFixture) {
        $rootIsReparse = ((Get-Item -LiteralPath $safeFixture).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        if ($rootIsReparse -or @(Get-ChildItem -LiteralPath $safeFixture -Recurse -Force -Attributes ReparsePoint).Count -gt 0) {
            throw 'Refusing recursive fixture cleanup through a reparse point.'
        }
        Remove-Item -LiteralPath $safeFixture -Recurse -Force
    }
}
