[CmdletBinding()]
param(
    [ValidateSet('All','Desktop','Docker')][string]$Target='All',
    [string]$ExpectedVersion,
    [string]$OutputDirectory,
    [string]$InnoCompiler,
    [string]$CertificateThumbprint,
    [string]$SigningMetadata,
    [string]$SigningDlib,
    [string]$SignTool,
    [switch]$RequireSignature
)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$python=Get-Command python -ErrorAction Stop
$version=([xml](Get-Content -LiteralPath (Join-Path $root 'desktop/ClipperStudio/ClipperStudio.csproj') -Raw)).Project.PropertyGroup.Version
if($version -notmatch '^\d+\.\d+\.\d+(-beta\.[1-9]\d*)?$'){throw 'Use a stable or beta semantic version.'}
$channel=if($version -like '*-beta.*'){'beta'}else{'stable'}
$numericVersion=$version.Split('-')[0]
$pythonVersion=[regex]::Match((Get-Content -LiteralPath (Join-Path $root 'pyproject.toml') -Raw),'(?m)^version = "([^"]+)"').Groups[1].Value
$packageVersion=[regex]::Match((Get-Content -LiteralPath (Join-Path $root 'clipper/__init__.py') -Raw),'__version__ = "([^"]+)"').Groups[1].Value
if($version -ne $pythonVersion -or $version -ne $packageVersion -or ($ExpectedVersion -and $version -ne $ExpectedVersion.TrimStart('v'))){throw 'Desktop, Python and release tag versions must match.'}
if($RequireSignature -and -not($CertificateThumbprint -or $SigningMetadata)){throw 'A verified signing identity is required for a signed public release.'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root "releases/$version"}
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output|Out-Null
if(Test-Path -LiteralPath (Join-Path $output 'release-manifest.json')){throw 'Completed release already exists. Use a new output directory to preserve published artifacts.'}
$artifacts=@()
$signed=$false
Push-Location -LiteralPath $root
try {
    if($Target -in @('All','Docker')){
        # Docker progress uses stderr even on success; Windows PowerShell 5 must check exit code.
        try {$ErrorActionPreference='Continue'; & docker build -t "kairos-studio:$version" -t "clipper-studio:$version" .; $dockerExit=$LASTEXITCODE}
        finally {$ErrorActionPreference='Stop'}
        if($dockerExit){throw 'Docker build failed.'}
        & $python.Source (Join-Path $root 'scripts/package-docker.py') --version $version --output $output
        if($LASTEXITCODE){throw 'Docker packaging failed.'}
        $artifacts+=Get-Item -LiteralPath (Join-Path $output "KairosStudio-$version-Docker.zip")
    }
    if($Target -in @('All','Desktop')){
        if(-not $InnoCompiler){$InnoCompiler=& (Join-Path $root 'desktop/installer/setup-inno.ps1')}
        if(-not(Test-Path -LiteralPath $InnoCompiler)){throw 'Inno Setup compiler not found.'}
        $published=@(& (Join-Path $root 'desktop/publish.ps1') -CertificateThumbprint $CertificateThumbprint -SigningMetadata $SigningMetadata -SigningDlib $SigningDlib -SignTool $SignTool -RequireSignature:$RequireSignature)
        $application=[string]$published[-1]
        if(-not(Test-Path -LiteralPath (Join-Path $application 'KairosStudio.exe'))){throw 'Application publish did not return a package directory.'}
        $report=Get-Content -LiteralPath (Join-Path $application 'signing-report.json') -Raw|ConvertFrom-Json
        $signed=[bool]$report.publicReleaseReady
        $arguments=@("/DAppVersion=$version","/DAppNumericVersion=$numericVersion","/DSourceDir=$application","/DReleaseDir=$output")
        $previous=@{}
        try {
            if($signed){
                $values=@{KAIROS_SIGN_CERTIFICATE=$CertificateThumbprint;KAIROS_SIGN_METADATA=$SigningMetadata;KAIROS_SIGN_DLIB=$SigningDlib;KAIROS_SIGN_TOOL=$SignTool}
                foreach($key in $values.Keys){$previous[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$values[$key])}
                $wrapper=Join-Path $root 'desktop/installer/sign-file.ps1'
                $arguments+=@('/DSignedBuild',('/Skairos=powershell.exe -NoProfile -ExecutionPolicy Bypass -File $q'+$wrapper+'$q -File $f'))
            }
            & $InnoCompiler @arguments (Join-Path $root 'desktop/installer/KairosStudio.iss')
            if($LASTEXITCODE){throw 'Installer compilation failed.'}
        }finally{foreach($key in $previous.Keys){[Environment]::SetEnvironmentVariable($key,$previous[$key])}}
        $installer=Get-Item -LiteralPath (Join-Path $output "KairosStudio-$version-Setup-win-x64.exe")
        if($signed){
            $signature=Get-AuthenticodeSignature -LiteralPath $installer.FullName
            if($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate){throw 'Installer signature verification failed.'}
        }
        $artifacts+=$installer
        # A separate temporary copy keeps the portable marker out of the installed build.
        $scratch=Join-Path $env:TEMP ('KairosPortable-'+[guid]::NewGuid().ToString('N'))
        $portable=Join-Path $scratch "KairosStudio-$version-Portable"
        New-Item -ItemType Directory -Path $portable -Force|Out-Null
        Get-ChildItem -LiteralPath $application|Copy-Item -Destination $portable -Recurse
        'Kairos Studio portable mode. Keep this file beside KairosStudio.exe.'|Set-Content -LiteralPath (Join-Path $portable 'portable.flag') -Encoding ASCII
        $hashes=Get-ChildItem -LiteralPath $portable -File -Recurse -Force -Name |
            Where-Object {$_ -ne 'SHA256SUMS.txt'} | Sort-Object | ForEach-Object {
                (Get-FileHash -LiteralPath (Join-Path $portable $_) -Algorithm SHA256).Hash.ToLower()+'  '+$_.Replace('\','/')
            }
        $hashes|Set-Content -LiteralPath (Join-Path $portable 'SHA256SUMS.txt') -Encoding ASCII
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip=Join-Path $output "KairosStudio-$version-Portable-win-x64.zip"
        if(Test-Path -LiteralPath $zip){Remove-Item -LiteralPath $zip}
        [IO.Compression.ZipFile]::CreateFromDirectory($portable,$zip,[IO.Compression.CompressionLevel]::Optimal,$true)
        $artifacts+=Get-Item -LiteralPath $zip
        $temporaryRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
        $resolvedScratch=[IO.Path]::GetFullPath($scratch)
        if(-not $resolvedScratch.StartsWith($temporaryRoot,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedScratch) -notmatch '^KairosPortable-[a-f0-9]{32}$'){throw 'Unsafe portable staging cleanup path.'}
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
    $files=@($artifacts|ForEach-Object {[ordered]@{name=$_.Name;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLower()}})
    [ordered]@{version=$version;channel=$channel;target=$Target;dockerImage=$(if($Target -in @('All','Docker')){"kairos-studio:$version"}else{$null});windowsSigned=$signed;publicReleaseReady=($signed -and $Target -eq 'All' -and $channel -eq 'stable');files=$files}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'release-manifest.json') -Encoding UTF8
    $files|ForEach-Object {$_.sha256+'  '+$_.name}|Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ASCII
    & $python.Source (Join-Path $root 'scripts/verify-release.py') $output
    if($LASTEXITCODE){Remove-Item -LiteralPath (Join-Path $output 'release-manifest.json');throw 'Release archive verification failed.'}
    Write-Host "Release $version ready in $output"
    if($Target -ne 'Docker' -and -not $signed){Write-Warning 'Windows artifacts are unsigned local builds. Public signing still requires your verified publisher identity.'}
}finally{Pop-Location}
