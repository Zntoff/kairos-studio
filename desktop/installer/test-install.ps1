# Silent round trip in a fresh temporary folder. Never uninstall a pre-existing Kairos installation.
param([Parameter(Mandatory=$true)][string]$Installer,[Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference='Stop'
$installerPath=(Resolve-Path -LiteralPath $Installer).Path
$uninstallKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7AAFBFBF-6B62-4DC3-B559-25CB927CFF44}_is1'
if(Test-Path -LiteralPath $uninstallKey){throw 'An installed Kairos copy exists. Run this test on a clean user/CI runner.'}
$fixture=Join-Path $env:TEMP ('KairosInstallerTest-'+[guid]::NewGuid().ToString('N'))
$application=Join-Path $fixture 'Application'
New-Item -ItemType Directory -Path $fixture|Out-Null
$args=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS','/LANG=english',('/DIR="'+$application+'"'),('/LOG="'+(Join-Path $fixture 'install.log')+'"'))
for($attempt=0;$attempt -lt 2;$attempt++){
    $attemptArgs=$args
    if($attempt -eq 1){$attemptArgs=$args|Where-Object {$_ -notlike '/DIR=*'}; $attemptArgs+=('/DIR="'+(Join-Path $fixture 'Duplicate')+'"')}
    $process=Start-Process -FilePath $installerPath -ArgumentList $attemptArgs -WindowStyle Hidden -Wait -PassThru
    if($process.ExitCode -ne 0){throw "Installer returned $($process.ExitCode). Logs: $fixture"}
    if(-not(Test-Path -LiteralPath (Join-Path $application 'KairosStudio.exe'))){throw 'Installed application missing.'}
    if(Test-Path -LiteralPath (Join-Path $application 'portable.flag')){throw 'Installed builds must not inherit portable mode.'}
    $registration=Get-ItemProperty -LiteralPath $uninstallKey
    if($registration.DisplayVersion -ne $Version){throw 'Installed version mismatch.'}
    if($registration.InstallLocation.TrimEnd('\') -ne $application){throw 'Update changed the registered install directory.'}
    if(Test-Path -LiteralPath (Join-Path $fixture 'Duplicate\KairosStudio.exe')){throw 'Update created a second application copy.'}
    if(@(Get-ChildItem -LiteralPath $application -Filter 'unins*.exe').Count -ne 1){throw 'Update created multiple uninstallers.'}
}
# Unknown user files must survive uninstall; personal libraries live outside {app} as well.
$sentinel=Join-Path $application 'user-sentinel.txt'
'synthetic data - preserve on uninstall'|Set-Content -LiteralPath $sentinel -Encoding ASCII
$uninstaller=Join-Path $application 'unins000.exe'
$process=Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
if($process.ExitCode -ne 0){throw 'Uninstall failed.'}
if(Test-Path -LiteralPath $uninstallKey){throw 'Uninstall registration was not removed.'}
if(Test-Path -LiteralPath (Join-Path $application 'KairosStudio.exe')){throw 'Installed executable remains after uninstall.'}
if(-not(Test-Path -LiteralPath $sentinel)){throw 'Uninstall removed a file it did not own.'}
Write-Host "Passed silent install, same-version upgrade and uninstall with user-file preservation. Fixture: $fixture"
