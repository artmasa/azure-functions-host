﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.WebHost.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Management.LinuxSpecialization
{
    public class PackageDownloadHandler : IPackageDownloadHandler
    {
        public const int AriaDownloadThreshold = 100 * 1024 * 1024;
        public const string Aria2CExecutable = "aria2c";
        private const string StorageBlobDownloadApiVersion = "2019-12-12";

        private readonly HttpClient _httpClient;
        private readonly IManagedIdentityTokenProvider _managedIdentityTokenProvider;
        private readonly IBashCommandHandler _bashCommandHandler;
        private readonly ILogger<PackageDownloadHandler> _logger;
        private readonly IMetricsLogger _metricsLogger;

        public PackageDownloadHandler(HttpClient httpClient, IManagedIdentityTokenProvider managedIdentityTokenProvider,
            IBashCommandHandler bashCommandHandler, ILogger<PackageDownloadHandler> logger,
            IMetricsLogger metricsLogger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _managedIdentityTokenProvider = managedIdentityTokenProvider ?? throw new ArgumentNullException(nameof(managedIdentityTokenProvider));
            _bashCommandHandler = bashCommandHandler ?? throw new ArgumentNullException(nameof(bashCommandHandler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsLogger = metricsLogger ?? throw new ArgumentNullException(nameof(metricsLogger));
        }

        public async Task<string> Download(RunFromPackageContext pkgContext)
        {
            if (Utility.TryCleanUrl(pkgContext.Url, out var cleanedUrl))
            {
                _logger.LogDebug("Downloading app contents from '{cleanedUrl}'", cleanedUrl);
            }
            else
            {
                throw new InvalidOperationException("Invalid url for the package");
            }

            var isWarmupRequest = pkgContext.IsWarmUpRequest;
            var needsManagedIdentityToken = !isWarmupRequest && await IsAuthenticationTokenNecessary(pkgContext.Url);
            _logger.LogDebug(
                "{nameof(PackageDownloadHandler)}: Needs ManagedIdentity Token = '{needsManagedIdentityToken}' IsWarmupRequest = '{isWarmupRequest}'",
                nameof(PackageDownloadHandler), needsManagedIdentityToken, isWarmupRequest);

            var zipUri = new Uri(pkgContext.Url);
            var token = needsManagedIdentityToken
                ? await _managedIdentityTokenProvider.GetManagedIdentityToken(zipUri.AbsoluteUri)
                : string.Empty;

            return await Download(pkgContext, zipUri, token);
        }

        private async Task<string> Download(RunFromPackageContext pkgContext, Uri zipUri, string token)
        {
            if (pkgContext.IsWarmUpRequest && !string.IsNullOrEmpty(token))
            {
                throw new Exception("Warmup requests do not support ManagedIdentity token");
            }

            var tmpPath = Path.GetTempPath();
            var fileName = Path.GetFileName(zipUri.AbsolutePath);
            var filePath = Path.Combine(tmpPath, fileName);

            string downloadMetricName;
            if (!string.IsNullOrEmpty(token))
            {
                downloadMetricName = MetricEventNames.LinuxContainerSpecializationZipDownloadUsingManagedIdentity;
            }
            else
            {
                downloadMetricName = pkgContext.IsWarmUpRequest
                    ? MetricEventNames.LinuxContainerSpecializationZipDownloadWarmup
                    : MetricEventNames.LinuxContainerSpecializationZipDownload;
            }

            var tokenPrefix = token == null ? "Null" : token.Substring(0, Math.Min(token.Length, 3));

            // Aria download doesn't support MI Token or warmup requests
            if (pkgContext.PackageContentLength != null && pkgContext.PackageContentLength > AriaDownloadThreshold && string.IsNullOrEmpty(token) && !pkgContext.IsWarmUpRequest)
            {
                _logger.LogDebug(
                    "Downloading zip contents using aria2c. IsWarmupRequest = '{pkgContext.IsWarmUpRequest}'. Managed Identity TokenPrefix = '{tokenPrefix}'",
                    pkgContext.IsWarmUpRequest, tokenPrefix);
                AriaDownload(tmpPath, fileName, zipUri, pkgContext.IsWarmUpRequest, downloadMetricName);
            }
            else
            {
                _logger.LogDebug(
                    "Downloading zip contents using httpclient. IsWarmupRequest = '{pkgContext.IsWarmUpRequest}'. Managed Identity TokenPrefix = '{tokenPrefix}'",
                    pkgContext.IsWarmUpRequest, tokenPrefix);
                await HttpClientDownload(filePath, zipUri, pkgContext.IsWarmUpRequest, token, downloadMetricName);
            }

            return filePath;
        }

        private void AriaDownload(string directory, string fileName, Uri zipUri, bool isWarmupRequest, string downloadMetricName)
        {
            (string stdout, string stderr, int exitCode) = _bashCommandHandler.RunBashCommand(
                $"{Aria2CExecutable} --allow-overwrite -x12 -d {directory} -o {fileName} '{zipUri}'",
                downloadMetricName);
            if (exitCode != 0)
            {
                var msg = $"Error downloading package. stdout: {stdout}, stderr: {stderr}, exitCode: {exitCode}";
                _logger.LogError(msg);
                throw new InvalidOperationException(msg);
            }
            var fileInfo = FileUtility.FileInfoFromFileName(Path.Combine(directory, fileName));
            _logger.LogInformation("'{fileInfo.Length}' bytes downloaded. IsWarmupRequest = '{isWarmupRequest}'",
                fileInfo.Length, isWarmupRequest);
        }

        private async Task HttpClientDownload(string filePath, Uri zipUri, bool isWarmupRequest, string token, string downloadMetricName)
        {
            HttpResponseMessage response = null;
            await Utility.InvokeWithRetriesAsync(async () =>
            {
                try
                {
                    using (_metricsLogger.LatencyEvent(downloadMetricName))
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, zipUri);

                        if (!string.IsNullOrEmpty(token))
                        {
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                            request.Headers.Add(ScriptConstants.AzureVersionHeader, StorageBlobDownloadApiVersion);
                        }

                        response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                        response.EnsureSuccessStatusCode();
                    }
                }
                catch (Exception e)
                {
                    const string error = "Error downloading zip content";
                    _logger.LogError(e, error);
                    throw;
                }

                _logger.LogInformation(
                    "'{response.Content.Headers.ContentLength}' bytes downloaded. IsWarmupRequest = '{isWarmupRequest}'",
                    response.Content.Headers.ContentLength, isWarmupRequest);
            }, 2, TimeSpan.FromSeconds(0.5));

            using (_metricsLogger.LatencyEvent(isWarmupRequest ? MetricEventNames.LinuxContainerSpecializationZipWriteWarmup : MetricEventNames.LinuxContainerSpecializationZipWrite))
            {
                using (var content = await response.Content.ReadAsStreamAsync())
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
                {
                    await content.CopyToAsync(stream);
                }

                _logger.LogInformation(
                    "'{response.Content.Headers.ContentLength}' bytes written. IsWarmupRequest = '{isWarmupRequest}'",
                    response.Content.Headers.ContentLength, isWarmupRequest);
            }
        }

        private async Task<bool> IsAuthenticationTokenNecessary(string resourceUrl)
        {
            if (!Uri.TryCreate(resourceUrl, UriKind.Absolute, out var resourceUri))
            {
                _logger.LogDebug("Token retrieval not required since site content url is invalid");
                return false;
            }

            if (!Utility.IsResourceAzureBlobWithoutSas(resourceUri))
            {
                _logger.LogDebug("Token retrieval not required because site content is a SAS Azure Blob URL.");
                return false;
            }

            if (await IsResourceAccessibleWithoutAuthorization(resourceUrl))
            {
                _logger.LogDebug("Token retrieval not required because site content zip is publicly accessible.");
                return false;
            }

            return true;
        }

        private async Task<bool> IsResourceAccessibleWithoutAuthorization(string resourceUrl)
        {
            try
            {
                using (var cts = new CancellationTokenSource())
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    using (var httpRequest = new HttpRequestMessage(HttpMethod.Head, resourceUrl))
                    {
                        using (var httpResponse = await _httpClient.SendAsync(httpRequest, cts.Token))
                        {
                            return httpResponse.StatusCode == HttpStatusCode.OK;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, nameof(IsResourceAccessibleWithoutAuthorization));
                return false;
            }
        }
    }
}
