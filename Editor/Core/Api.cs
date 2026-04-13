using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
        const int maxRetrySeconds = 30;
        #endregion

        #region Fields
        protected readonly string fileKey;
        protected readonly HttpClient httpClient;
        protected Action<string> onProgress;
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
                onProgress?.Invoke("Connecting...");

                using HttpRequestMessage request = new(HttpMethod.Get, url);
                HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

                if (response.IsSuccessStatusCode)
                {
                    long? contentLength = response.Content.Headers.ContentLength;
                    using Stream stream = await response.Content.ReadAsStreamAsync();
                    StringBuilder sb = new();
                    byte[] buffer = new byte[8192];
                    long totalRead = 0;
                    int bytesRead;

                    while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                    {
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                        totalRead += bytesRead;

                        if (contentLength.HasValue && contentLength.Value > 0)
                            onProgress?.Invoke($"Downloading... {totalRead / 1024}KB / {contentLength.Value / 1024}KB");
                        else
                            onProgress?.Invoke($"Downloading... {totalRead / 1024}KB");
                    }

                    onProgress?.Invoke("Download complete.");
                    return sb.ToString();
                }

                if (response.StatusCode == (HttpStatusCode)429 && attempt < maxRetries)
                {
                    int retryAfter = defaultRetrySeconds;
                    if (response.Headers.TryGetValues("Retry-After", out var values) && int.TryParse(values.First(), out int parsed) && parsed > 0)
                        retryAfter = Math.Min(parsed, maxRetrySeconds);

                    string msg = $"Rate limited. Retrying in {retryAfter}s (attempt {attempt + 1}/{maxRetries})...";
                    Debug.LogWarning($"[FigmaToUnity] {msg}");
                    onProgress?.Invoke(msg);
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
