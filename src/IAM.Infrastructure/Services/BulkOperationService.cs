using System.Globalization;
using System.Text;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Services;

public class BulkOperationService : IBulkOperationService
{
    private readonly IAMDbContext _context;

    public BulkOperationService(IAMDbContext context)
    {
        _context = context;
    }

    public async Task<BulkOperation> ImportUsersAsync(
        Guid tenantId,
        Guid createdByUserId,
        Stream fileStream,
        string fileName,
        BulkOperationFormat format,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        var operation = new BulkOperation
        {
            TenantId = tenantId,
            Type = BulkOperationType.Import,
            Status = BulkOperationStatus.Processing,
            Format = format,
            FileName = fileName,
            DryRun = dryRun,
            CreatedByUserId = createdByUserId,
            StartedAt = DateTime.UtcNow
        };

        _context.BulkOperations.Add(operation);
        await _context.SaveChangesAsync(ct);

        try
        {
            List<UserImportRow> rows;
            if (format == BulkOperationFormat.CSV)
            {
                rows = await ParseCsvAsync(fileStream, ct);
            }
            else
            {
                rows = await ParseJsonAsync(fileStream, ct);
            }

            operation.TotalRows = rows.Count;
            await _context.SaveChangesAsync(ct);

            var errors = new List<object>();
            var existingEmails = await _context.Users
                .Select(u => u.Email.ToLower())
                .ToHashSetAsync(ct);

            foreach (var (row, index) in rows.Select((r, i) => (r, i)))
            {
                operation.ProcessedRows = index + 1;

                var rowErrors = ValidateRow(row, index, existingEmails);
                if (rowErrors.Count > 0)
                {
                    operation.ErrorRows++;
                    errors.Add(new { row = index + 1, errors = rowErrors });
                    continue;
                }

                if (!dryRun)
                {
                    var user = new User
                    {
                        Email = row.Email!.Trim(),
                        FirstName = row.FirstName!.Trim(),
                        LastName = row.LastName!.Trim(),
                        PhoneNumber = row.PhoneNumber?.Trim(),
                        PasswordHash = BCrypt.Net.BCrypt.HashPassword(GenerateTemporaryPassword(), workFactor: 12),
                        IsActive = true,
                        EmailConfirmed = false,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    _context.Users.Add(user);
                    existingEmails.Add(user.Email.ToLower());

                    // Assign role if specified
                    if (!string.IsNullOrWhiteSpace(row.RoleName))
                    {
                        var role = await _context.Roles
                            .FirstOrDefaultAsync(r =>
                                r.Name.ToLower() == row.RoleName.Trim().ToLower() &&
                                (r.TenantId == tenantId || r.TenantId == null), ct);

                        if (role != null)
                        {
                            _context.UserRoles.Add(new UserRole
                            {
                                UserId = user.Id,
                                RoleId = role.Id,
                                TenantId = tenantId,
                                GrantedAt = DateTime.UtcNow
                            });
                        }
                        else
                        {
                            errors.Add(new { row = index + 1, errors = new[] { $"Role '{row.RoleName}' not found, user created without role" } });
                        }
                    }
                }

                operation.SuccessRows++;
            }

            if (!dryRun)
            {
                await _context.SaveChangesAsync(ct);
            }

            operation.ErrorDetails = errors.Count > 0 ? JsonSerializer.Serialize(errors) : null;
            operation.Status = operation.ErrorRows > 0 && operation.SuccessRows == 0
                ? BulkOperationStatus.Failed
                : BulkOperationStatus.Completed;
            operation.CompletedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(ct);
            return operation;
        }
        catch (Exception ex)
        {
            operation.Status = BulkOperationStatus.Failed;
            operation.ErrorDetails = JsonSerializer.Serialize(new[] { new { row = 0, errors = new[] { ex.Message } } });
            operation.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
            return operation;
        }
    }

    public async Task<BulkOperation> ExportUsersAsync(
        Guid tenantId,
        Guid createdByUserId,
        BulkOperationFormat format,
        CancellationToken ct = default)
    {
        var operation = new BulkOperation
        {
            TenantId = tenantId,
            Type = BulkOperationType.Export,
            Status = BulkOperationStatus.Processing,
            Format = format,
            CreatedByUserId = createdByUserId,
            StartedAt = DateTime.UtcNow
        };

        _context.BulkOperations.Add(operation);
        await _context.SaveChangesAsync(ct);

        try
        {
            // Get all users with their roles in the tenant
            var userRoles = await _context.UserRoles
                .Where(ur => ur.TenantId == tenantId)
                .Include(ur => ur.User)
                .Include(ur => ur.Role)
                .ToListAsync(ct);

            var userIds = userRoles.Select(ur => ur.UserId).Distinct().ToHashSet();

            // Also get users without roles in the tenant (via direct tenant association)
            var allTenantUsers = await _context.Users
                .Where(u => _context.UserRoles.Any(ur => ur.UserId == u.Id && ur.TenantId == tenantId))
                .ToListAsync(ct);

            var exportRows = new List<UserExportRow>();
            foreach (var user in allTenantUsers)
            {
                var roles = userRoles
                    .Where(ur => ur.UserId == user.Id)
                    .Select(ur => ur.Role?.Name ?? "")
                    .Where(r => !string.IsNullOrEmpty(r))
                    .ToList();

                exportRows.Add(new UserExportRow
                {
                    Id = user.Id.ToString(),
                    Email = user.Email,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    PhoneNumber = user.PhoneNumber ?? "",
                    IsActive = user.IsActive,
                    EmailConfirmed = user.EmailConfirmed,
                    TwoFactorEnabled = user.TwoFactorEnabled,
                    Roles = string.Join(";", roles),
                    CreatedAt = user.CreatedAt.ToString("o"),
                    LastLoginAt = user.LastLoginAt?.ToString("o") ?? ""
                });
            }

            operation.TotalRows = exportRows.Count;
            operation.ProcessedRows = exportRows.Count;
            operation.SuccessRows = exportRows.Count;

            byte[] fileBytes;
            string contentType;
            string exportFileName;

            if (format == BulkOperationFormat.CSV)
            {
                fileBytes = GenerateCsvExport(exportRows);
                contentType = "text/csv";
                exportFileName = $"users-export-{tenantId:N}-{DateTime.UtcNow:yyyyMMddHHmmss}.csv";
            }
            else
            {
                fileBytes = GenerateJsonExport(exportRows);
                contentType = "application/json";
                exportFileName = $"users-export-{tenantId:N}-{DateTime.UtcNow:yyyyMMddHHmmss}.json";
            }

            // Store as base64 data URL
            var base64 = Convert.ToBase64String(fileBytes);
            operation.ResultUrl = $"data:{contentType};base64,{base64}";
            operation.FileName = exportFileName;
            operation.Status = BulkOperationStatus.Completed;
            operation.CompletedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(ct);
            return operation;
        }
        catch (Exception ex)
        {
            operation.Status = BulkOperationStatus.Failed;
            operation.ErrorDetails = JsonSerializer.Serialize(new[] { new { row = 0, errors = new[] { ex.Message } } });
            operation.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
            return operation;
        }
    }

    public async Task<List<BulkOperation>> GetOperationsAsync(
        Guid tenantId,
        int skip = 0,
        int take = 20,
        CancellationToken ct = default)
    {
        return await _context.BulkOperations
            .Where(b => b.TenantId == tenantId)
            .OrderByDescending(b => b.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<BulkOperation?> GetOperationAsync(
        Guid operationId,
        CancellationToken ct = default)
    {
        return await _context.BulkOperations
            .FirstOrDefaultAsync(b => b.Id == operationId, ct);
    }

    public async Task<(byte[] Data, string ContentType, string FileName)?> GetOperationResultAsync(
        Guid operationId,
        CancellationToken ct = default)
    {
        var operation = await _context.BulkOperations
            .FirstOrDefaultAsync(b => b.Id == operationId, ct);

        if (operation == null || operation.ResultUrl == null)
            return null;

        // Decode base64 data URL
        if (operation.ResultUrl.StartsWith("data:"))
        {
            var semicolonIndex = operation.ResultUrl.IndexOf(';');
            var commaIndex = operation.ResultUrl.IndexOf(',');
            var contentType = operation.ResultUrl[5..semicolonIndex];
            var base64Data = operation.ResultUrl[(commaIndex + 1)..];
            var bytes = Convert.FromBase64String(base64Data);
            var fileName = operation.FileName ?? $"export-{operationId:N}.dat";
            return (bytes, contentType, fileName);
        }

        return null;
    }

    // ---- Private Helpers ----

    private static async Task<List<UserImportRow>> ParseCsvAsync(Stream stream, CancellationToken ct)
    {
        var rows = new List<UserImportRow>();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var headerLine = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(headerLine))
            throw new InvalidOperationException("CSV file is empty or has no header row");

        var headers = headerLine.Split(',')
            .Select(h => h.Trim().Trim('"').ToLowerInvariant())
            .ToArray();

        var emailIdx = Array.IndexOf(headers, "email");
        var firstNameIdx = Array.IndexOf(headers, "firstname");
        if (firstNameIdx < 0) firstNameIdx = Array.IndexOf(headers, "first_name");
        if (firstNameIdx < 0) firstNameIdx = Array.IndexOf(headers, "first name");
        var lastNameIdx = Array.IndexOf(headers, "lastname");
        if (lastNameIdx < 0) lastNameIdx = Array.IndexOf(headers, "last_name");
        if (lastNameIdx < 0) lastNameIdx = Array.IndexOf(headers, "last name");
        var phoneIdx = Array.IndexOf(headers, "phonenumber");
        if (phoneIdx < 0) phoneIdx = Array.IndexOf(headers, "phone_number");
        if (phoneIdx < 0) phoneIdx = Array.IndexOf(headers, "phone");
        var roleIdx = Array.IndexOf(headers, "role");
        if (roleIdx < 0) roleIdx = Array.IndexOf(headers, "rolename");
        if (roleIdx < 0) roleIdx = Array.IndexOf(headers, "role_name");

        if (emailIdx < 0)
            throw new InvalidOperationException("CSV must contain an 'Email' column");

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = ParseCsvLine(line);

            rows.Add(new UserImportRow
            {
                Email = GetField(fields, emailIdx),
                FirstName = GetField(fields, firstNameIdx),
                LastName = GetField(fields, lastNameIdx),
                PhoneNumber = GetField(fields, phoneIdx),
                RoleName = GetField(fields, roleIdx)
            });
        }

        return rows;
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString().Trim());
        return fields.ToArray();
    }

    private static async Task<List<UserImportRow>> ParseJsonAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync(ct);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // Support both array format and { users: [...] } format
        try
        {
            var rows = JsonSerializer.Deserialize<List<UserImportRow>>(json, options);
            return rows ?? new List<UserImportRow>();
        }
        catch
        {
            var wrapper = JsonSerializer.Deserialize<UserImportWrapper>(json, options);
            return wrapper?.Users ?? new List<UserImportRow>();
        }
    }

    private static List<string> ValidateRow(UserImportRow row, int index, HashSet<string> existingEmails)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(row.Email))
        {
            errors.Add("Email is required");
        }
        else if (!row.Email.Contains('@') || !row.Email.Contains('.'))
        {
            errors.Add($"Invalid email format: '{row.Email}'");
        }
        else if (existingEmails.Contains(row.Email.Trim().ToLower()))
        {
            errors.Add($"Email '{row.Email}' already exists");
        }

        if (string.IsNullOrWhiteSpace(row.FirstName))
            errors.Add("First name is required");

        if (string.IsNullOrWhiteSpace(row.LastName))
            errors.Add("Last name is required");

        return errors;
    }

    private static string? GetField(string[] fields, int index)
    {
        if (index < 0 || index >= fields.Length) return null;
        var value = fields[index].Trim().Trim('"');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string GenerateTemporaryPassword()
    {
        var random = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(random);
        return Convert.ToBase64String(random) + "!Aa1";
    }

    private static byte[] GenerateCsvExport(List<UserExportRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,Email,FirstName,LastName,PhoneNumber,IsActive,EmailConfirmed,TwoFactorEnabled,Roles,CreatedAt,LastLoginAt");

        foreach (var row in rows)
        {
            sb.AppendLine($"\"{row.Id}\",\"{EscapeCsv(row.Email)}\",\"{EscapeCsv(row.FirstName)}\",\"{EscapeCsv(row.LastName)}\",\"{EscapeCsv(row.PhoneNumber)}\",{row.IsActive},{row.EmailConfirmed},{row.TwoFactorEnabled},\"{EscapeCsv(row.Roles)}\",\"{row.CreatedAt}\",\"{row.LastLoginAt}\"");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] GenerateJsonExport(List<UserExportRow> rows)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        var json = JsonSerializer.Serialize(new { exportedAt = DateTime.UtcNow, totalUsers = rows.Count, users = rows }, options);
        return Encoding.UTF8.GetBytes(json);
    }

    private static string EscapeCsv(string value)
    {
        return value.Replace("\"", "\"\"");
    }

    // ---- DTOs ----

    private class UserImportRow
    {
        public string? Email { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? PhoneNumber { get; set; }
        public string? RoleName { get; set; }
    }

    private class UserImportWrapper
    {
        public List<UserImportRow>? Users { get; set; }
    }

    private class UserExportRow
    {
        public string Id { get; set; } = "";
        public string Email { get; set; } = "";
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string PhoneNumber { get; set; } = "";
        public bool IsActive { get; set; }
        public bool EmailConfirmed { get; set; }
        public bool TwoFactorEnabled { get; set; }
        public string Roles { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string LastLoginAt { get; set; } = "";
    }
}
