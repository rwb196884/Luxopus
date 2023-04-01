using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace Rwb.Luxopus.Services
{
    // TODO: Add to DI.

    public interface ISmsService
    {
        void SendSms(string message);
    }

    public class SmsSettings : Settings
    {
        public string Number { get; set; }
        public string GatewayAddress { get; set; }
        public string Password { get; set; }
    }
    

    public class SmsService : Service<SmsSettings>, ISmsService
    {
        public SmsService(ILogger<SmsService> logger, IOptions<SmsSettings> settings) : base(logger, settings) { }

        public override bool ValidateSettings()
        {
            return true;
        }

        public void SendSms(string message)
        {
            if(string.IsNullOrEmpty(Settings.Number)) { return; }
            try
            {
                using (HttpClient c = new HttpClient())
                {
                    c.DefaultRequestHeaders.Add("Authorization", Settings.Password);
                    c.Timeout = TimeSpan.FromSeconds(12);
                    Task<HttpResponseMessage> rq = c.PostAsync(Settings.GatewayAddress, new StringContent(
                        JsonConvert.SerializeObject(new Dictionary<string, string>() {
                        { "to", Settings.Number},
                        {"message", message }
                        })));
                    rq.Wait();
                    HttpResponseMessage msg = rq.Result;
                    if (msg.StatusCode != System.Net.HttpStatusCode.OK)
                    {
                        Task<String> content = msg.Content.ReadAsStringAsync();
                        content.Wait();
                        throw new System.Exception($"Text message response. Status {msg.StatusCode}. Body: {content.Result}");
                    }
                }
            }
            catch (System.Exception e)
            {
                Logger.LogError(e, "Failed to send text messsage:");
            }
        }

    }
}
