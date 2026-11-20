param([string] $OutputRoot = (Join-Path $PSScriptRoot 'dist'))

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$cache = Join-Path $PSScriptRoot 'obj/recognition'
$archive = Join-Path $cache 'python-3.13.17-embed-amd64.zip'
$packages = Join-Path $cache 'site-packages'
$requirements = Join-Path $PSScriptRoot 'Recognition/requirements.txt'
$requirementsStamp = Join-Path $cache 'requirements.sha256'
$output = Join-Path $OutputRoot 'ParcaBul-Windows-x64'
$engine = Join-Path $output 'engine'
New-Item -ItemType Directory -Path $cache -Force | Out-Null

if (!(Test-Path -LiteralPath $archive)) {
    $runtimeUrl = 'https://www.python.org/ftp/python/3.13.17/python-3.13.17-embed-amd64.zip'
    Invoke-WebRequest -Uri $runtimeUrl -OutFile $archive
}

$runtimeHash = '9E35319BFFEDAB8B36AD44D8F377CB4F1733BCEC257A0B6D04D94F01486DDB38'
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $runtimeHash) {
    throw "The SHA-256 checksum for $archive did not match the expected value."
}

$requirementsHash = (Get-FileHash -LiteralPath $requirements -Algorithm SHA256).Hash
if (!(Test-Path -LiteralPath $packages) -or !(Test-Path -LiteralPath $requirementsStamp) -or
    (Get-Content -LiteralPath $requirementsStamp -Raw).Trim() -ne $requirementsHash) {
    python -m pip --isolated install --index-url https://pypi.org/simple --disable-pip-version-check --no-cache-dir `
        --no-compile --only-binary=:all: --python-version 3.13 --implementation cp --platform win_amd64 --abi cp313 `
        --target $packages --upgrade -r $requirements
    if ($LASTEXITCODE -ne 0) { throw 'Could not install the recognition engine packages.' }
    Set-Content -LiteralPath $requirementsStamp -Value $requirementsHash -Encoding ascii
}

dotnet publish (Join-Path $PSScriptRoot 'ParcaBul.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:EnableRuntimePackDownload=false `
    -p:DebugType=none --source $PSScriptRoot -o $output
if ($LASTEXITCODE -ne 0) { throw 'Could not build the Windows application.' }

Expand-Archive -LiteralPath $archive -DestinationPath $engine -Force
Set-Content -LiteralPath (Join-Path $engine 'python313._pth') -Encoding ascii -Value @('python313.zip', '.', 'site-packages')
Copy-Item -LiteralPath $packages -Destination $engine -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Recognition/recognize.py') -Destination $engine -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output -Force

$zip = Join-Path (Split-Path $output -Parent) 'ParcaBul-Windows-x64.zip'
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -Force
Write-Output $zip
