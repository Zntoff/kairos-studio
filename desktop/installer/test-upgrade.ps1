# Exercise the real installer script with a unique AppId and tiny synthetic payloads.
# The user's production registration, installation and shortcuts are never touched.
param([string]$InnoCompiler)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(-not $InnoCompiler){$InnoCompiler=& (Join-Path $PSScriptRoot 'setup-inno.ps1')}
$testId='{'+[guid]::NewGuid().ToString().ToUpperInvariant()+'}'
$key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\'+$testId+'_is1'
$fixture=Join-Path $env:TEMP ('KairosUpgradeTest-'+[guid]::NewGuid().ToString('N'))
$application=Join-Path $fixture 'Application'
$duplicate=Join-Path $fixture 'Duplicate'
$output=Join-Path $fixture 'Installers'
New-Item -ItemType Directory -Path $output -Force|Out-Null
$productionKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7AAFBFBF-6B62-4DC3-B559-25CB927CFF44}_is1'
function ProductionSnapshot {
    if(-not(Test-Path -LiteralPath $productionKey)){return ''}
    $registration=Get-Item -LiteralPath $productionKey
    $values=[ordered]@{}
    foreach($name in ($registration.GetValueNames()|Sort-Object)){$values[$name]=$registration.GetValue($name)}
    return ($values|ConvertTo-Json -Depth 5 -Compress)
}
$productionBefore=ProductionSnapshot
foreach($beta in @(1,2)){
    $payload=Join-Path $fixture "Payload-$beta"
    New-Item -ItemType Directory -Path (Join-Path $payload 'tools') -Force|Out-Null
    "synthetic payload beta $beta"|Set-Content -LiteralPath (Join-Path $payload 'KairosStudio.exe') -Encoding ASCII
    Copy-Item -LiteralPath (Join-Path $root 'desktop/tools/MicrosoftEdgeWebview2Setup.exe') -Destination (Join-Path $payload 'tools')
    & $InnoCompiler /Qp "/DInstallerTestId=$testId" "/DAppVersion=0.11.0-beta.$beta" /DAppNumericVersion=0.11.0 "/DSourceDir=$payload" "/DReleaseDir=$output" (Join-Path $PSScriptRoot 'KairosStudio.iss')
    if($LASTEXITCODE){throw 'Synthetic installer compilation failed.'}
    $destination=if($beta -eq 1){$application}else{$duplicate}
    $installer=Join-Path $output "KairosStudio-0.11.0-beta.$beta-Setup-win-x64.exe"
    $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS','/LANG=english',('/DIR="'+$destination+'"'),('/LOG="'+(Join-Path $fixture "install-$beta.log")+'"'))
    $process=Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if($process.ExitCode){throw "Install beta $beta failed: $($process.ExitCode). Fixture: $fixture"}
    $registration=Get-ItemProperty -LiteralPath $key
    if($registration.DisplayVersion -ne "0.11.0-beta.$beta" -or $registration.DisplayName -ne 'Kairos Studio Installer Test'){throw 'Application identity or version was not updated.'}
    if($registration.InstallLocation.TrimEnd('\') -ne $application){throw 'Update moved the installation.'}
    if((Get-Content -LiteralPath (Join-Path $application 'KairosStudio.exe') -Raw).Trim() -ne "synthetic payload beta $beta"){throw 'Update did not replace the old binaries.'}
    if(Test-Path -LiteralPath (Join-Path $duplicate 'KairosStudio.exe')){throw 'Update created a duplicate installation.'}
    if(@(Get-ChildItem -LiteralPath $application -Filter 'unins*.exe').Count -ne 1){throw 'Update created multiple uninstallers.'}
    $sentinel=Join-Path $application 'user-sentinel.txt'
    if($beta -eq 1){'preserve user files'|Set-Content -LiteralPath $sentinel -Encoding ASCII}
    elseif((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne 'preserve user files'){throw 'Update changed a user file.'}
}
$process=Start-Process -FilePath (Join-Path $application 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
if($process.ExitCode -or (Test-Path -LiteralPath $key) -or (Test-Path -LiteralPath (Join-Path $application 'KairosStudio.exe'))){throw 'Uninstall did not remove the updated application.'}
if(-not(Test-Path -LiteralPath $sentinel)){throw 'Uninstall removed an unowned user file.'}
$productionAfter=ProductionSnapshot
if($productionBefore -ne $productionAfter){throw 'Production installation registration changed.'}
Write-Host "Passed fresh install, beta.1 to beta.2 update, conflicting /DIR, one uninstaller, file preservation and uninstall. Fixture: $fixture"
