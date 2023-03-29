using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Luxopus
{
    internal class LuxService : IDisposable
    {
        private readonly LuxSettings _Settings;
        private readonly CookieContainer _CookieContainer;

        public LuxService(IOptions<LuxSettings> settings)
        {
            _Settings = settings.Value;
            _CookieContainer = new CookieContainer();
        }

        private async Task<HttpResponseMessage> PostAsync(string path, KeyValuePair<string, string>[] formData)
        {
            using (HttpClientHandler handler = new HttpClientHandler() { CookieContainer = _CookieContainer })
            using (HttpClient client = new HttpClient(handler) { BaseAddress = new Uri(_Settings.BaseAddress) })
            {
                FormUrlEncodedContent content = new FormUrlEncodedContent(formData);
                return await client.PostAsync(path, content);
            }

        }

        private async Task LoginAsync()
        {
            HttpResponseMessage result = await PostAsync("/WManage/web/login", new[]
            {
                    new KeyValuePair<string, string>("account", _Settings.Username),
                    new KeyValuePair<string, string>("password", _Settings.Password),
                });
            result.EnsureSuccessStatusCode();
        }
    }
}
