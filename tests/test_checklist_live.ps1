$ErrorActionPreference = "Stop"

$exe = "c:\Users\solarquack\Downloads\RawAccel_v1.7.1\RawAccel\src\BetterRawAccel\bin\x64\Release\BetterRawAccel.exe"
$workingDir = "c:\Users\solarquack\Downloads\RawAccel_v1.7.1\RawAccel\src\BetterRawAccel\bin\x64\Release"

Write-Host "Killing any prior BetterRawAccel instances..."
Get-Process BetterRawAccel -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

Write-Host "Starting BetterRawAccel Release binary..."
$proc = Start-Process -FilePath $exe -WorkingDirectory $workingDir -PassThru
Start-Sleep -Seconds 3

function Call-McpTool($port, $toolName, $toolArgs) {
    $url = "http://127.0.0.1:$port/mcp"
    $body = @{
        jsonrpc = "2.0"
        id = [Guid]::NewGuid().ToString()
        method = "tools/call"
        params = @{
            name = $toolName
            arguments = $toolArgs
        }
    } | ConvertTo-Json -Depth 10

    $resp = Invoke-RestMethod -Uri $url -Method Post -ContentType "application/json" -Body $body
    return $resp
}

try {
    Write-Host "`n=== Test 1: Query initial state on port 47700 ==="
    $init = Call-McpTool 47700 "get_state" @{}
    $stateJson = $init.result.content[0].text | ConvertFrom-Json
    Write-Host "Profile name:" $stateJson.selectedProfile
    Write-Host "App version:" $stateJson.appVersion
    Write-Host "Unwritten changes:" $stateJson.unwrittenChanges

    Write-Host "`n=== Test 2: Test MCP Port Switching (47700 -> 47702 -> 47700) ==="
    Write-Host "Switching port to 47702..."
    $changePortRes = Call-McpTool 47700 "set_app_settings" @{ mcpPort = 47702 }
    Start-Sleep -Milliseconds 1200

    Write-Host "Calling get_state on new port 47702..."
    $port702Res = Call-McpTool 47702 "get_state" @{}
    $state702 = $port702Res.result.content[0].text | ConvertFrom-Json
    Write-Host "Successfully queried port 47702! Profile:" $state702.selectedProfile

    Write-Host "Switching port back to 47700..."
    $revertPortRes = Call-McpTool 47702 "set_app_settings" @{ mcpPort = 47700 }
    Start-Sleep -Milliseconds 1200

    Write-Host "Calling get_state on restored port 47700..."
    $port700Res = Call-McpTool 47700 "get_state" @{}
    $state700 = $port700Res.result.content[0].text | ConvertFrom-Json
    Write-Host "Successfully verified port 47700 restored! Profile:" $state700.selectedProfile

    Write-Host "`n=== Test 3: Test Advanced Options (JSON-only controls) ==="
    Write-Host "Patching snap, speedCap, lrRatio, udRatio, inputSmooth, scaleSmooth, outputSmooth..."
    $patchArgs = @{
        patch = @{
            snap = 14.5
            speedCap = 95.0
            lrRatio = 1.25
            udRatio = 0.85
            inputSmooth = 8.0
            scaleSmooth = 12.0
            outputSmooth = 16.0
        }
    }
    $patchRes = Call-McpTool 47700 "update_profile" $patchArgs
    $patchedState = $patchRes.result.content[0].text | ConvertFrom-Json
    $prof = $patchedState.profile
    Write-Host "snap:" $prof.snap "speedCap:" $prof.speedCap "lrRatio:" $prof.lrRatio "udRatio:" $prof.udRatio
    Write-Host "inputSmooth:" $prof.inputSmooth "scaleSmooth:" $prof.scaleSmooth "outputSmooth:" $prof.outputSmooth
    
    if ($prof.snap -ne 14.5 -or $prof.speedCap -ne 95.0 -or $prof.lrRatio -ne 1.25 -or $prof.udRatio -ne 0.85 -or $prof.inputSmooth -ne 8.0 -or $prof.scaleSmooth -ne 12.0 -or $prof.outputSmooth -ne 16.0) {
        throw "Advanced profile options failed to reflect in profile!"
    }
    Write-Host "All advanced options successfully verified in profile DTO!"

    Write-Host "`n=== Test 4: Test Apply at Windows Sign-in (startWithWindows) ==="
    $startupFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
    $lnkPath = Join-Path $startupFolder "BetterRawAccel settings.lnk"

    Write-Host "Enabling startWithWindows..."
    $setStartupRes = Call-McpTool 47700 "set_app_settings" @{ startWithWindows = $true }
    $appSettings = Call-McpTool 47700 "get_app_settings" @{}
    $appSettingsJson = $appSettings.result.content[0].text | ConvertFrom-Json
    Write-Host "startWithWindows in settings:" $appSettingsJson.startWithWindows
    Write-Host "LNK file exists:" (Test-Path $lnkPath)
    if (-not (Test-Path $lnkPath)) {
        throw "Startup shortcut file was not created!"
    }

    Write-Host "Disabling startWithWindows..."
    $unsetStartupRes = Call-McpTool 47700 "set_app_settings" @{ startWithWindows = $false }
    $appSettings2 = Call-McpTool 47700 "get_app_settings" @{}
    $appSettings2Json = $appSettings2.result.content[0].text | ConvertFrom-Json
    Write-Host "startWithWindows in settings:" $appSettings2Json.startWithWindows
    Write-Host "LNK file exists:" (Test-Path $lnkPath)
    if (Test-Path $lnkPath) {
        throw "Startup shortcut file was not removed!"
    }
    Write-Host "Apply at Windows sign-in successfully tested and cleaned up!"

    Write-Host "`n=== Test 5: Test Apply on Start Setting ==="
    $setApplyOnStart = Call-McpTool 47700 "set_app_settings" @{ applyOnStart = $true }
    $appSettings3 = Call-McpTool 47700 "get_app_settings" @{}
    $appSettings3Json = $appSettings3.result.content[0].text | ConvertFrom-Json
    Write-Host "applyOnStart:" $appSettings3Json.applyOnStart
    if (-not $appSettings3Json.applyOnStart) {
        throw "applyOnStart was not persisted!"
    }
    # Reset back to false
    $null = Call-McpTool 47700 "set_app_settings" @{ applyOnStart = $false }

    Write-Host "`n=== Test 6: Capture Window & Clean Revert ==="
    $cap = Call-McpTool 47700 "capture_window" @{}
    $base64 = $cap.result.content[0].text
    $bytes = [Convert]::FromBase64String($base64)
    $capPath = "C:\Users\solarquack\.gemini\antigravity\brain\526aab6e-814e-4c00-8c71-7328a95a7cd7\capture_checklist_test.png"
    [IO.File]::WriteAllBytes($capPath, $bytes)
    Write-Host "Saved window capture to $capPath"

    Write-Host "Reverting changes..."
    $rev = Call-McpTool 47700 "revert" @{}
    $revJson = $rev.result.content[0].text | ConvertFrom-Json
    Write-Host "Reverted unwritten changes, pending:" $revJson.unwrittenChanges

    Write-Host "`n>>> ALL CHECKLIST VERIFICATION SUITES PASSED! <<<"
}
finally {
    Write-Host "Stopping BetterRawAccel process..."
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}
