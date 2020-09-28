﻿using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace Moonglade.Pingback
{
    public class PingbackReceiver : IPingbackReceiver
    {
        public ILogger<PingbackReceiver> Logger { get; set; }

        public int RemoteTimeout { get; set; }

        #region Events

        public delegate void PingSuccessHandler(object sender, PingSuccessEventArgs e);
        public event PingSuccessHandler OnPingSuccess;

        #endregion

        private string _remoteIpAddress;
        private string _sourceUrl;
        private string _targetUrl;

        public PingbackReceiver(ILogger<PingbackReceiver> logger = null)
        {
            Logger = logger;
            RemoteTimeout = 30;
        }

        public PingbackValidationResult ValidatePingRequest(string requestBody, string remoteIp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(requestBody))
                {
                    throw new ArgumentNullException(nameof(requestBody));
                }

                if (string.IsNullOrWhiteSpace(remoteIp))
                {
                    throw new ArgumentNullException(nameof(remoteIp));
                }

                Logger.LogInformation($"Receiving Pingback from {remoteIp}");
                Logger.LogInformation($"Pingback received xml: {requestBody}");

                if (!requestBody.Contains("<methodName>pingback.ping</methodName>"))
                {
                    return PingbackValidationResult.TerminatedMethodNotFound;
                }

                var doc = new XmlDocument();
                doc.LoadXml(requestBody);

                var list = doc.SelectNodes("methodCall/params/param/value/string") ??
                           doc.SelectNodes("methodCall/params/param/value");

                if (list == null)
                {
                    Logger.LogWarning("Could not find Pingback sourceUrl and targetUrl, request has been terminated.");
                    return PingbackValidationResult.TerminatedUrlNotFound;
                }

                _sourceUrl = list[0].InnerText.Trim();
                _targetUrl = list[1].InnerText.Trim();
                _remoteIpAddress = remoteIp;

                return PingbackValidationResult.ValidPingRequest;
            }
            catch (Exception e)
            {
                Logger.LogError(e, nameof(ValidatePingRequest));
                return PingbackValidationResult.GenericError;
            }
        }

        public async Task<PingRequest> GetPingRequest()
        {
            Logger.LogInformation($"Processing Pingback from: {_sourceUrl} to {_targetUrl}");
            var req = await ExamineSourceAsync();
            return req;
        }

        public PingbackResponse ReceivingPingback(PingRequest req, Func<bool> ifTargetResourceExists, Func<bool> ifAlreadyBeenPinged)
        {
            try
            {
                if (null == req)
                {
                    return PingbackResponse.InvalidPingRequest;
                }

                var ti = ifTargetResourceExists();
                if (!ti) return PingbackResponse.Error32TargetUriNotExist;

                var pd = ifAlreadyBeenPinged();
                if (pd) return PingbackResponse.Error48PingbackAlreadyRegistered;

                if (req.SourceDocumentInfo.SourceHasLink && !req.SourceDocumentInfo.ContainsHtml)
                {
                    Logger.LogInformation("Adding received pingback...");
                    var domain = GetDomain(_sourceUrl);

                    OnPingSuccess?.Invoke(this, new PingSuccessEventArgs(domain, req));
                    return PingbackResponse.Success;
                }

                if (!req.SourceDocumentInfo.SourceHasLink)
                {
                    Logger.LogError("Pingback error: The source URI does not contain a link to the target URI, and so cannot be used as a source.");
                    return PingbackResponse.Error17SourceNotContainTargetUri;
                }
                Logger.LogWarning("Spam detected on current Pingback...");
                return PingbackResponse.SpamDetectedFakeNotFound;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, nameof(ReceivingPingback));
                return PingbackResponse.GenericError;
            }
        }

        private async Task<PingRequest> ExamineSourceAsync()
        {
            try
            {
                var regexHtml = new Regex(
                    @"</?\w+((\s+\w+(\s*=\s*(?:"".*?""|'.*?'|[^'"">\s]+))?)+\s*|\s*)/?>",
                    RegexOptions.Singleline | RegexOptions.Compiled);

                var regexTitle = new Regex(
                    @"(?<=<title.*>)([\s\S]*)(?=</title>)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(RemoteTimeout) };
                var html = await httpClient.GetStringAsync(_sourceUrl);
                var title = regexTitle.Match(html).Value.Trim();
                Logger.LogInformation($"ExamineSourceAsync:title: {title}");

                var containsHtml = regexHtml.IsMatch(title);
                Logger.LogInformation($"ExamineSourceAsync:containsHtml: {containsHtml}");

                var sourceHasLink = html.ToUpperInvariant().Contains(_targetUrl.ToUpperInvariant());
                Logger.LogInformation($"ExamineSourceAsync:sourceHasLink: {sourceHasLink}");

                var pingRequest = new PingRequest
                {
                    SourceDocumentInfo = new SourceDocumentInfo
                    {
                        Title = title,
                        ContainsHtml = containsHtml,
                        SourceHasLink = sourceHasLink
                    },
                    TargetUrl = _targetUrl,
                    SourceUrl = _sourceUrl,
                    SourceIpAddress = _remoteIpAddress
                };

                return pingRequest;
            }
            catch (WebException ex)
            {
                Logger.LogError(ex, nameof(ExamineSourceAsync));
                return new PingRequest
                {
                    SourceDocumentInfo = new SourceDocumentInfo
                    {
                        SourceHasLink = false
                    },
                    SourceUrl = _sourceUrl,
                    SourceIpAddress = _remoteIpAddress
                };
            }
        }

        private static string GetDomain(string sourceUrl)
        {
            var start = sourceUrl.IndexOf("://", StringComparison.Ordinal) + 3;
            var stop = sourceUrl.IndexOf("/", start, StringComparison.Ordinal);
            return sourceUrl[start..stop].Replace("www.", string.Empty);
        }
    }
}
