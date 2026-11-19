[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EnvFile,
    [ValidateRange(1,65535)][int]$PostgresPort=55433,
    [ValidateRange(1024,65535)][int]$ApiPort=5089,
    [string]$OutputPath
)
$ErrorActionPreference='Stop'
$backendRoot=Split-Path $PSScriptRoot -Parent
$projectRoot=Split-Path $backendRoot -Parent
if (-not $OutputPath) { $OutputPath=Join-Path $projectRoot 'qa/local-business-checks-current.json' }
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
if (-not $OutputPath.StartsWith($projectRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetExtension($OutputPath) -ne '.json') {
    throw 'OutputPath must be a JSON report inside this project.'
}
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Local business checks require PowerShell 7.' }
if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) { throw 'The local environment file was not found.' }
$apiDll=Join-Path $backendRoot 'src/VortexBird.API/bin/Release/net10.0/VortexBird.API.dll'
if (-not (Test-Path -LiteralPath $apiDll)) { throw 'Build or publish the backend in Release before running business checks.' }
$psql=@(
    (Join-Path $projectRoot '.work/closure-pg-native/bin/psql.exe'),
    (Join-Path $projectRoot '.work/closure-pg-edb/pgsql/bin/psql.exe')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $psql) { throw 'A PostgreSQL native psql client is required.' }
$settings=@{}
foreach ($line in Get-Content -LiteralPath $EnvFile) {
    if ($line -match '^\s*([A-Z][A-Z0-9_]*)\s*=(.*)$') {
        $key=$Matches[1]; $value=$Matches[2].Trim()
        if ($value.Length -ge 2 -and (($value[0] -eq '"' -and $value[-1] -eq '"') -or ($value[0] -eq "'" -and $value[-1] -eq "'"))) {
            $value=$value.Substring(1,$value.Length-2)
        }
        $settings[$key]=$value
    }
}
if ([string]::IsNullOrWhiteSpace($settings['POSTGRES_PASSWORD'])) { throw 'POSTGRES_PASSWORD is missing from the local environment file.' }
$postgresUser=if ($settings['POSTGRES_USER']) { $settings['POSTGRES_USER'] } else { 'vortexbird' }
$databaseName='vortexbird_acceptance_'+[DateTime]::UtcNow.ToString('yyyyMMddHHmmss')+'_'+[guid]::NewGuid().ToString('N').Substring(0,8)
$environmentNames=@(
    'PGPASSWORD','PGSSLMODE','PGCONNECT_TIMEOUT','PGSERVICE','PGOPTIONS','ConnectionStrings__Database','ConnectionStrings__Redis',
    'ASPNETCORE_ENVIRONMENT','Database__Initialize','Seed__Enabled','Workers__Enabled','Payments__Provider','Messaging__Provider',
    'Messaging__Region','Messaging__ServiceUrl','Messaging__QueueUrl',
    'Demo__Email','Seed__AdminSubject','Authentication__DemoEnabled','Authentication__DemoSigningKey','Demo__Password','ASPNETCORE_URLS'
)
$savedEnvironment=@{}
foreach ($name in $environmentNames) { $savedEnvironment[$name]=[Environment]::GetEnvironmentVariable($name,'Process') }
$started=[DateTimeOffset]::UtcNow
$report=[ordered]@{
    checkedAt=$started.ToString('o'); status='failed'; transport='local API, new dedicated PostgreSQL database and dedicated loopback ElasticMQ queues';
    database=$databaseName; databaseCreated=$false; databaseRetained=$false; apiPort=$ApiPort; postgresPort=$PostgresPort;
    provider='Simulator'; messaging='ElasticMQ'; stage='queue transport'; checks=@();
    apiLogs=".work/native-runtime/business-runs/$databaseName";
    commands=@('SqsTransportChecks --emulator-only against loopback','psql: CREATE DATABASE with generated synthetic name','tests/Integration.ps1 -Configuration Release using isolated queue','psql: read persisted reservations, events and transitions');
    limitations=@('Synthetic local data only; does not verify AWS or external payment providers.')
}
$failed=$false
try {
    $env:PGPASSWORD=$settings['POSTGRES_PASSWORD']
    $env:PGSSLMODE='disable'
    $env:PGCONNECT_TIMEOUT='5'
    $env:PGSERVICE=''
    $env:PGOPTIONS=''
    $queueReportPath=Join-Path (Split-Path $OutputPath -Parent) "$databaseName-transport.json"
    $report['transportReport']=[IO.Path]::GetRelativePath($projectRoot,$queueReportPath)
    $sqsProject=Join-Path $backendRoot 'tests/SqsTransportChecks/SqsTransportChecks.csproj'
    $sqsDll=Join-Path (Split-Path $sqsProject -Parent) 'bin/Release/net10.0/SqsTransportChecks.dll'
    & dotnet build $sqsProject -c Release --no-restore --disable-build-servers -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Building the ElasticMQ test client failed.' }
    & dotnet $sqsDll --emulator-only '--elasticmq=http://127.0.0.1:9324' "--report=$queueReportPath"
    if ($LASTEXITCODE -ne 0) { throw 'The local ElasticMQ transport checks failed.' }
    $queues=Get-Content -LiteralPath $queueReportPath -Raw | ConvertFrom-Json
    if (-not $queues.emulatorAttempted -or $queues.awsContacted -or $queues.failed -ne 0 -or $queues.passed -ne 3 -or
        $queues.queueUrl -cnotmatch '^http://127\.0\.0\.1:9324/000000000000/vortexbird-events-qa-([a-f0-9]{32})\z' -or
        $queues.deadLetterQueueUrl -cne ('http://127.0.0.1:9324/000000000000/vortexbird-dlq-qa-' + $Matches[1])) {
        throw 'The emulator did not verify the expected isolated queues and transport checks.'
    }
    $report['queueUrl']=$queues.queueUrl
    $report['deadLetterQueueUrl']=$queues.deadLetterQueueUrl
    $report.checks+=@('ElasticMQ visibility, redelivery and acknowledgement','ElasticMQ DLQ and controlled replay')
    $report.stage='database creation'
    # Name is generated internally from an ASCII prefix, UTC timestamp and GUID. No existing database is reused.
    $creationOutput=& $psql -X -h 127.0.0.1 -p $PostgresPort -U $postgresUser -d postgres -w -v ON_ERROR_STOP=1 -c "CREATE DATABASE $databaseName;" 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Dedicated test database creation failed; verify the native local PostgreSQL service and CREATE DATABASE permission.' }
    $report.databaseCreated=$true; $report.databaseRetained=$true
    $connection=[System.Data.Common.DbConnectionStringBuilder]::new()
    $connection['Host']='127.0.0.1'; $connection['Port']=$PostgresPort; $connection['Database']=$databaseName
    $connection['Username']=$postgresUser; $connection['Password']=$settings['POSTGRES_PASSWORD']
    $connection['SSL Mode']='Disable'; $connection['Timeout']='5'
    $env:ConnectionStrings__Database=$connection.ConnectionString
    $report.stage='order, payment and durable messaging'
    $integrationOutput=& (Join-Path $backendRoot 'tests/Integration.ps1') -Configuration Release -Port $ApiPort -ElasticMqQueueUrl $queues.queueUrl -PsqlPath $psql
    $observations=@($integrationOutput | Where-Object { $_ -is [pscustomobject] -and $_.PSObject.Properties['persistence'] })
    if ($observations.Count -ne 1) { throw 'Missing database persistence evidence.' }
    $report['orders']=$observations[0]
    $report.status='passed'
    $report.stage='complete'
    $report.checks+=@('Concurrent order idempotency','Changed payload returns conflict','SQL confirms one released and one committed reservation','Eight approvals produce one settlement','SQL confirms one confirmed transition','Five distinct outbox events completed through ElasticMQ and recorded in the PostgreSQL inbox','Both orders and their terminal histories survive restart of the isolated API and workers','Dashboard matches persisted orders')
    $report['evidence']=@($integrationOutput | Where-Object { $_ -is [string] -and $_.StartsWith('PASS PostgreSQL API ',[StringComparison]::Ordinal) })
} catch {
    $failed=$true
    $report['errorType']=$_.Exception.GetType().Name
    $report['errorSource']=[IO.Path]::GetFileName($_.InvocationInfo.ScriptName)
    $report['errorLine']=$_.InvocationInfo.ScriptLineNumber
    if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response -is [Net.Http.HttpResponseMessage]) {
        $report['httpStatus']=[int]$_.Exception.Response.StatusCode
    }
    $report['detail']='Local business checks failed. The dedicated test database is retained if it was created; platform data was not selected for testing.'
} finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name],'Process') }
    $settings.Clear()
    $creationOutput=$null
    $connection=$null
    $report['durationSeconds']=[math]::Round(([DateTimeOffset]::UtcNow-$started).TotalSeconds,2)
    New-Item -ItemType Directory -Path (Split-Path $OutputPath -Parent) -Force | Out-Null
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8
}
if ($failed) { throw "Local business checks failed; inspect $OutputPath for the stage and run-specific log paths." }
Write-Output "Local business checks passed. Dedicated database retained: $databaseName. Report: $OutputPath"
