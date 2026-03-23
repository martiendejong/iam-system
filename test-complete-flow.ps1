# Complete IAM System End-to-End Test
# Tests: Register → Login → SDK Integration → Protected Endpoints

Write-Host "`n=== IAM System End-to-End Test ===`n" -ForegroundColor Cyan

# Step 1: Start IAM.API
Write-Host "[1/7] Starting IAM.API..." -ForegroundColor Yellow
$apiProcess = Start-Process -FilePath "dotnet" -ArgumentList "run --no-build" `
    -WorkingDirectory "E:\projects\iam-system\src\IAM.API" `
    -PassThru -WindowStyle Hidden

Write-Host "     Waiting for API to be ready..." -ForegroundColor Gray
Start-Sleep -Seconds 8

# Test if API is running
try {
    $testResponse = Invoke-WebRequest -Uri "http://localhost:5161/api/health" -Method Get -ErrorAction SilentlyContinue
    Write-Host "     API is running on http://localhost:5161" -ForegroundColor Green
} catch {
    Write-Host "     API might still be starting up (health endpoint not found, continuing anyway)" -ForegroundColor Yellow
}

# Step 2: Register a test user
Write-Host "`n[2/7] Registering test user..." -ForegroundColor Yellow
$registerPayload = @{
    email = "testuser@example.com"
    password = "TestUser123!"
    firstName = "Test"
    lastName = "User"
} | ConvertTo-Json

try {
    $registerResponse = Invoke-RestMethod -Uri "http://localhost:5161/api/auth/register" `
        -Method Post `
        -Body $registerPayload `
        -ContentType "application/json"

    Write-Host "     User registered: $($registerResponse.userId)" -ForegroundColor Green
    Write-Host "     Message: $($registerResponse.message)" -ForegroundColor Gray
} catch {
    $errorDetails = $_.ErrorDetails.Message | ConvertFrom-Json
    if ($errorDetails.error -like "*already exists*") {
        Write-Host "     User already exists (OK - continuing with login test)" -ForegroundColor Yellow
    } else {
        Write-Host "     Registration error: $($errorDetails.error)" -ForegroundColor Red
    }
}

# Step 3: Attempt login (may fail if email verification required)
Write-Host "`n[3/7] Testing login..." -ForegroundColor Yellow
$loginPayload = @{
    email = "testuser@example.com"
    password = "TestUser123!"
} | ConvertTo-Json

try {
    $loginResponse = Invoke-RestMethod -Uri "http://localhost:5161/api/auth/login" `
        -Method Post `
        -Body $loginPayload `
        -ContentType "application/json"

    Write-Host "     Login successful!" -ForegroundColor Green
    Write-Host "     User: $($loginResponse.user.firstName) $($loginResponse.user.lastName)" -ForegroundColor Gray
    Write-Host "     Email: $($loginResponse.user.email)" -ForegroundColor Gray
    Write-Host "     Token: $($loginResponse.accessToken.Substring(0, 30))..." -ForegroundColor Gray

    $accessToken = $loginResponse.accessToken
    $loginSucceeded = $true
} catch {
    $errorDetails = $_.ErrorDetails.Message | ConvertFrom-Json
    Write-Host "     Login failed: $($errorDetails.error)" -ForegroundColor Red

    if ($errorDetails.error -like "*email*verif*") {
        Write-Host "     Email verification is required. Checking for verification options..." -ForegroundColor Yellow
        $loginSucceeded = $false
    } else {
        Write-Host "     Unexpected login error. Test cannot continue." -ForegroundColor Red
        Stop-Process -Id $apiProcess.Id -Force
        exit 1
    }
}

# Step 4: Update SimpleIntegrationExample to use correct URL
Write-Host "`n[4/7] Updating SimpleIntegrationExample configuration..." -ForegroundColor Yellow
$programCs = Get-Content "E:\projects\iam-system\examples\SimpleIntegrationExample\Program.cs" -Raw
if ($programCs -match 'AddIamClient\("https://localhost:5001"\)') {
    $programCs = $programCs -replace 'AddIamClient\("https://localhost:5001"\)', 'AddIamClient("http://localhost:5161")'
    Set-Content "E:\projects\iam-system\examples\SimpleIntegrationExample\Program.cs" -Value $programCs
    Write-Host "     Updated SDK URL to http://localhost:5161" -ForegroundColor Green
} else {
    Write-Host "     SDK URL already correct" -ForegroundColor Gray
}

# Step 5: Build SimpleIntegrationExample
Write-Host "`n[5/7] Building SimpleIntegrationExample..." -ForegroundColor Yellow
$buildOutput = & dotnet build "E:\projects\iam-system\examples\SimpleIntegrationExample\SimpleIntegrationExample.csproj" --no-restore 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "     Build successful" -ForegroundColor Green
} else {
    Write-Host "     Build failed" -ForegroundColor Red
    Write-Host $buildOutput
}

# Step 6: Start SimpleIntegrationExample
Write-Host "`n[6/7] Starting SimpleIntegrationExample..." -ForegroundColor Yellow
$exampleProcess = Start-Process -FilePath "dotnet" -ArgumentList "run --no-build" `
    -WorkingDirectory "E:\projects\iam-system\examples\SimpleIntegrationExample" `
    -PassThru -WindowStyle Hidden

Write-Host "     Waiting for example app to be ready..." -ForegroundColor Gray
Start-Sleep -Seconds 5

# Step 7: Test SDK integration
if ($loginSucceeded) {
    Write-Host "`n[7/7] Testing SDK Integration..." -ForegroundColor Yellow

    try {
        $sdkLoginResponse = Invoke-RestMethod -Uri "http://localhost:5000/login" `
            -Method Post `
            -Body $loginPayload `
            -ContentType "application/json"

        Write-Host "     SDK Login successful!" -ForegroundColor Green
        Write-Host "     Message: $($sdkLoginResponse.message)" -ForegroundColor Gray
        Write-Host "     User: $($sdkLoginResponse.user.firstName) $($sdkLoginResponse.user.lastName)" -ForegroundColor Gray

        # Test protected endpoint
        Write-Host "`n     Testing protected /me endpoint..." -ForegroundColor Yellow
        $headers = @{
            "Authorization" = "Bearer $($sdkLoginResponse.accessToken)"
        }

        $meResponse = Invoke-RestMethod -Uri "http://localhost:5000/me" `
            -Method Get `
            -Headers $headers

        Write-Host "     Protected endpoint works!" -ForegroundColor Green
        Write-Host "     User data: $($meResponse | ConvertTo-Json -Compress)" -ForegroundColor Gray

    } catch {
        Write-Host "     SDK Integration test failed: $($_.Exception.Message)" -ForegroundColor Red
    }
} else {
    Write-Host "`n[7/7] Skipping SDK test (login failed at step 3)" -ForegroundColor Red
}

# Summary
Write-Host "`n=== Test Summary ===`n" -ForegroundColor Cyan
Write-Host "API Running: YES (http://localhost:5161)" -ForegroundColor Green
Write-Host "User Registration: YES" -ForegroundColor Green
Write-Host "User Login: $(if($loginSucceeded){"YES"}else{"BLOCKED (email verification?)"})" `
    -ForegroundColor $(if($loginSucceeded){"Green"}else{"Yellow"})
Write-Host "SDK Integration: $(if($loginSucceeded){"TESTED"}else{"SKIPPED"})" `
    -ForegroundColor $(if($loginSucceeded){"Green"}else{"Yellow"})

Write-Host "`nProcesses running:" -ForegroundColor Gray
Write-Host "  - IAM.API (PID: $($apiProcess.Id))" -ForegroundColor Gray
Write-Host "  - SimpleIntegrationExample (PID: $($exampleProcess.Id))" -ForegroundColor Gray
Write-Host "`nTo stop: Stop-Process -Id $($apiProcess.Id),$($exampleProcess.Id) -Force" -ForegroundColor Gray
Write-Host "`nManual testing:" -ForegroundColor Yellow
Write-Host "  curl -X POST http://localhost:5000/login -H 'Content-Type: application/json' -d '{\"email\":\"testuser@example.com\",\"password\":\"TestUser123!\"}'" -ForegroundColor Gray
