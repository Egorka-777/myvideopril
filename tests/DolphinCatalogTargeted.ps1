param([string]$Assembly = "$PSScriptRoot\..\bin\Release\net48\VideoBatch.exe")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$assemblyPath = (Resolve-Path $Assembly).Path
$folder = Join-Path ([IO.Path]::GetTempPath()) ('VideoBatchDolphinTests-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($folder) | Out-Null
try {
    $harness = @'
using System;
using System.Reflection;
public static class DolphinCatalogTargeted {
    public static int Main() {
        AppDomain.CurrentDomain.AssemblyResolve += (sender,args) => Assembly.LoadFrom(Environment.GetEnvironmentVariable("VIDEOBATCH_TEST_ASSEMBLY"));
        try { Run(); return 0; } catch(Exception e) { Console.WriteLine(e); return 1; }
    }
    static void Run() { VideoBatch.DolphinProfileCatalogTests.RunSelfTests(); Console.WriteLine("PASS: numeric Dolphin folders, explicit metadata-only markets, assigned proxy IDs, credentials retained, safe labels and distinct shared-host routes."); }
}
'@
    $harnessPath = Join-Path $folder 'Harness.cs'
    [IO.File]::WriteAllText($harnessPath,$harness)
    $exe = Join-Path $folder 'DolphinCatalogTargeted.exe'
    $sdk = (& dotnet --version).Trim()
    $compiler = Join-Path $env:ProgramFiles ('dotnet\sdk\'+$sdk+'\Roslyn\bincore\csc.dll')
    $framework = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319'
    & dotnet $compiler /nologo /warn:0 /target:exe /langversion:latest "/out:$exe" "/reference:$framework\mscorlib.dll" "/reference:$framework\System.dll" "/reference:$framework\System.Core.dll" "/reference:$framework\System.Security.dll" "/reference:$assemblyPath" "/reference:$framework\System.Net.Http.dll" "/reference:$framework\System.Web.Extensions.dll" "/reference:$framework\System.Web.dll" (Join-Path $root 'src\DolphinProfileCatalog.cs') (Join-Path $root 'src\TelegramProxyCatalog.cs') (Join-Path $root 'src\DolphinProfileCatalogTests.cs') (Join-Path $root 'src\TelegramStore.cs') (Join-Path $root 'src\TelegramHealth.cs') $harnessPath
    if ($LASTEXITCODE -ne 0) { throw 'Targeted catalog compilation failed' }
    $env:VIDEOBATCH_TEST_ASSEMBLY = $assemblyPath
    & $exe
    if ($LASTEXITCODE -ne 0) { throw 'Targeted catalog tests failed' }
} finally { Remove-Item Env:\VIDEOBATCH_TEST_ASSEMBLY -ErrorAction SilentlyContinue; if ([IO.Directory]::Exists($folder)) { [IO.Directory]::Delete($folder,$true) } }
