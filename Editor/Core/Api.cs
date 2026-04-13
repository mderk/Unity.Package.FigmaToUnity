using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Figma
{
    using Internals;

    internal abstract class Api : IDisposable
    {
        #region Consts
        const int maxRetries = 3;
        const int defaultRetrySeconds = 10;
        #endregion

        #region Fields
        protected readonly string fileKey;
        protected readonly HttpClient httpClient;
        #endregion

        #region Constructors
        protected Api(string personalAccessToken, string fileKey)
        {
            this.fileKey = fileKey;
            httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("X-FIGMA-TOKEN", personalAccessToken);
        }
        #endregion

        #region Methods
        void IDisposable.Dispose() => httpClient.Dispose();
        #endregion

        #region Support Methods
        protected async Task<T> ConvertOnBackgroundAsync<T>(string json, CancellationToken token) where T : class => await Task.Run(() => Task.FromResult(JsonUtility.FromJson<T>(json)), token);
        protected async Task<T> GetAsync<T>(string get, CancellationToken token = default) where T : class => await ConvertOnBackgroundAsync<T>(await GetJsonAsync(get, token), token);
        protected async Task<string> GetJsonAsync(string get, CancellationToken token = default) => await HttpGetAsync($"{Internals.Const.api}/{get}", token);
        async Task<string> HttpGetAsync(string url, CancellationToken token = default)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                HttpResponseMessage response = await httpClient.SendAsync(request, token);

                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadAsStringAsync();

                if (response.StatusCode == (HttpStatusCode)429 && attempt < maxRetries)
                {
                    int retryAfter = defaultRetrySeconds;
                    if (response.Headers.TryGetValues("Retry-After", out var values) && int.TryParse(values.First(), out int parsed))
                        retryAfter = parsed;

                    Debug.LogWarning($"[FigmaToUnity] Rate limited. Retrying in {retryAfter}s (attempt {attempt + 1}/{maxRetries})...");
                    await Task.Delay(retryAfter * 1000, token);
                    continue;
                }

                throw new HttpRequestException($"{HttpMethod.Get} {url} {response.StatusCode.ToString()}");
            }

            throw new HttpRequestException($"{HttpMethod.Get} {url} TooManyRequests (exhausted {maxRetries} retries)");
        }
        #endregion
    }
}