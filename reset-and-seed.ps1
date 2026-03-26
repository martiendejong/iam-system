# Reset Database and Test Development Seeder
# Drops database, recreates it, and tests the auto-seeding

Write-Host "`n=== IAM Database Reset & Seed Test ===`n" -ForegroundColor Cyan

# Step 1: Stop any running API
Write-Host "[1/5] Stopping running API..." -ForegroundColor Yellow
Stop-Process -Name dotnet -Force -ErrorAction SilentlyContinue
Write-Host "     Stopped" -ForegroundColor Green

# Step 2: Drop and recreate database
Write-Host "`n[2/5] Dropping and recreating database..." -ForegroundColor Yellow
Push-Location "E:\projects\iam-system\src\IAM.API"

dotnet ef database drop --force
if ($LASTEXITCODE -eq 0) {
    Write-Host "     Database dropped" -ForegroundColor Green
} else {
    Write-Host "     Drop failed (might not exist)" -ForegroundColor Yellow
}

dotnet ef database update
if ($LASTEXITCODE -eq 0) {
    Write-Host "     Database created with migrations" -ForegroundColor Green
} else {
    Write-Host "     Migration failed!" -ForegroundColor Red
    Pop-Location
    exit 1
}

Pop-Location

# Step 3: Start API (will auto-seed)
Write-Host "`n[3/5] Starting IAM.API (auto-seeding)..." -ForegroundColor Yellow
$apiProcess = Start-Process -FilePath "dotnet" -ArgumentList "run --no-build" `
    -WorkingDirectory "E:\projects\iam-system\src\IAM.API" `
    -PassThru -WindowStyle Hidden

Write-Host "     Waiting for API to start..." -ForegroundColor Gray
Start-Sleep -Seconds 8

# Step 4: Test login with seeded users
Write-Host "`n[4/5] Testing seeded users..." -ForegroundColor Yellow

$users = @(
    @{ Email = "admin@test.com"; Password = "Admin123!"; Name = "Admin" }
    @{ Email = "user@test.com"; Password = "User123!"; Name = "User" }
    @{ Email = "dev@test.com"; Password = "Dev123!"; Name = "Developer" }
)

$successCount = 0
foreach ($user in $users) {
    $payload = @{
        email = $user.Email
        password = $user.Password
    } | ConvertTo-Json

    try {
        $response = Invoke-RestMethod -Uri "http://localhost:5161/api/auth/login" `
            -Method Post `
            -Body $payload `
            -ContentType "application/json" `
            -ErrorAction Stop

        Write-Host "     ✅ $($user.Name) login: SUCCESS" -ForegroundColor Green
        Write-Host "        Token: $($response.accessToken.Substring(0, 30))..." -ForegroundColor Gray
        $successCount++
    } catch {
        Write-Host "     ❌ $($user.Name) login: FAILED" -ForegroundColor Red
        if ($_.ErrorDetails.Message) {
            $error = ($_.ErrorDetails.Message | ConvertFrom-Json).error
            Write-Host "        Error: $error" -ForegroundColor Yellow
        }
    }
}

# Step 5: Summary
Write-Host "`n[5/5] Test Summary" -ForegroundColor Yellow
Write-Host "     Seeded users tested: $($users.Count)" -ForegroundColor Gray
Write-Host "     Successful logins: $successCount" -ForegroundColor $(if($successCount -eq $users.Count){"Green"}else{"Red"})

if ($successCount -eq $users.Count) {
    Write-Host "`n✅ SUCCESS: Development seeder working perfectly!" -ForegroundColor Green
    Write-Host "   All 3 test users created and verified automatically." -ForegroundColor Gray
} else {
    Write-Host "`n❌ FAILURE: Some users failed to login" -ForegroundColor Red
}

Write-Host "`nAPI Process ID: $($apiProcess.Id)" -ForegroundColor Gray
Write-Host "To stop: Stop-Process -Id $($apiProcess.Id) -Force" -ForegroundColor Gray

Write-Host "`n=== Test Complete ===`n" -ForegroundColor Cyan
