param([string]$Configuration='Debug', [int]$Port=5089, [string]$ElasticMqQueueUrl, [string]$PsqlPath)
$ErrorActionPreference='Stop'
if (-not $env:ConnectionStrings__Database) { throw 'ConnectionStrings__Database is required; use a dedicated disposable test database.' }
$testConnection=[System.Data.Common.DbConnectionStringBuilder]::new()
$testConnection.set_ConnectionString($env:ConnectionStrings__Database)
$testHost=[string]$testConnection['Host']
$testDatabase=[string]$testConnection['Database']
if ($testHost -notin @('localhost','127.0.0.1','::1') -or $testDatabase -notmatch '^(vortexbird_ci|vortexbird_(test|acceptance)_[a-zA-Z0-9_]+)$') {
  throw 'Integration checks require literal localhost and a dedicated vortexbird_ci, vortexbird_test_* or vortexbird_acceptance_* database.'
}
$taskRoot = Split-Path $PSScriptRoot -Parent
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:Database__Initialize='true'
$env:Seed__Enabled='true'
$env:Workers__Enabled='true'
$env:Payments__Provider='Simulator'
$env:Messaging__Provider='local'
if ($ElasticMqQueueUrl) {
  if ($ElasticMqQueueUrl -cnotmatch '^http://127\.0\.0\.1:9324/000000000000/vortexbird-events-qa-[a-f0-9]{32}\z' -or
      $testDatabase -cnotmatch '^vortexbird_acceptance_[0-9]{14}_[a-f0-9]{8}\z' -or
      -not $PsqlPath -or -not (Test-Path -LiteralPath $PsqlPath -PathType Leaf)) {
    throw 'Connected business checks require the run-specific ElasticMQ queue, a new acceptance database and psql.'
  }
  $env:Messaging__Provider='sqs'
  $env:Messaging__Region='us-east-2'
  $env:Messaging__ServiceUrl='http://127.0.0.1:9324'
  $env:Messaging__QueueUrl=$ElasticMqQueueUrl
}
$env:ConnectionStrings__Redis=''
$env:Demo__Email='admin@andesretail.local'
$env:Seed__AdminSubject='demo-admin'
$env:Authentication__DemoEnabled='true'
$env:Authentication__DemoSigningKey=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:Demo__Password=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
$env:ASPNETCORE_URLS="http://localhost:$Port"
$apiDll=Join-Path $taskRoot "src/VortexBird.API/bin/$Configuration/net10.0/VortexBird.API.dll"
$logDirectory=$PSScriptRoot
if ($ElasticMqQueueUrl) {
  $logDirectory=Join-Path (Split-Path $taskRoot -Parent) ".work/native-runtime/business-runs/$testDatabase"
  $null=New-Item -ItemType Directory -Path $logDirectory -Force
}
$launch=@{FilePath='dotnet';ArgumentList=@('"' + $apiDll + '"');WorkingDirectory=(Split-Path $apiDll -Parent);PassThru=$true;RedirectStandardOutput=(Join-Path $logDirectory 'integration.stdout.log');RedirectStandardError=(Join-Path $logDirectory 'integration.stderr.log')}
if ($IsWindows) { $launch.WindowStyle='Hidden' }
function Wait-TestApi([Diagnostics.Process]$Process, [string]$BaseUrl) {
  $deadline=[datetime]::UtcNow.AddSeconds(90)
  do {
    $Process.Refresh()
    if ($Process.HasExited) { throw 'The dedicated test API exited before readiness; check port availability and test logs.' }
    try {
      $health=Invoke-RestMethod "$BaseUrl/health/ready" -TimeoutSec 3
      if ($health.status -eq 'ready') { return }
    } catch { }
    Start-Sleep -Milliseconds 500
  } while ([datetime]::UtcNow -lt $deadline)
  throw 'The dedicated test API did not become ready; check test logs.'
}
$server=Start-Process @launch
try {
  $base="http://localhost:$Port"
  Wait-TestApi $server $base
  $login=Invoke-RestMethod "$base/api/v1/auth/demo-login" -Method Post -ContentType application/json -Body (@{email='admin@andesretail.local';password=$env:Demo__Password}|ConvertTo-Json)
  $headers=@{Authorization="Bearer $($login.accessToken)";'Idempotency-Key'=[guid]::NewGuid().ToString()}
  $request=@{partnerId='11111111-1111-1111-1111-111111111111';channelId='22222222-2222-2222-2222-222222222222';storeId='BOG-01';customerId='CUST-001';items=@(@{sku='AND-0001';quantity=1});paymentOutcome='rejected'}
  $body=$request|ConvertTo-Json -Depth 5
  $invalidPublicRequest=Invoke-WebRequest "$base/api/v1/orders" -Method Post -Headers $headers -ContentType application/json -Body $body -SkipHttpErrorCheck
  if ($invalidPublicRequest.StatusCode -ne 400) { throw 'The public order endpoint accepted a simulation field.' }
  $receipts = @(1..4 | ForEach-Object -Parallel {
    Invoke-RestMethod "$using:base/api/v1/simulations/orders" -Method Post -Headers $using:headers -ContentType application/json -Body $using:body -ErrorAction Stop
  } -ThrottleLimit 4)
  $created = @($receipts | Where-Object { -not $_.replayed })
  if ($receipts.Count -ne 4 -or $created.Count -ne 1 -or @($receipts.id | Sort-Object -Unique).Count -ne 1) {
    throw 'Concurrent attempts did not produce exactly one order and three replays.'
  }
  $first = $created[0]
  $request.customerId='DIFFERENT'
  $conflict=Invoke-WebRequest "$base/api/v1/simulations/orders" -Method Post -Headers $headers -ContentType application/json -Body ($request|ConvertTo-Json -Depth 5) -SkipHttpErrorCheck
  if ($conflict.StatusCode -ne 409) { throw 'Changed payload did not return409.' }
  $order=$null
  for($i=0;$i -lt 30;$i++) { $order=Invoke-RestMethod "$base/api/v1/orders/$($first.id)" -Headers $headers; if($order.status -eq 'cancelled'){break}; Start-Sleep -Milliseconds 500 }
  if ($order.status -ne 'cancelled' -or $order.paymentStatus -ne 'rejected') { throw 'Rejected payment did not compensate order.' }
  $history = @($order.timeline | Sort-Object createdAt | ForEach-Object status)
  if (($history -join ',') -ne 'reserved,awaiting_payment,compensating,cancelled') {
    throw 'The worker did not persist the expected order history.'
  }
  # Competing resolutions must reread the order after acquiring the advisory lock.
  $headers['Idempotency-Key']=[guid]::NewGuid().ToString()
  $request.customerId='CUST-001'
  $request.paymentOutcome='pending'
  $pending=Invoke-RestMethod "$base/api/v1/simulations/orders" -Method Post -Headers $headers -ContentType application/json -Body ($request|ConvertTo-Json -Depth 5)
  for($i=0;$i -lt 30;$i++) {
    $pendingOrder=Invoke-RestMethod "$base/api/v1/orders/$($pending.id)" -Headers $headers
    if($pendingOrder.status -eq 'awaiting_payment'){break}
    Start-Sleep -Milliseconds 500
  }
  if ($pendingOrder.status -ne 'awaiting_payment') { throw 'The simulator did not create a pending payment.' }
  $payments=Invoke-RestMethod "$base/api/v1/payments?status=pending&pageSize=100" -Headers $headers
  $payment=@($payments.items | Where-Object orderId -EQ $pending.id)
  if ($payment.Count -ne 1) { throw 'Expected exactly one pending payment for the order.' }
  $paymentId=$payment[0].id
  $resolutions=@(1..8 | ForEach-Object -Parallel {
    $response=Invoke-WebRequest "$using:base/api/v1/payments/$using:paymentId/simulate" -Method Post -Headers $using:headers -ContentType application/json -Body '{"outcome":"approved"}' -SkipHttpErrorCheck
    [int]$response.StatusCode
  } -ThrottleLimit 8)
  if (@($resolutions | Where-Object { $_ -eq 200 }).Count -ne 1 -or @($resolutions | Where-Object { $_ -eq 409 }).Count -ne 7) {
    throw 'Concurrent payment resolutions did not produce exactly one settlement and seven conflicts.'
  }
  $settled=Invoke-RestMethod "$base/api/v1/orders/$($pending.id)" -Headers $headers
  if ($settled.status -ne 'confirmed' -or $settled.paymentStatus -ne 'approved' -or @($settled.timeline | Where-Object status -EQ 'confirmed').Count -ne 1) {
    throw 'Concurrent settlement duplicated a transition or changed the final payment state.'
  }
  $dashboard=Invoke-RestMethod "$base/api/v1/dashboard?days=30" -Headers $headers
  $dailyCount=($dashboard.ordersByDay | Measure-Object -Property count -Sum).Sum
  if ($dashboard.metrics.totalOrders -lt 1 -or $dailyCount -ne $dashboard.metrics.totalOrders) {
    throw 'The dashboard did not aggregate its orders into UTC days.'
  }
  if ($ElasticMqQueueUrl) {
    $persistenceQuery=Join-Path $PSScriptRoot 'BusinessPersistence.sql'
    $deadline=[datetime]::UtcNow.AddSeconds(45)
    do {
      $snapshot=& $PsqlPath -X -w -qAt -h $testHost -p $testConnection['Port'] -U $testConnection['Username'] -d $testDatabase -v ON_ERROR_STOP=1 -v "rejected=$([guid]$first.id)" -v "approved=$([guid]$pending.id)" -f $persistenceQuery
      if ($LASTEXITCODE -ne 0) { throw 'Could not read the isolated PostgreSQL persistence evidence.' }
      $persistence=$snapshot | ConvertFrom-Json -ErrorAction Stop
      if ($persistence.durableEvents -eq 5) { break }
      Start-Sleep -Milliseconds 500
    } while ([datetime]::UtcNow -lt $deadline)
    if ($persistence.durableEvents -ne 5 -or -not $persistence.released -or -not $persistence.committed -or $persistence.confirmedTransitions -ne 1) {
      throw 'PostgreSQL did not confirm five distinct processed events, released and committed reservations, and one confirmed transition.'
    }
    Stop-Process -InputObject $server -ErrorAction Stop
    if (-not $server.WaitForExit(10000)) { throw 'The isolated API did not stop for the restart check.' }
    $launch.RedirectStandardOutput=Join-Path $logDirectory 'integration-restart.stdout.log'
    $launch.RedirectStandardError=Join-Path $logDirectory 'integration-restart.stderr.log'
    $server=Start-Process @launch
    Wait-TestApi $server $base
    $recoveredApproved=Invoke-RestMethod "$base/api/v1/orders/$($pending.id)" -Headers $headers -TimeoutSec 5
    $recoveredRejected=Invoke-RestMethod "$base/api/v1/orders/$($first.id)" -Headers $headers -TimeoutSec 5
    if ($recoveredApproved.status -ne 'confirmed' -or $recoveredRejected.status -ne 'cancelled' -or
        @($recoveredApproved.timeline | Where-Object status -EQ 'confirmed').Count -ne 1 -or
        @($recoveredRejected.timeline | Where-Object status -EQ 'cancelled').Count -ne 1) {
      throw 'The restarted isolated API did not recover the expected orders and histories.'
    }
    [pscustomobject]@{rejectedOrder=$first.id; approvedOrder=$pending.id; persistence=$persistence; recoveredAfterApiRestart=$true}
  }
  Write-Output 'PASS PostgreSQL API concurrent idempotency, payload conflict, persisted saga, compensation and single payment settlement.'
} finally { if (-not $server.HasExited) { Stop-Process -Id $server.Id } }
