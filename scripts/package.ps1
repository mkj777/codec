param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([+-][0-9A-Za-z.-]+)?$')]
    [string]$Version
)

$repo = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $repo 'obj\publish-avalonia'
$releases = Join-Path $repo 'obj\velopack-releases'

dotnet publish (Join-Path $repo 'Codec.Avalonia\Codec.Avalonia.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:Platform=x64 -p:PublishSingleFile=false -o $publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

vpk pack --packId Codec --packVersion $Version --packDir $publish `
    --mainExe Codec.exe --packTitle Codec `
    --icon (Join-Path $repo 'Codec.Avalonia\Assets\icon.ico') `
    --outputDir $releases
exit $LASTEXITCODE
