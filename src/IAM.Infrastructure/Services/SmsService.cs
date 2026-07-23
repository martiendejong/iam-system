using System.Net.Http.Headers;
using System.Text;
using IAM.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IAM.Infrastructure.Services;

public class SmsService : ISmsService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SmsSettings _settings;
    private readonly ILogger<SmsService> _logger;

    public SmsService(
        IHttpClientFactory httpClientFactory,
        IOptions<SmsSettings> settings,
        ILogger<SmsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<bool> SendSmsAsync(string phoneNumber, string message)
    {
        if (!_settings.EnableSending)
        {
            _logger.LogInformation(
                "[SmsService DEV MODE] To: {PhoneNumber} | Message: {Message}",
                phoneNumber, message);
            return true;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("TwilioSms");

            var url = $"https://api.twilio.com/2010-04-01/Accounts/{_settings.AccountSid}/Messages.json";

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "To", phoneNumber },
                { "From", _settings.FromNumber },
                { "Body", message }
            });

            // Basic auth: AccountSid:AuthToken
            var authBytes = Encoding.ASCII.GetBytes($"{_settings.AccountSid}:{_settings.AuthToken}");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

            var response = await client.PostAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("SMS sent successfully to {PhoneNumber}", phoneNumber);
                return true;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning(
                "Failed to send SMS to {PhoneNumber}. Status: {StatusCode}, Response: {Response}",
                phoneNumber, response.StatusCode, responseBody);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending SMS to {PhoneNumber}", phoneNumber);
            return false;
        }
    }
}
