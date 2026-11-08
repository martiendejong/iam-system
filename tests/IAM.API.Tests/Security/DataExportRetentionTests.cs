using System.Security.Claims;
using IAM.API.Controllers;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace IAM.API.Tests.Security;

/// <summary>
/// Task 5851: stored GDPR export text must not outlive its 48h expiry or the user's erasure.
/// </summary>
public class DataExportRetentionTests
{
    private const string Payload = "data:application/json;base64,eyJlbWFpbCI6InNlY3JldCJ9";

    private static IAMDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new IAMDbContext(options);
    }

    private static DataRequestService CreateService(IAMDbContext context)
        => new(context, new FakeEmailService(), NullLogger<DataRequestService>.Instance);

    private static async Task<User> AddUserAsync(IAMDbContext context, string name)
    {
        var user = new User
        {
            Email = $"{name}@example.com",
            PasswordHash = "x",
            FirstName = name,
            LastName = "Tester",
            EmailConfirmed = true,
            IsActive = true
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private static async Task<DataRequest> AddExportAsync(IAMDbContext context, Guid userId, DateTime? expiresAt, string? dataUrl = Payload)
    {
        var export = new DataRequest
        {
            UserId = userId,
            Type = DataRequestType.Export,
            Status = DataRequestStatus.Completed,
            DataUrl = dataUrl,
            ExpiresAt = expiresAt,
            CompletedAt = DateTime.UtcNow
        };
        context.DataRequests.Add(export);
        await context.SaveChangesAsync();
        return export;
    }

    private static async Task<string?> StoredDataUrlAsync(IAMDbContext context, Guid requestId)
        => (await context.DataRequests.AsNoTracking().FirstAsync(r => r.Id == requestId)).DataUrl;

    private static DataRequestsController CreateController(DataRequestService service, Guid userId)
    {
        var controller = new DataRequestsController(service, NullLogger<DataRequestsController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test"))
            }
        };
        return controller;
    }

    private static string? ErrorOf(IActionResult result)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        return bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value) as string;
    }

    [Fact]
    public async Task DataErasure_ClearsAllStoredExportsOfThatUser_AndLeavesOtherUsersAlone()
    {
        var context = CreateContext();
        var service = CreateService(context);
        var erased = await AddUserAsync(context, "erased-export");
        var bystander = await AddUserAsync(context, "bystander-export");
        var unexpired = await AddExportAsync(context, erased.Id, DateTime.UtcNow.AddHours(40));
        var expired = await AddExportAsync(context, erased.Id, DateTime.UtcNow.AddHours(-1));
        var others = await AddExportAsync(context, bystander.Id, DateTime.UtcNow.AddHours(40));
        var deletion = await service.CreateDeletionRequestAsync(erased.Id, "test");

        await service.ProcessDeletionAsync(deletion.Id, Guid.NewGuid());

        Assert.Null(await StoredDataUrlAsync(context, unexpired.Id));
        Assert.Null(await StoredDataUrlAsync(context, expired.Id));
        Assert.Equal(Payload, await StoredDataUrlAsync(context, others.Id));
    }

    [Fact]
    public async Task ExpiryCleanup_ClearsExpiredExports_AndLeavesUnexpiredOnesUntouched()
    {
        var context = CreateContext();
        var service = CreateService(context);
        var user = await AddUserAsync(context, "cleanup");
        var other = await AddUserAsync(context, "cleanup-other");
        var expired = await AddExportAsync(context, user.Id, DateTime.UtcNow.AddMinutes(-5));
        var expiredOther = await AddExportAsync(context, other.Id, DateTime.UtcNow.AddDays(-30));
        var live = await AddExportAsync(context, user.Id, DateTime.UtcNow.AddHours(47));
        var notGenerated = await AddExportAsync(context, user.Id, null, dataUrl: null);

        var purged = await service.PurgeExpiredExportsAsync();

        Assert.Equal(2, purged);
        Assert.Null(await StoredDataUrlAsync(context, expired.Id));
        Assert.Null(await StoredDataUrlAsync(context, expiredOther.Id));
        Assert.Equal(Payload, await StoredDataUrlAsync(context, live.Id));
        Assert.Null(await StoredDataUrlAsync(context, notGenerated.Id));
        Assert.Equal(0, await service.PurgeExpiredExportsAsync());
    }

    [Fact]
    public async Task ExpiryCleanup_ClearsAnExistingBacklogLargerThanOneBatch()
    {
        var context = CreateContext();
        var service = CreateService(context);
        var user = await AddUserAsync(context, "backlog");
        for (var i = 0; i < 250; i++)
            context.DataRequests.Add(new DataRequest { UserId = user.Id, Type = DataRequestType.Export, Status = DataRequestStatus.Completed, DataUrl = Payload, ExpiresAt = DateTime.UtcNow.AddDays(-2) });
        await context.SaveChangesAsync();

        var purged = await service.PurgeExpiredExportsAsync();

        Assert.Equal(250, purged);
        Assert.Equal(0, await context.DataRequests.CountAsync(r => r.DataUrl != null));
    }

    [Fact]
    public async Task Download_OfPurgedOrExpiredExport_ReturnsExpiredError_NotNotYetAvailable()
    {
        var context = CreateContext();
        var service = CreateService(context);
        var user = await AddUserAsync(context, "download");
        var purged = await AddExportAsync(context, user.Id, DateTime.UtcNow.AddHours(-1), dataUrl: null);
        var erasedBeforeExpiry = await AddExportAsync(context, user.Id, DateTime.UtcNow.AddHours(10), dataUrl: null);
        var expiredNotYetPurged = await AddExportAsync(context, user.Id, DateTime.UtcNow.AddHours(-1));
        var controller = CreateController(service, user.Id);

        Assert.Equal("Export download link has expired", ErrorOf(await controller.DownloadExport(purged.Id, default)));
        Assert.Equal("Export download link has expired", ErrorOf(await controller.DownloadExport(erasedBeforeExpiry.Id, default)));
        Assert.Equal("Export download link has expired", ErrorOf(await controller.DownloadExport(expiredNotYetPurged.Id, default)));
    }

    [Fact]
    public async Task Download_OfUnexpiredExport_StillWorks_AndNotYetGeneratedStaysNotAvailable()
    {
        var context = CreateContext();
        var service = CreateService(context);
        var user = await AddUserAsync(context, "download-live");
        var live = await AddExportAsync(context, user.Id, DateTime.UtcNow.AddHours(10));
        var pending = await AddExportAsync(context, user.Id, null, dataUrl: null);
        var controller = CreateController(service, user.Id);

        Assert.IsType<FileContentResult>(await controller.DownloadExport(live.Id, default));
        Assert.Equal("Export data is not yet available", ErrorOf(await controller.DownloadExport(pending.Id, default)));
    }

    [Fact]
    public async Task Download_OfAnotherUsersExport_IsNotFound()
    {
        var context = CreateContext();
        var service = CreateService(context);
        var owner = await AddUserAsync(context, "owner");
        var intruder = await AddUserAsync(context, "intruder");
        var export = await AddExportAsync(context, owner.Id, DateTime.UtcNow.AddHours(10));

        var result = await CreateController(service, intruder.Id).DownloadExport(export.Id, default);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task ListingRequests_DoesNotLoadDataUrl_ButStillReportsDownloadAvailability()
    {
        var context = CreateContext();
        var service = CreateService(context);
        var user = await AddUserAsync(context, "listing");
        await AddExportAsync(context, user.Id, DateTime.UtcNow.AddHours(10));

        var listed = await service.GetRequestsAsync(user.Id);

        var row = Assert.Single(listed);
        Assert.Null(row.DataUrl);
        Assert.True(row.HasData);
    }

    private class FakeEmailService : IEmailService
    {
        public Task SendEmailVerificationAsync(string email, string username, string verificationToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPasswordResetAsync(string email, string username, string resetToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendMfaCodeAsync(string email, string username, string code, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendLoginTwoFactorCodeAsync(string email, string username, string code, string verifyUrl, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendSessionAlertAsync(string email, string username, string deviceInfo, string ipAddress, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDeviceProvisionedAsync(string email, string username, string deviceName, string deviceType, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCertificateExpiryWarningAsync(string email, string deviceName, DateTime expiryDate, int daysRemaining, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWelcomeEmailAsync(string email, string username, string tenantName, CancellationToken ct = default, Guid? tenantId = null) => Task.CompletedTask;
        public Task<bool> SendInvitationAsync(string email, string inviterName, string tenantName, string inviteToken, CancellationToken ct = default, Guid? tenantId = null) => Task.FromResult(true);
        public Task<bool> SendRawEmailAsync(string email, string subject, string htmlBody, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ValidateEmailAsync(string email, CancellationToken ct = default) => Task.FromResult(true);
    }
}
