﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rwb.Luxopus.Jobs;
using System;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;

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
        void SendPlanEmail(Plan plan, string notes);
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
            if (string.IsNullOrEmpty(Settings.Server)) { return; }
            if (string.IsNullOrEmpty(body)) { return; }
            try
            {
                using (SmtpClient client = new SmtpClient(Settings.Server)
                {
                    UseDefaultCredentials = false,
                    Port = 587,
                    EnableSsl = true,
                    Credentials = new NetworkCredential(Settings.Username, Settings.Password)
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

        public void SendPlanEmail(Plan plan, string notes)
        {
            StringBuilder message = new StringBuilder();
            foreach (PeriodPlan p in plan.Plans.OrderBy(z => z.Start))
            {
                message.AppendLine(p.ToString());
            }

            SendEmail($"Solar strategy " + plan.Plans.First().Start.ToString("dd MMM"), message.ToString() + Environment.NewLine + Environment.NewLine + notes);
            Logger.LogInformation($"Planner '{this.GetType().Name}' created new plan: " + Environment.NewLine + message.ToString() + Environment.NewLine + notes);
        }
    }
}
