param([string]$Configuration='Debug', [int]$Port=5089)
$ErrorActionPreference='Stop'
if (-not $env:ConnectionStrings__Database) { throw 'ConnectionStrings__Database is required; use a dedicated disposable test database.' }
$taskRoot = Split-Path $PSScriptRoot -Parent
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:Database__Initialize='true'
$env:Seed__Enabled='true'
$env:Workers__Enabled='true'
$env:Authentication__DemoEnabled='true'
$env:Authentication__DemoSigningKey=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:Demo__Password=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
$env:ASPNETCORE_URLS="http://localhost:$Port"
$apiDll=Join-Path $taskRoot "src/VortexBird.API/bin/$Configuration/net10.0/VortexBird.API.dll"
$launch=@{FilePath='dotnet';ArgumentList=@($apiDll);PassThru=$true;RedirectStandardOutput=(Join-Path $PSScriptRoot 'integration.stdout.log');RedirectStandardError=(Join-Path $PSScriptRoot 'integration.stderr.log')}
if ($IsWindows) { $launch.WindowStyle='Hidden' }
$server=Start-Process @launch
try {
  $base="http://localhost:$Port"
  $ready=$false
  for($i=0;$i -lt 30;$i++) { try { $null=Invoke-RestMethod "$base/health/ready"; $ready=$true; break } catch { Start-Sleep -Milliseconds 500 } }
  if (-not $ready) { throw 'API not ready; check test logs.' }
  $login=Invoke-RestMethod "$base/api/v1/auth/demo-login" -Method Post -ContentType application/json -Body (@{email='admin@andesretail.local';password=$env:Demo__Password}|ConvertTo-Json)
  $headers=@{Authorization="Bearer $($login.accessToken)";'Idempotency-Key'=[guid]::NewGuid().ToString()}
  $request=@{partnerId='11111111-1111-1111-1111-111111111111';channelId='22222222-2222-2222-2222-222222222222';storeId='BOG-01';customerId='CUST-001';items=@(@{sku='AND-0001';quantity=1});paymentOutcome='rejected'}
  $body=$request|ConvertTo-Json -Depth 5
  $first=Invoke-RestMethod "$base/api/v1/orders" -Method Post -Headers $headers -ContentType application/json -Body $body
  $second=Invoke-RestMethod "$base/api/v1/orders" -Method Post -Headers $headers -ContentType application/json -Body $body
  if ($first.id -ne $second.id -or -not $second.replayed) { throw 'Idempotent replay created different order.' }
  $request.customerId='DIFFERENT'
  $conflict=Invoke-WebRequest "$base/api/v1/orders" -Method Post -Headers $headers -ContentType application/json -Body ($request|ConvertTo-Json -Depth 5) -SkipHttpErrorCheck
  if ($conflict.StatusCode -ne 409) { throw 'Changed payload did not return409.' }
  $order=$null
  for($i=0;$i -lt 30;$i++) { $order=Invoke-RestMethod "$base/api/v1/orders/$($first.id)" -Headers $headers; if($order.status -eq 'cancelled'){break}; Start-Sleep -Milliseconds 500 }
  if ($order.status -ne 'cancelled' -or $order.paymentStatus -ne 'rejected') { throw 'Rejected payment did not compensate order.' }
  Write-Output 'PASS PostgreSQL API idempotency, payload conflict, persisted saga and compensation.'
} finally { if (-not $server.HasExited) { Stop-Process -Id $server.Id } }
