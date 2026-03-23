# Quick script to verify the test user in database
$connectionString = "Host=localhost;Port=5432;Database=iamdb;Username=iamuser;Password=iampassword"

Add-Type -Path "C:\Program Files\PostgreSQL\16\Npgsql.dll" -ErrorAction SilentlyContinue

try {
    $conn = New-Object Npgsql.NpgsqlConnection($connectionString)
    $conn.Open()

    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "UPDATE ""Users"" SET ""EmailConfirmed"" = true WHERE ""Email"" = 'testuser@example.com'"

    $rowsAffected = $cmd.ExecuteNonQuery()

    Write-Host "Updated $rowsAffected user(s) - Email confirmed set to true" -ForegroundColor Green

    $conn.Close()
} catch {
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Trying alternative method via dotnet ef..." -ForegroundColor Yellow

    # Alternative: Use SQL via dotnet ef
    $sql = "UPDATE \`"Users\`" SET \`"EmailConfirmed\`" = true WHERE \`"Email\`" = 'testuser@example.com'"

    Push-Location "E:\projects\iam-system\src\IAM.API"
    $result = dotnet ef database script --idempotent --output temp-verify.sql
    $sql | Out-File -FilePath "verify-user.sql" -Encoding UTF8

    Write-Host "SQL file created. Manually run: psql -h localhost -U iamuser -d iamdb -f verify-user.sql" -ForegroundColor Yellow
    Pop-Location
}
