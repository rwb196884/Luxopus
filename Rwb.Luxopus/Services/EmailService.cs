using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Net;
using System.Net.Mail;

namespace Rwb.Luxopus.Services
{
    public class EmailSettings : Settings
    {
        public string Server { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string MailFrom { get; set; }
        public string MailTo { get; set; }
    }

    public interface IEmailService
    {
        void SendEmail(string subject, string body);
    }

    public class EmailService : Service<EmailSettings>, IEmailService
    {
        public EmailService(ILogger<EmailService> logger, IOptions<EmailSettings> settings) : base(logger, settings) { }

        public override bool ValidateSettings()
        {
            bool ok = true;

            if(string.IsNullOrEmpty(Settings.Server))
            {
                Logger.LogWarning("Email is disabled because setting Email.Server is empty.");
            }
            else
            {
                if (string.IsNullOrEmpty(Settings.Username))
                {
                    Logger.LogError("Setting Email.Username is required. (To disable email set Email.Server to emtpty.)");
                    ok = false;
                }
                if (string.IsNullOrEmpty(Settings.Username))
                {
                    Logger.LogError("Setting Email.Username is required. (To disable email set Email.Server to emtpty.)");
                    ok = false;
                }
                if (string.IsNullOrEmpty(Settings.MailFrom))
                {
                    Logger.LogError("Setting Email.MailFrom is required. (To disable email set Email.Server to emtpty.)");
                    ok = false;
                }
                if (string.IsNullOrEmpty(Settings.MailTo))
                {
                    Logger.LogError("Setting Email.MailTo is required. (To disable email set Email.Server to emtpty.)");
                    ok = false;
                }
                if (string.IsNullOrEmpty(Settings.Username))
                {
                    Logger.LogError("Setting Email.Username is required. (To disable email set Email.Server to emtpty.)");
                    ok = false;
                }
            }

            return ok;
        }

        public void SendEmail(string subject, string body)
        {
            if (string.IsNullOrEmpty(body)) { return; }
            if (string.IsNullOrEmpty(Settings.Password)) { return; }
            try
            {
                using (SmtpClient client = new SmtpClient(Settings.Server)
                {
                    UseDefaultCredentials = false,
                    Port = 587,
                    EnableSsl = true,
                    Credentials = new NetworkCredential(Settings.MailTo, Settings.Password)
                })
                {
                    MailMessage mailMessage = new MailMessage()
                    {
                        From = new MailAddress(Settings.MailFrom),
                        Subject = subject,
                        IsBodyHtml = true,
                    };
                    mailMessage.To.Add(Settings.MailTo);
                    mailMessage.Body = "<pre style='font-family: Lucida Console, Courier' >" + body + "</pre>";

                    //AlternateView plainText = AlternateView.CreateAlternateViewFromString("seven bits transferencoding test", Encoding.ASCII, "text/plain");
                    //plainText.TransferEncoding = System.Net.Mime.TransferEncoding.SevenBit;
                    //mailMessage.AlternateViews.Add(plainText);

                    client.Send(mailMessage);
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Failed to send e-mail: " + e.Message);
            }
        }
    }
}
