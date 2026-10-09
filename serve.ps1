# Compile and start the local mirror. Open http://127.0.0.1:4173/
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$exe = Join-Path $here "serve.exe"
& $csc /nologo /optimize /out:$exe (Join-Path $here "serve.cs")
if ($LASTEXITCODE -ne 0) { throw "Could not compile serve.cs" }
& $exe @args
