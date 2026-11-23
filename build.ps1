$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'V tomto počítači nebyl nalezen kompilátor .NET Framework.' }
Push-Location $PSScriptRoot
try {
    $sources = @('PsaReader.cs','Vci.cs','VciTests.cs','Catalogue.cs','Discovery.cs','DiscoveryTests.cs','ModuleReader.cs','ModuleTests.cs','VehicleForm.cs','CabinProbe.cs','CabinProbeTests.cs','BulkReader.cs','BulkTests.cs','MappedDecoder.cs','SnapshotHtml.cs','UnitForm.cs','CommandSafety.cs','Accessories.cs','AccessoryPages.cs','AccessoryTests.cs')
    $sources += @('AccessoryRunner.cs','AccessoryRunnerTests.cs','BsiCodingReadiness.cs','BsiServices.cs','BsiServiceTests.cs','BsiConfigurationKey.cs','ServiceMaintenance.cs','AdapterIdentity.cs','DiagnosticsExport.cs','UniversalTests.cs','FaultClear.cs','FaultClearTests.cs')
    & $compilerPath /nologo /target:exe /out:PsaReader.Check.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll $sources
    if ($LASTEXITCODE -ne 0) { throw 'Sestavení kontrolní verze selhalo.' }
    & .\PsaReader.Check.exe --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Kontroly neprošly.' }
    & $compilerPath /nologo /target:winexe /out:PsaReader.Next.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll $sources
    if ($LASTEXITCODE -ne 0) { throw 'Sestavení aplikace selhalo.' }
    if (Get-Process -Name PsaReader -ErrorAction SilentlyContinue) { Write-Output 'Nová verze je PsaReader.Next.exe; původní okno zůstalo otevřené.' }
    else { Copy-Item -LiteralPath 'PsaReader.Next.exe' -Destination 'PsaReader.exe' -Force }
} finally { Pop-Location }





