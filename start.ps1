[CmdletBinding()]
param(
    [string]$TunnelProfile = "gw2-account",
    [ValidateRange(1, 65535)]
    [int]$McpPort = 5288,
    [ValidateRange(1, 65535)]
    [int]$TunnelHealthPort = 8080,
    [ValidateRange(1, 300)]
    [int]$ReadyTimeoutSeconds = 30,
    [switch]$NoBrowser
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$serverProcess = $null
$tunnelProcess = $null
$projectPath = Join-Path $PSScriptRoot "src\GW2AccountMCP\GW2AccountMCP.csproj"
$McpBaseUrl = [uri]"http://127.0.0.1:$McpPort/"
$TunnelHealthBaseUrl = [uri]"http://127.0.0.1:$TunnelHealthPort/"
$mcpEndpoint = [uri]::new($McpBaseUrl, "mcp")
$tunnelReadyEndpoint = [uri]::new($TunnelHealthBaseUrl, "readyz")
$tunnelUiEndpoint = [uri]::new($TunnelHealthBaseUrl, "ui")

if ($McpPort -eq $TunnelHealthPort) {
    throw "McpPort and TunnelHealthPort must use different ports."
}

function Assert-CommandAvailable {
    param([Parameter(Mandatory)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found on PATH."
    }
}

function Test-TcpPortInUse {
    param([Parameter(Mandatory)][int]$Port)

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $connectTask = $client.ConnectAsync([System.Net.IPAddress]::Loopback, $Port)
        return $connectTask.Wait([TimeSpan]::FromSeconds(1)) -and $client.Connected
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Start-OwnedProcess {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$ArgumentList
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $PSScriptRoot
    $startInfo.UseShellExecute = $false
    foreach ($argument in $ArgumentList) {
        $startInfo.ArgumentList.Add($argument)
    }

    return [System.Diagnostics.Process]::Start($startInfo)
}

function Wait-ForHttpStatus {
    param(
        [Parameter(Mandatory)][uri]$Uri,
        [Parameter(Mandatory)][int]$ExpectedStatus,
        [Parameter(Mandatory)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory)][string]$ProcessName
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "$ProcessName exited before becoming ready (exit code $($Process.ExitCode))."
        }

        try {
            $response = Invoke-WebRequest -Uri $Uri -Method Get -TimeoutSec 2 -SkipHttpErrorCheck
            if ([int]$response.StatusCode -eq $ExpectedStatus) {
                return
            }
        }
        catch {
            # The listener may not have bound yet.
        }

        Start-Sleep -Milliseconds 250
    }

    throw "$ProcessName did not become ready at $Uri within $ReadyTimeoutSeconds seconds."
}

function Stop-OwnedProcess {
    param(
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory)][string]$ProcessName
    )

    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            Write-Host "Stopping $ProcessName..."
            $Process.Kill($true)
            $null = $Process.WaitForExit(5000)
        }
    }
    catch {
        Write-Warning "Could not stop $ProcessName cleanly: $($_.Exception.Message)"
    }
    finally {
        $Process.Dispose()
    }
}

try {
    Assert-CommandAvailable "dotnet"
    Assert-CommandAvailable "tunnel-client"

    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "GW2AccountMCP project was not found at '$projectPath'."
    }

    if (Test-TcpPortInUse $McpPort) {
        throw "MCP port $McpPort is already in use. Stop the existing listener or choose another port with -McpPort."
    }

    if (Test-TcpPortInUse $TunnelHealthPort) {
        throw "Tunnel health port $TunnelHealthPort is already in use. Stop the existing listener or choose another port with -TunnelHealthPort."
    }

    Write-Host "Building GW2AccountMCP..."
    & dotnet build $projectPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    Write-Host "Starting GW2AccountMCP at $McpBaseUrl..."
    $serverProcess = Start-OwnedProcess "dotnet" @(
        "run",
        "--no-build",
        "--project",
        $projectPath,
        "--",
        "--urls",
        $McpBaseUrl.AbsoluteUri.TrimEnd("/")
    )
    Wait-ForHttpStatus $mcpEndpoint 405 $serverProcess "GW2AccountMCP"
    Write-Host "GW2AccountMCP is ready."

    Write-Host "Validating tunnel profile '$TunnelProfile'..."
    $null = & tunnel-client doctor `
        --profile $TunnelProfile `
        --mcp.server-url $mcpEndpoint.AbsoluteUri `
        --health.listen-addr $TunnelHealthBaseUrl.Authority `
        --json
    if ($LASTEXITCODE -ne 0) {
        throw "Tunnel profile validation failed. Run 'tunnel-client doctor --profile $TunnelProfile --explain' for redacted diagnostics."
    }

    Write-Host "Starting tunnel client..."
    $tunnelProcess = Start-OwnedProcess "tunnel-client" @(
        "run",
        "--profile",
        $TunnelProfile,
        "--mcp.server-url",
        $mcpEndpoint.AbsoluteUri,
        "--health.listen-addr",
        $TunnelHealthBaseUrl.Authority
    )
    Wait-ForHttpStatus $tunnelReadyEndpoint 200 $tunnelProcess "tunnel-client"
    Write-Host "Tunnel is healthy and ready."

    if (-not $NoBrowser) {
        Start-Process $tunnelUiEndpoint.AbsoluteUri
    }

    Write-Host "GW2 Account MCP is running. Press Ctrl+C to stop both processes."
    while (-not $serverProcess.HasExited -and -not $tunnelProcess.HasExited) {
        Start-Sleep -Seconds 1
    }

    if ($serverProcess.HasExited) {
        throw "GW2AccountMCP exited unexpectedly with code $($serverProcess.ExitCode)."
    }

    throw "tunnel-client exited unexpectedly with code $($tunnelProcess.ExitCode)."
}
finally {
    Stop-OwnedProcess $tunnelProcess "tunnel-client"
    Stop-OwnedProcess $serverProcess "GW2AccountMCP"
}
