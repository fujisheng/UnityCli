$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Condition {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-UnityCliJson {
    param(
        [string[]]$Arguments,
        [int]$ExpectedExitCode
    )

    $output = & $script:ExePath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $rawText = ($output | Out-String).Trim()

    if ($exitCode -ne $ExpectedExitCode) {
        throw "Unexpected exit code. Expected=$ExpectedExitCode Actual=$exitCode Command=$($Arguments -join ' ') Output=$rawText"
    }

    Assert-Condition (-not [string]::IsNullOrWhiteSpace($rawText)) "Command did not output JSON. Command=$($Arguments -join ' ')"

    try {
        $payload = $rawText | ConvertFrom-Json
    }
    catch {
        throw "Command output is not valid JSON. Command=$($Arguments -join ' ') Output=$rawText Error=$($_.Exception.Message)"
    }

    return [pscustomobject]@{
        Payload = $payload
        Raw = $rawText
        ExitCode = $exitCode
    }
}

function New-TemporaryJsonFile {
    param(
        [string]$Content
    )

    $path = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), ([System.Guid]::NewGuid().ToString('N') + '.json'))
    [System.IO.File]::WriteAllText($path, $Content, [System.Text.Encoding]::ASCII)
    return $path
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Resolve-Path (Join-Path $scriptRoot '..\..\..')).Path
$script:ExePath = Join-Path $projectRoot 'Tools/UnityCli/publish/unitycli.exe'
$endpointFile = Join-Path $projectRoot 'Library/UnityCliBridge/endpoint.json'
$tempFiles = New-Object System.Collections.Generic.List[string]

Assert-Condition (Test-Path -LiteralPath $script:ExePath) "Published CLI not found: $script:ExePath"
Assert-Condition (Test-Path -LiteralPath $endpointFile) "Unity bridge endpoint not found: $endpointFile . Open the project in Unity and wait for bridge startup before running this script."

try {
    Write-Host '[1/5] unitycli ping'
    $ping = Invoke-UnityCliJson -Arguments @(
        'ping',
        '--project', $projectRoot
    ) -ExpectedExitCode 0
    Assert-Condition ($ping.Payload.ok -eq $true) 'ping should return ok=true'
    Assert-Condition ($ping.Payload.status -eq 'completed') 'ping should return completed status'
    Assert-Condition ([int]$ping.Payload.data.port -gt 0) 'ping should return a valid port'
    Assert-Condition ([long]$ping.Payload.data.generation -gt 0) 'ping should return a valid generation'
    Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$ping.Payload.data.instanceId)) 'ping should return instanceId'

    Write-Host '[2/5] unitycli tools list'
    $toolsList = Invoke-UnityCliJson -Arguments @(
        'tools', 'list',
        '--project', $projectRoot
    ) -ExpectedExitCode 0
    $toolIds = @($toolsList.Payload | ForEach-Object { $_.id })
    Assert-Condition ($toolIds -contains '__test.echo') 'tools list missing __test.echo'
    Assert-Condition ($toolIds -contains '__test.sleep') 'tools list missing __test.sleep'
    Assert-Condition ($toolIds -contains 'editor.status') 'tools list missing editor.status'

    $echoInputPath = New-TemporaryJsonFile '{"text":"hello from e2e"}'
    $tempFiles.Add($echoInputPath)
    Write-Host '[3/5] unitycli invoke --tool __test.echo'
    $echoResult = Invoke-UnityCliJson -Arguments @(
        'invoke',
        '--tool', '__test.echo',
        '--in', $echoInputPath,
        '--project', $projectRoot
    ) -ExpectedExitCode 0
    Assert-Condition ($echoResult.Payload.ok -eq $true) 'echo should return ok=true'
    Assert-Condition ($echoResult.Payload.status -eq 'completed') 'echo should return completed status'
    Assert-Condition ($echoResult.Payload.data.echo -eq 'hello from e2e') 'echo text does not match'

    $sleepInputPath = New-TemporaryJsonFile '{"ms":100}'
    $tempFiles.Add($sleepInputPath)
    Write-Host '[4/5] unitycli invoke --tool __test.sleep --wait'
    $sleepResult = Invoke-UnityCliJson -Arguments @(
        'invoke',
        '--tool', '__test.sleep',
        '--in', $sleepInputPath,
        '--wait',
        '--timeout', '5000',
        '--project', $projectRoot
    ) -ExpectedExitCode 0
    Assert-Condition ($sleepResult.Payload.ok -eq $true) 'sleep should return ok=true'
    Assert-Condition ($sleepResult.Payload.status -eq 'completed') 'sleep should return completed status'
    Assert-Condition ([int]$sleepResult.Payload.data.slept -eq 100) 'sleep payload does not match'

    $missingInputPath = New-TemporaryJsonFile '{}'
    $tempFiles.Add($missingInputPath)
    Write-Host '[5/5] unitycli invoke --tool nonexistent'
    $missingToolResult = Invoke-UnityCliJson -Arguments @(
        'invoke',
        '--tool', 'nonexistent',
        '--in', $missingInputPath,
        '--project', $projectRoot
    ) -ExpectedExitCode 1
    Assert-Condition ($missingToolResult.Payload.ok -eq $false) 'missing tool should return ok=false'
    Assert-Condition ($missingToolResult.Payload.status -eq 'error') 'missing tool should return error status'
    Assert-Condition ($missingToolResult.Payload.error.code -eq 'tool_not_found') 'missing tool should return tool_not_found'

    Write-Host 'UnityCli E2E checks passed.'
}
finally {
    foreach ($tempFile in $tempFiles) {
        if ([string]::IsNullOrWhiteSpace($tempFile)) {
            continue
        }

        if (Test-Path -LiteralPath $tempFile) {
            Remove-Item -LiteralPath $tempFile -Force
        }
    }
}
