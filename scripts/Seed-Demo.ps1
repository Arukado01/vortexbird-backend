param([string]$BaseUrl='http://localhost:5080',[int]$Count=18)
$ErrorActionPreference='Stop'
if (-not $env:Demo__Password) { throw 'Define Demo__Password con la contraseña del entorno Development.' }
$email=if($env:Demo__Email){$env:Demo__Email}else{'admin@andesretail.local'}
$login=Invoke-RestMethod "$BaseUrl/api/v1/auth/demo-login" -Method Post -ContentType application/json -TimeoutSec 30 -Body (@{email=$email;password=$env:Demo__Password}|ConvertTo-Json)
for($index=1;$index -le [Math]::Min(60,[Math]::Max(1,$Count));$index++) {
  $headers=@{Authorization="Bearer $($login.accessToken)";'Idempotency-Key'=[guid]::NewGuid().ToString()}
  $outcome=@('approved','approved','rejected','pending')[$index%4]
  $request=@{partnerId='11111111-1111-1111-1111-111111111111';channelId='22222222-2222-2222-2222-222222222222';storeId='BOG-01';customerId=('CUST-{0:000}' -f (($index%30)+1));items=@(@{sku=('AND-{0:0000}' -f (($index%48)+1));quantity=1});paymentOutcome=$outcome}
  $receipt=Invoke-RestMethod "$BaseUrl/api/v1/orders" -Method Post -Headers $headers -ContentType application/json -TimeoutSec 30 -Body ($request|ConvertTo-Json -Depth 5)
  Write-Output ("Created order {0}, requested simulated payment {1}" -f $receipt.id,$outcome)
}
Write-Output 'Las órdenes fueron creadas por la API; el worker procesará los eventos y actualizará las métricas.'
