using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MobileShop.Models;
using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace MobileShop.Services
{
    public interface IEmailService
    {
        Task SendEmailAsync(string to, string subject, string body, bool isHtml = true);
        Task SendOrderConfirmationAsync(string to, string orderNumber, decimal total);
        Task SendOrderStatusUpdateAsync(string to, string orderNumber, string status);
    }

    public class EmailService : IEmailService
    {
        private readonly IOptions<EmailSettings> _emailSettings;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IOptions<EmailSettings> emailSettings, ILogger<EmailService> logger)
        {
            _emailSettings = emailSettings;
            _logger = logger;
        }

        public async Task SendEmailAsync(string to, string subject, string body, bool isHtml = true)
        {
            try
            {
                var settings = _emailSettings.Value;
                if (string.IsNullOrWhiteSpace(settings.Host) || settings.Port < 1 || settings.Port > 65535 ||
                    string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password) ||
                    string.IsNullOrWhiteSpace(settings.FromEmail))
                {
                    _logger.LogError("Failed to send email: EmailSettings must include Host, a valid Port, Username, Password, and FromEmail.");
                    return;
                }

                using var client = new SmtpClient(settings.Host, settings.Port)
                {
                    EnableSsl = true,
                    UseDefaultCredentials = false,
                    Credentials = new NetworkCredential(settings.Username, settings.Password)
                };

                using var message = new MailMessage();
                message.From = new MailAddress(settings.FromEmail, settings.FromName);
                message.To.Add(to);
                message.Subject = subject;
                message.Body = body;
                message.IsBodyHtml = isHtml;

                await client.SendMailAsync(message);
                _logger.LogInformation("Email sent to {Recipient}", to);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to send email. Error type: {ErrorType}; SMTP status: {SmtpStatus}; underlying error type: {InnerErrorType}.",
                    ex.GetType().Name, (ex as SmtpException)?.StatusCode.ToString() ?? "not available",
                    ex.InnerException?.GetType().Name ?? "none");
            }
        }

        public async Task SendOrderConfirmationAsync(string to, string orderNumber, decimal total)
        {
            var subject = $"Order Confirmation - {orderNumber}";
            var body = $@"
                <h2>Thank you for your order!</h2>
                <p>Your order <strong>{orderNumber}</strong> has been placed successfully.</p>
                <p>Total Amount: <strong>MMK {total:N2}</strong></p>
                <p>We will notify you once your order is shipped.</p>";

            await SendEmailAsync(to, subject, body);
        }

        public async Task SendOrderStatusUpdateAsync(string to, string orderNumber, string status)
        {
            var subject = $"Order Status Update - {orderNumber}";
            var body = $@"
                <h2>Order Status Update</h2>
                <p>Your order <strong>{orderNumber}</strong> status has been updated to: <strong>{status}</strong></p>
                <p>You can track your order on our website.</p>";

            await SendEmailAsync(to, subject, body);
        }
    }
}