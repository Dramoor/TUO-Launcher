﻿using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TazUO_Launcher.Utility
{
    class UpdateManager
    {
        private const string UPDATE_ZIP_URL = "https://github.com/bittiez/ClassicUO/releases/latest/download/ClassicUO.zip";

        public static UpdateManager Instance { get; private set; } = new UpdateManager();
        public bool DownloadInProgress { get; private set; } = false;
        public Version RemoteVersion { get; private set; } = null;
        public Version LocalVersion { get; private set; } = null;
        public GitHubReleaseData MainReleaseData { get; private set; } = null;

        private static HttpClient httpClient = new HttpClient();

        /// <summary>
        /// Download the most recent version of TazUO
        /// </summary>
        /// <param name="action">This method is called using the ui dispatcher</param>
        /// <param name="afterCompleted">This method is called on the download thread</param>
        /// <returns></returns>
        public Task DownloadTUO(Action<int>? action = null, Action afterCompleted = null)
        {
            if (DownloadInProgress)
            {
                return Task.CompletedTask;
            }

            DownloadProgress downloadProgress = new DownloadProgress();

            downloadProgress.DownloadProgressChanged += (s, e) =>
            {
                Utility.UIDispatcher.InvokeAsync(() =>
                {
                    action?.Invoke((int)(downloadProgress.ProgressPercentage * 100));
                });
            };

            Task download = Task.Factory.StartNew(() =>
            {
                string tempFilePath = Path.GetTempFileName();
                using (var file = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    if (MainReleaseData != null)
                    {
                        foreach (GitHubReleaseData.Asset asset in MainReleaseData.assets)
                        {
                            if (
                                asset.name.EndsWith(".zip") &&
                                (asset.name.StartsWith("ClassicUO") || asset.name.StartsWith("TazUO"))
                                )
                            {
                                httpClient.DownloadAsync(asset.browser_download_url, file, downloadProgress).Wait();
                            }
                        }
                    }
                    else
                    {
                        httpClient.DownloadAsync(UPDATE_ZIP_URL, file, downloadProgress).Wait();
                    }
                }

                try
                {
                    ZipFile.ExtractToDirectory(tempFilePath, Path.Combine(LauncherSettings.LauncherPath, "TazUO"), true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }

                afterCompleted?.Invoke();
                DownloadInProgress = false;

            });

            return download;
        }

        public void GetRemoteVersionAsync(Action? onVersionFound = null)
        {
            Task.Factory.StartNew(() =>
            {
                HttpRequestMessage restApi = new HttpRequestMessage()
                {
                    Method = HttpMethod.Get,
                    RequestUri = new Uri("https://api.github.com/repos/bittiez/TazUO/releases/latest"),
                };
                restApi.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                restApi.Headers.Add("User-Agent", "Public");
                string jsonResponse = httpClient.Send(restApi).Content.ReadAsStringAsync().Result;

                Console.WriteLine(jsonResponse);

                MainReleaseData = JsonSerializer.Deserialize<GitHubReleaseData>(jsonResponse);

                if (MainReleaseData != null)
                {
                    if (MainReleaseData.tag_name.StartsWith("v"))
                    {
                        MainReleaseData.tag_name = MainReleaseData.tag_name.Substring(1);
                    }

                    if (Version.TryParse(MainReleaseData.tag_name, out var version))
                    {
                        RemoteVersion = version;
                        Utility.UIDispatcher.InvokeAsync(() =>
                        {
                            onVersionFound?.Invoke();
                        });
                    }
                }
            });
        }

        public Version? GetInstalledVersion(string exePath)
        {
            return LocalVersion = AssemblyName.GetAssemblyName(exePath).Version;
        }

        public class DownloadProgress : IProgress<float>
        {
            public event EventHandler DownloadProgressChanged;

            public float ProgressPercentage { get; set; }

            public void Report(float value)
            {
                ProgressPercentage = value;
                DownloadProgressChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public static class HttpClientExtensions
    {
        public static async Task DownloadAsync(this HttpClient client, string requestUri, Stream destination, IProgress<float> progress = null, CancellationToken cancellationToken = default)
        {
            // Get the http headers first to examine the content length
            using (var response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead))
            {
                var contentLength = response.Content.Headers.ContentLength;

                using (var download = await response.Content.ReadAsStreamAsync())
                {

                    // Ignore progress reporting when no progress reporter was 
                    // passed or when the content length is unknown
                    if (progress == null || !contentLength.HasValue)
                    {
                        await download.CopyToAsync(destination);
                        return;
                    }

                    // Convert absolute progress (bytes downloaded) into relative progress (0% - 100%)
                    var relativeProgress = new Progress<long>(totalBytes => progress.Report((float)totalBytes / contentLength.Value));
                    // Use extension method to report progress while downloading
                    await download.CopyToAsync(destination, 81920, relativeProgress, cancellationToken);
                    progress.Report(1);
                }
            }
        }
    }

    public static class StreamExtensions
    {
        public static async Task CopyToAsync(this Stream source, Stream destination, int bufferSize, IProgress<long> progress = null, CancellationToken cancellationToken = default)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (!source.CanRead)
                throw new ArgumentException("Has to be readable", nameof(source));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (!destination.CanWrite)
                throw new ArgumentException("Has to be writable", nameof(destination));
            if (bufferSize < 0)
                throw new ArgumentOutOfRangeException(nameof(bufferSize));

            var buffer = new byte[bufferSize];
            long totalBytesRead = 0;
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) != 0)
            {
                await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                totalBytesRead += bytesRead;
                progress?.Report(totalBytesRead);
            }
        }
    }
}
