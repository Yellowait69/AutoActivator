using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoActivator.Services
{
    /// <summary>
    /// Service responsible for communicating with the Micro Focus ESCWA REST API.
    /// Handles authentication, node failover, JCL submission, status polling and
    /// diagnostic spool retrieval.
    /// </summary>
    public sealed class MicroFocusApiService
    {
        private static readonly CookieContainer CookieContainer =
            new CookieContainer();

        private static readonly ConcurrentDictionary<string, string>
            LastWorkingServers =
                new ConcurrentDictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, DateTime>
            LastVerifiedSessionUtc =
                new ConcurrentDictionary<string, DateTime>(
                    StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, string>
            SubmittedJobNodes =
                new ConcurrentDictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

        private static readonly HttpClient HttpClient;

        private static readonly Regex JobNumRegex =
            new Regex(
                @"\bJOBNUM\s*=\s*[Jj]?(?<number>\d{5,10})\b",
                RegexOptions.Compiled |
                RegexOptions.IgnoreCase);

        private static readonly Regex FallbackJobNumRegex =
            new Regex(
                @"\b[Jj](?<number>\d{5,10})\b",
                RegexOptions.Compiled |
                RegexOptions.IgnoreCase);

        private static readonly Regex JobNameRegex =
            new Regex(
                @"(?m)^\s*//(?<name>[A-Z0-9$#@]{1,8})\s+JOB\b",
                RegexOptions.Compiled |
                RegexOptions.IgnoreCase);

        private static readonly Regex JobEndedRegex =
            new Regex(
                @"JCLCM0182I\s+JOB\s+ENDED\s+-\s+COND\s+CODE\s+0*(?<rc>[0-9]{1,4})\b",
                RegexOptions.Compiled |
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);

        private readonly Dictionary<string, List<string>> _activeServers =
            new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase)
            {
                {
                    "D",
                    new List<string>
                    {
                        "sdmfas01",
                        "sdmfas03",
                        "sdmfas05"
                    }
                },
                {
                    "Q",
                    new List<string>
                    {
                        "sqmfas06",
                        "sqmfas08",
                        "sqmfas10",
                        "sqmfas02",
                        "sqmfas04"
                    }
                },
                {
                    "A",
                    new List<string>
                    {
                        "samfas04",
                        "samfas06",
                        "samfas02"
                    }
                },
                {
                    "P",
                    new List<string>
                    {
                        "spmfas07",
                        "spmfas09",
                        "spmfas01",
                        "spmfas03",
                        "spmfas05",
                        "spmfas11",
                        "spmfas13"
                    }
                }
            };

        private static string _nodeUrl = string.Empty;

        public static string ActiveServer { get; private set; } =
            string.Empty;

        public static string ActiveEnvironment { get; private set; } =
            string.Empty;

        public static string ActiveRegion { get; private set; } =
            string.Empty;

        public static string ActiveNodeUrl { get; private set; } =
            string.Empty;

        public static string LastSubmissionResponse { get; private set; } =
            string.Empty;

        static MicroFocusApiService()
        {
            var handler = new HttpClientHandler
            {
                CookieContainer = CookieContainer,
                UseCookies = true,
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate,
                ServerCertificateCustomValidationCallback =
                    (_, _, _, _) => true,
                MaxConnectionsPerServer = 500
            };

            HttpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            HttpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "accept-encoding",
                "gzip, deflate");
            HttpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "sec-fetch-dest",
                "empty");
            HttpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "sec-fetch-mode",
                "cors");
            HttpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "sec-fetch-site",
                "same-origin");
            HttpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "accept-language",
                "en-BE");
            HttpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "x-requested-with",
                "XMLHttpRequest");
            HttpClient.DefaultRequestHeaders.Accept.ParseAdd(
                "application/json, text/plain, */*");
        }

        /// <summary>
        /// Authenticates against ESCWA and selects a working native region.
        /// </summary>
        public async Task<bool> LogonAsync(
            string username,
            string password,
            string env,
            Action<string>? onProgress,
            CancellationToken cancellationToken)
        {
            string environment = NormalizeEnvironment(env);

            if (!_activeServers.TryGetValue(
                    environment,
                    out List<string>? servers))
            {
                throw new ArgumentException(
                    $"Unknown environment: {environment}",
                    nameof(env));
            }

            string baseUrl =
                $"https://escwa{environment.ToLowerInvariant()}" +
                ".aginsurance.intranet:10086";

            string logonUrl =
                $"{baseUrl}/logon";

            ActiveEnvironment = environment;
            ActiveRegion = ResolveBatchRegion(environment);

            if (LastWorkingServers.TryGetValue(
                    environment,
                    out string? lastServer))
            {
                ConfigureActiveNode(
                    baseUrl,
                    lastServer,
                    environment);

                IEnumerable<Cookie> cookies =
                    CookieContainer
                        .GetCookies(new Uri(logonUrl))
                        .Cast<Cookie>();

                Cookie? esAdminCookie =
                    cookies.FirstOrDefault(
                        cookie =>
                            cookie.Name.Equals(
                                "esadmin-cookie",
                                StringComparison.OrdinalIgnoreCase));

                if (IsSessionCookieValid(esAdminCookie))
                {
                    if (LastVerifiedSessionUtc.TryGetValue(
                            environment,
                            out DateTime lastCheck) &&
                        (DateTime.UtcNow - lastCheck).TotalMinutes < 3d)
                    {
                        onProgress?.Invoke(
                            "Active ESCWA session reused | " +
                            $"Server={ActiveServer} | " +
                            $"Region={ActiveRegion}.");

                        return true;
                    }

                    if (await TestConnectionAsync(
                            _nodeUrl,
                            cancellationToken)
                        .ConfigureAwait(false))
                    {
                        LastVerifiedSessionUtc[environment] =
                            DateTime.UtcNow;

                        onProgress?.Invoke(
                            "Existing ESCWA session verified | " +
                            $"Server={ActiveServer} | " +
                            $"Region={ActiveRegion}.");

                        return true;
                    }
                }
            }

            string lastErrorMessage =
                string.Empty;

            string jsonPayload =
                JsonConvert.SerializeObject(
                    new
                    {
                        mfUser = username ?? string.Empty,
                        mfNewPassword = string.Empty,
                        mfPassword = password ?? string.Empty
                    });

            foreach (string server in servers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ConfigureActiveNode(
                    baseUrl,
                    server,
                    environment);

                try
                {
                    using var request =
                        CreateHttpRequest(
                            HttpMethod.Post,
                            logonUrl,
                            logonUrl);

                    request.Content =
                        new StringContent(
                            jsonPayload,
                            Encoding.UTF8,
                            "application/json");

                    using HttpResponseMessage response =
                        await HttpClient.SendAsync(
                                request,
                                cancellationToken)
                            .ConfigureAwait(false);

                    string responseBody =
                        await response.Content
                            .ReadAsStringAsync()
                            .ConfigureAwait(false);

                    if (response.StatusCode ==
                            HttpStatusCode.Unauthorized ||
                        response.StatusCode ==
                            HttpStatusCode.Forbidden)
                    {
                        throw new UnauthorizedAccessException(
                            "Incorrect password. Immediate stop " +
                            "to prevent account lockout.");
                    }

                    if (response.IsSuccessStatusCode &&
                        await TestConnectionAsync(
                                _nodeUrl,
                                cancellationToken)
                            .ConfigureAwait(false))
                    {
                        LastWorkingServers[environment] =
                            ActiveServer;
                        LastVerifiedSessionUtc[environment] =
                            DateTime.UtcNow;

                        onProgress?.Invoke(
                            "ESCWA connection established | " +
                            $"Server={ActiveServer} | " +
                            $"Region={ActiveRegion}.");

                        return true;
                    }

                    lastErrorMessage =
                        responseBody;

                    onProgress?.Invoke(
                        $"Server {ActiveServer} ignored " +
                        $"(HTTP {(int)response.StatusCode}).");
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (UnauthorizedAccessException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastErrorMessage =
                        exception.Message;

                    Debug.WriteLine(
                        "[ESCWA LOGON ERROR] " +
                        $"Server={ActiveServer} | " +
                        $"Region={ActiveRegion} | " +
                        $"Error={exception}");
                }
            }

            throw new InvalidOperationException(
                $"No ESCWA server responded for environment " +
                $"{environment}. Last error: {lastErrorMessage}");
        }

        public async Task<(bool Success, string JobNum, string Error)>
            SubmitJobAsync(
                string jclContent,
                CancellationToken cancellationToken)
        {
            EnsureConnected();

            if (string.IsNullOrWhiteSpace(jclContent))
            {
                return (
                    false,
                    string.Empty,
                    "The JCL content is empty.");
            }

            string requestedJobName =
                ExtractJobNameFromJcl(jclContent);

            string submitUrl =
                $"{_nodeUrl}jescontrol/";

            var payload =
                new
                {
                    subJes = "2",
                    ctlSubmit = "Submit",
                    JCLIn = jclContent
                };

            string jsonPayload =
                JsonConvert.SerializeObject(payload);

            try
            {
                using var request =
                    CreateHttpRequest(
                        HttpMethod.Post,
                        submitUrl,
                        submitUrl);

                request.Content =
                    new StringContent(
                        jsonPayload,
                        Encoding.UTF8,
                        "application/json");

                Debug.WriteLine(
                    "[JES SUBMIT REQUEST] " +
                    $"RequestedJob={requestedJobName} | " +
                    $"Environment={ActiveEnvironment} | " +
                    $"Server={ActiveServer} | " +
                    $"Region={ActiveRegion} | " +
                    $"Url={submitUrl}");

                using HttpResponseMessage response =
                    await HttpClient.SendAsync(
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);

                string responseBody =
                    await response.Content
                        .ReadAsStringAsync()
                        .ConfigureAwait(false);

                LastSubmissionResponse =
                    responseBody;

                Debug.WriteLine(
                    "[JES SUBMIT RESPONSE] " +
                    $"RequestedJob={requestedJobName} | " +
                    $"Environment={ActiveEnvironment} | " +
                    $"Server={ActiveServer} | " +
                    $"Region={ActiveRegion} | " +
                    $"Http={(int)response.StatusCode} | " +
                    $"Body={responseBody}");

                if (!response.IsSuccessStatusCode)
                {
                    return (
                        false,
                        string.Empty,
                        $"HTTP Error {(int)response.StatusCode}: " +
                        responseBody);
                }

                JObject document =
                    JObject.Parse(responseBody);

                JToken? jobMessageToken =
                    document.Properties()
                        .FirstOrDefault(
                            property =>
                                property.Name.Equals(
                                    "JobMsg",
                                    StringComparison.OrdinalIgnoreCase))
                        ?.Value;

                if (jobMessageToken == null)
                {
                    return (
                        false,
                        string.Empty,
                        "Unexpected JSON format: " + responseBody);
                }

                string jobNumber =
                    string.Empty;
                bool isReady =
                    false;
                var messages =
                    new StringBuilder();

                IEnumerable<JToken> messageLines =
                    jobMessageToken.Type == JTokenType.Array
                        ? jobMessageToken.Children()
                        : new[] { jobMessageToken };

                foreach (JToken lineToken in messageLines)
                {
                    string line =
                        lineToken.ToString();

                    Match match =
                        JobNumRegex.Match(line);

                    if (match.Success)
                    {
                        jobNumber =
                            "J" +
                            match.Groups["number"].Value;
                    }

                    if (line.IndexOf(
                            "Job ready for execution",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        isReady =
                            true;
                    }

                    messages.AppendLine(line);
                }

                if (string.IsNullOrWhiteSpace(jobNumber))
                {
                    Match fallback =
                        FallbackJobNumRegex.Match(
                            messages.ToString());

                    if (fallback.Success)
                    {
                        jobNumber =
                            "J" +
                            fallback.Groups["number"].Value;
                    }
                }

                if (isReady &&
                    !string.IsNullOrWhiteSpace(jobNumber))
                {
                    SubmittedJobNodes[jobNumber] = _nodeUrl;
                    Debug.WriteLine(
                        "[JES SUBMIT RESOLVED] " +
                        $"RequestedJob={requestedJobName} | " +
                        $"ReturnedJobId={jobNumber} | " +
                        $"Server={ActiveServer} | " +
                        $"Region={ActiveRegion}");

                    return (
                        true,
                        jobNumber,
                        string.Empty);
                }

                string error =
                    string.IsNullOrWhiteSpace(jobNumber)
                        ? "Job number not found.\n" + messages
                        : messages.ToString();

                return (
                    false,
                    string.Empty,
                    error.TrimEnd());
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "[JES SUBMIT ERROR] " +
                    $"RequestedJob={requestedJobName} | " +
                    $"Server={ActiveServer} | " +
                    $"Region={ActiveRegion} | " +
                    $"Error={exception}");

                return (
                    false,
                    string.Empty,
                    exception.Message);
            }
        }

        public async Task<(string Status, string ReturnCode)>
            CheckJobStatusAsync(
                string jobNum,
                CancellationToken cancellationToken)
        {
            EnsureConnected();
            string normalizedJobNumber = NormalizeJobNumber(jobNum);
            if (string.IsNullOrWhiteSpace(normalizedJobNumber))
            {
                return ("Unknown", "Invalid JobNum");
            }

            string node = SubmittedJobNodes.TryGetValue(
                normalizedJobNumber,
                out string? submittedNode)
                    ? submittedNode
                    : _nodeUrl;

            string url = $"{node}jobview/{normalizedJobNumber}";
            try
            {
                using var request = CreateHttpRequest(HttpMethod.Get, url, url);
                using HttpResponseMessage response = await HttpClient.SendAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);

                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return ("Unknown", $"HTTP Error: {(int)response.StatusCode}");
                }

                JObject document = JObject.Parse(body);
                string returnedJobNumber =
                    GetValueCaseInsensitive(document, "JobID")
                    ?? GetValueCaseInsensitive(document, "JobId")
                    ?? GetValueCaseInsensitive(document, "JobNum")
                    ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(returnedJobNumber) &&
                    !JobNumbersMatch(normalizedJobNumber, returnedJobNumber))
                {
                    return ("Unknown", "Status response job mismatch");
                }

                string status = NormalizeEscwaStatus(
                    GetValueCaseInsensitive(document, "JobStatus")
                    ?? GetValueCaseInsensitive(document, "Status")
                    ?? "Unknown");
                string returnCode = NormalizeEscwaReturnCode(
                    GetValueCaseInsensitive(document, "JobCOND")
                    ?? GetValueCaseInsensitive(document, "JobRetCode")
                    ?? GetValueCaseInsensitive(document, "MaxCC")
                    ?? GetValueCaseInsensitive(document, "ReturnCode")
                    ?? GetValueCaseInsensitive(document, "RetCode")
                    ?? "Unknown");

                if (IsTerminalEscwaStatus(status))
                {
                    SubmittedJobNodes.TryRemove(normalizedJobNumber, out _);
                    return (status, returnCode);
                }

                // Some ESCWA responses remain RUNNING although JESYSMSG already proves
                // JCLCM0182I JOB ENDED. Read the existing spool as a status fallback.
                string spool = await GetJobDiagnosticDetailsAsync(
                        normalizedJobNumber,
                        250,
                        cancellationToken)
                    .ConfigureAwait(false);
                Match ended = JobEndedRegex.Match(spool ?? string.Empty);
                if (ended.Success)
                {
                    SubmittedJobNodes.TryRemove(normalizedJobNumber, out _);
                    return ("Complete", ended.Groups["rc"].Value.PadLeft(4, '0'));
                }

                return (status, returnCode);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "[JES STATUS ERROR] " +
                    $"Job={normalizedJobNumber} | Node={node} | Error={exception}");
                return ("Unknown", $"Request error: {exception.Message}");
            }
        }

        private static bool IsTerminalEscwaStatus(string status)
        {
            return status.Equals("Complete", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Abended", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("JclError", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeEscwaStatus(string? rawStatus)
        {
            string value = (rawStatus ?? string.Empty).Trim();
            string compact = Regex.Replace(
                value,
                @"[^A-Z0-9]",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .ToUpperInvariant();

            return compact switch
            {
                "COMPLETE" or "COMPLETED" or "ENDED" or "OUTPUT" => "Complete",
                "ABEND" or "ABENDED" => "Abended",
                "FAILED" or "FAILURE" => "Failed",
                "JCLERROR" => "JclError",
                "CANCELLED" or "CANCELED" => "Cancelled",
                "RUNNING" or "ACTIVE" or "EXECUTING" => "Running",
                "WAITING" or "INPUT" or "QUEUED" => "Waiting",
                "HELD" or "HOLD" => "Held",
                _ => string.IsNullOrWhiteSpace(value) ? "Unknown" : value
            };
        }

        private static string NormalizeEscwaReturnCode(string? rawReturnCode)
        {
            string value = (rawReturnCode ?? string.Empty).Trim();
            Match match = Regex.Match(
                value,
                @"(?<![0-9])0*(?<rc>[0-9]{1,4})(?![0-9])",
                RegexOptions.CultureInvariant);

            return match.Success
                ? match.Groups["rc"].Value.PadLeft(4, '0')
                : (value.Length == 0 ? "Unknown" : value);
        }

        /// <summary>
        /// Retrieves diagnostic spool information for a job.
        /// </summary>
        public async Task<string> GetJobDiagnosticDetailsAsync(
            string jobNum,
            int maxLinesPerDd,
            CancellationToken cancellationToken)
        {
            EnsureConnected();

            string normalizedJobNumber =
                NormalizeJobNumber(jobNum);

            if (string.IsNullOrWhiteSpace(normalizedJobNumber) ||
                normalizedJobNumber == "-")
            {
                return string.Empty;
            }

            int effectiveMaxLines =
                Math.Max(1, maxLinesPerDd);

            string url =
                $"{_nodeUrl}jobview/{normalizedJobNumber}";

            var output =
                new StringBuilder();

            try
            {
                JObject document;

                using (var request =
                       CreateHttpRequest(
                           HttpMethod.Get,
                           url,
                           url))
                using (HttpResponseMessage response =
                       await HttpClient.SendAsync(
                               request,
                               cancellationToken)
                           .ConfigureAwait(false))
                {
                    string responseBody =
                        await response.Content
                            .ReadAsStringAsync()
                            .ConfigureAwait(false);

                    Debug.WriteLine(
                        "[JES SPOOL JOBVIEW] " +
                        $"Job={normalizedJobNumber} | " +
                        $"Server={ActiveServer} | " +
                        $"Region={ActiveRegion} | " +
                        $"Http={(int)response.StatusCode} | " +
                        $"Body={responseBody}");

                    if (!response.IsSuccessStatusCode)
                    {
                        return
                            $"[Spool HTTP {(int)response.StatusCode}] " +
                            $"Unable to query jobview/" +
                            normalizedJobNumber + ".";
                    }

                    document =
                        JObject.Parse(responseBody);
                }

                JToken? ddToken =
                    document.Properties()
                        .FirstOrDefault(
                            property =>
                                property.Name.Equals(
                                    "JobDDs",
                                    StringComparison.OrdinalIgnoreCase))
                        ?.Value;

                if (ddToken == null ||
                    !ddToken.Any())
                {
                    return
                        "No Spool DD outputs available for this job.";
                }

                string[] priorityDds =
                {
                    "BERPCTLO",
                    "SYSOUT",
                    "SYSPRINT",
                    "JESYSMSG",
                    "JESMSGLG",
                    "CEEDUMP"
                };

                var selectedDds =
                    new List<(string DdName, string EntityName)>();

                foreach (string targetName in priorityDds)
                {
                    foreach (JToken dd in ddToken)
                    {
                        string ddName =
                            GetTokenValueCaseInsensitive(
                                dd,
                                "DDName")
                            ?? string.Empty;

                        string entityName =
                            GetTokenValueCaseInsensitive(
                                dd,
                                "DDEntityName")
                            ?? string.Empty;

                        if (ddName.Equals(
                                targetName,
                                StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(entityName) &&
                            !selectedDds.Any(
                                item =>
                                    item.EntityName.Equals(
                                        entityName,
                                        StringComparison.OrdinalIgnoreCase)))
                        {
                            selectedDds.Add(
                                (ddName, entityName));
                        }
                    }
                }

                if (selectedDds.Count == 0)
                {
                    foreach (JToken dd in ddToken.Take(2))
                    {
                        string ddName =
                            GetTokenValueCaseInsensitive(
                                dd,
                                "DDName")
                            ?? "UNKNOWN_DD";

                        string entityName =
                            GetTokenValueCaseInsensitive(
                                dd,
                                "DDEntityName")
                            ?? string.Empty;

                        if (!string.IsNullOrWhiteSpace(entityName))
                        {
                            selectedDds.Add(
                                (ddName, entityName));
                        }
                    }
                }

                foreach ((string ddName, string entityName) in
                         selectedDds)
                {
                    string content =
                        await DownloadDdContentAsync(
                                entityName,
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        continue;
                    }

                    string[] lines =
                        content
                            .Replace("\r", string.Empty)
                            .Split('\n')
                            .Where(
                                line =>
                                    !string.IsNullOrWhiteSpace(line))
                            .ToArray();

                    output.AppendLine(
                        $"   >>> SPOOL [{ddName}] " +
                        $"(Job {normalizedJobNumber}) <<<");

                    int startIndex =
                        Math.Max(
                            0,
                            lines.Length - effectiveMaxLines);

                    if (startIndex > 0)
                    {
                        output.AppendLine(
                            $"       ... ({startIndex} earlier " +
                            "line(s) omitted) ...");
                    }

                    for (int index = startIndex;
                         index < lines.Length;
                         index++)
                    {
                        output.AppendLine(
                            $"       {lines[index].TrimEnd()}");
                    }
                }

                return output
                    .ToString()
                    .TrimEnd();
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return
                    "Spool diagnostic extraction error: " +
                    exception.Message;
            }
        }

        public Task<string> GetJobBusinessReportAsync(
            string jobNum,
            CancellationToken cancellationToken)
        {
            return GetJobDiagnosticDetailsAsync(
                jobNum,
                100,
                cancellationToken);
        }

        /// <summary>
        /// Resolves the exact LI child job referenced by explicit evidence in
        /// the parent's JES messages and verifies the returned child identity.
        /// This method never submits, replays or guesses a job number.
        /// </summary>
        public async Task<string> ResolveGeneratedJobNumberAsync(
            string parentJobNumber,
            string expectedChildJobName,
            CancellationToken cancellationToken)
        {
            EnsureConnected();

            string parent = NormalizeJobNumber(parentJobNumber);
            string expectedName = (expectedChildJobName ?? string.Empty)
                .Trim()
                .ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(parent))
                throw new ArgumentException("Invalid parent job number.", nameof(parentJobNumber));
            if (!Regex.IsMatch(expectedName, @"^[A-Z0-9$#@]{1,8}$"))
                throw new ArgumentException("Invalid generated job name.", nameof(expectedChildJobName));

            string node = _nodeUrl;
            JObject parentDocument = await ReadJobDocumentAsync(parent, node, cancellationToken)
                .ConfigureAwait(false);
            string parentJobName = GetValueCaseInsensitive(parentDocument, "JobName") ?? string.Empty;
            string parentUser = GetJobOwner(parentDocument);

            // Parent wording differs between JCLs and ESCWA versions. It is useful
            // corroboration, but it must not prevent the child lookup from running.
            bool parentMentionsChild = await JobSpoolContainsAsync(
                    parentDocument,
                    node,
                    line => line.IndexOf(expectedName, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        (line.IndexOf("GENERAT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         line.IndexOf("SUBMIT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         line.IndexOf("INTRDR", StringComparison.OrdinalIgnoreCase) >= 0),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!int.TryParse(parent.Substring(1), out int parentNumber))
                throw new InvalidOperationException($"Cannot parse parent job number {parent}.");

            const int maximumForwardDistance = 500;
            const int batchSize = 25;
            var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int first = 1; first <= maximumForwardDistance; first += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureMonitoringNode(node);
                int last = Math.Min(maximumForwardDistance, first + batchSize - 1);
                var numbers = new List<string>();
                var reads = new List<Task<JObject?>>();
                for (int offset = first; offset <= last; offset++)
                {
                    string candidate = "J" + (parentNumber + offset).ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                    numbers.Add(candidate);
                    reads.Add(TryReadJobDocumentAsync(candidate, node, cancellationToken));
                }

                JObject?[] documents = await Task.WhenAll(reads).ConfigureAwait(false);
                for (int i = 0; i < documents.Length; i++)
                {
                    JObject? child = documents[i];
                    if (child == null) continue;
                    string candidate = numbers[i];
                    string childName = GetValueCaseInsensitive(child, "JobName") ?? string.Empty;
                    if (!string.Equals(childName.Trim(), expectedName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string childUser = GetJobOwner(child);
                    if (!string.IsNullOrWhiteSpace(parentUser) &&
                        !string.IsNullOrWhiteSpace(childUser) &&
                        !string.Equals(parentUser.Trim(), childUser.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;

                    named.Add(candidate);
                    bool referencesParent = await ChildSpoolReferencesParentAsync(
                            child, node, parent, parentJobName, cancellationToken)
                        .ConfigureAwait(false);
                    if (referencesParent) exact.Add(candidate);
                    else if (parentMentionsChild) supported.Add(candidate);
                }
            }

            if (exact.Count > 1)
                throw new InvalidOperationException(
                    $"Multiple {expectedName} jobs reference parent {parent}: {string.Join(", ", exact)}.");
            if (exact.Count == 1)
                return ToGeneratedJobNumber(exact.Single(), parent, expectedName, "child spool parent link");

            if (parentMentionsChild && supported.Count > 1)
                throw new InvalidOperationException(
                    $"Parent {parent} mentions {expectedName}, but multiple matching children were found: " +
                    string.Join(", ", supported));
            if (parentMentionsChild && supported.Count == 1)
                return ToGeneratedJobNumber(
                    supported.Single(), parent, expectedName,
                    "parent generation evidence and unique same-user child");

            Debug.WriteLine(
                "[JES GENERATED JOB NOT YET RESOLVED] " +
                $"Parent={parent} | Expected={expectedName} | ParentEvidence={parentMentionsChild} | " +
                $"NameMatches={string.Join(",", named)} | Node={node}");

            // The caller applies the bounded retry policy. Never submit or replay here.
            return string.Empty;
        }

        private async Task<bool> ChildSpoolReferencesParentAsync(
            JObject childDocument,
            string node,
            string parentJobNumber,
            string parentJobName,
            CancellationToken cancellationToken)
        {
            string parent = NormalizeJobNumber(parentJobNumber);
            string digits = parent.Length > 1 ? parent.Substring(1) : parent;
            return await JobSpoolContainsAsync(
                    childDocument,
                    node,
                    line =>
                    {
                        string value = line ?? string.Empty;
                        bool hasNumber = Regex.IsMatch(
                            value,
                            @"(?<![0-9])(?:J)?0*" + Regex.Escape(digits) + @"(?![0-9])",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        bool hasName = !string.IsNullOrWhiteSpace(parentJobName) &&
                            value.IndexOf(parentJobName.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
                        bool hasRelation =
                            value.IndexOf("INTRDR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            value.IndexOf("SUBMIT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            value.IndexOf("PARENT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            value.IndexOf("GENERAT", StringComparison.OrdinalIgnoreCase) >= 0;
                        return hasRelation && (hasNumber || hasName);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static string GetJobOwner(JObject document)
        {
            return GetValueCaseInsensitive(document, "JobUser")
                ?? GetValueCaseInsensitive(document, "User")
                ?? GetValueCaseInsensitive(document, "Owner")
                ?? GetValueCaseInsensitive(document, "JobOwner")
                ?? string.Empty;
        }

        private static string ToGeneratedJobNumber(
            string childJobNumber,
            string parentJobNumber,
            string childJobName,
            string source)
        {
            string child = NormalizeJobNumber(childJobNumber);
            if (string.IsNullOrWhiteSpace(child))
                throw new InvalidOperationException($"Invalid resolved child job number: {childJobNumber}.");
            Debug.WriteLine(
                "[JES GENERATED JOB RESOLVED] " +
                $"Parent={parentJobNumber} | Child={child} | Name={childJobName} | Source={source}");
            return child.Substring(1);
        }

        private async Task<bool> JobSpoolContainsAsync(
            JObject jobDocument,
            string node,
            Func<string, bool> predicate,
            CancellationToken cancellationToken)
        {
            JToken? dds = jobDocument.Properties()
                .FirstOrDefault(
                    property => property.Name.Equals(
                        "JobDDs",
                        StringComparison.OrdinalIgnoreCase))
                ?.Value;

            if (dds == null)
            {
                return false;
            }

            string[] preferredDds =
            {
                "JESYSMSG",
                "JESMSGLG",
                "SYSOUT",
                "SYSPRINT",
                "LFXROT01"
            };

            var entities = new List<(int Priority, string Name)>();

            foreach (JToken dd in dds.Children())
            {
                string ddName =
                    GetTokenValueCaseInsensitive(dd, "DDName")
                    ?? string.Empty;

                string entityName =
                    GetTokenValueCaseInsensitive(dd, "DDEntityName")
                    ?? string.Empty;

                if (string.IsNullOrWhiteSpace(entityName))
                {
                    continue;
                }

                int priority = Array.FindIndex(
                    preferredDds,
                    name => name.Equals(
                        ddName,
                        StringComparison.OrdinalIgnoreCase));

                entities.Add((
                    priority >= 0 ? priority : preferredDds.Length,
                    entityName));
            }

            foreach ((int _, string entityName) in entities
                         .OrderBy(item => item.Priority)
                         .Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureMonitoringNode(node);

                string content =
                    await DownloadDdContentAsync(
                            entityName,
                            cancellationToken)
                        .ConfigureAwait(false);

                EnsureMonitoringNode(node);

                foreach (string line in content
                             .Replace("\r", string.Empty)
                             .Split('\n'))
                {
                    if (predicate(line))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static async Task<JObject?> TryReadJobDocumentAsync(
            string jobNumber,
            string node,
            CancellationToken cancellationToken)
        {
            EnsureMonitoringNode(node);
            string url = $"{node}jobview/{jobNumber}";

            try
            {
                using var request =
                    CreateHttpRequest(
                        HttpMethod.Get,
                        url,
                        url);

                using HttpResponseMessage response =
                    await HttpClient.SendAsync(
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine(
                        "[JES CHILD PROBE] " +
                        $"Job={jobNumber} | HTTP={(int)response.StatusCode}");
                    return null;
                }

                string body =
                    await response.Content
                        .ReadAsStringAsync()
                        .ConfigureAwait(false);

                EnsureMonitoringNode(node);
                JObject document = JObject.Parse(body);

                string returned =
                    GetValueCaseInsensitive(document, "JobID")
                    ?? GetValueCaseInsensitive(document, "JobId")
                    ?? GetValueCaseInsensitive(document, "JobNum")
                    ?? string.Empty;

                return JobNumbersMatch(jobNumber, returned)
                    ? document
                    : null;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "[JES CHILD PROBE ERROR] " +
                    $"Job={jobNumber} | Error={exception.Message}");
                return null;
            }
        }

        private static void EnsureMonitoringNode(string expectedNode)
        {
            if (!string.Equals(_nodeUrl, expectedNode, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The active ESCWA node changed during generated job lookup.");
        }

        private static async Task<JObject> ReadJobDocumentAsync(
            string jobNumber,
            string node,
            CancellationToken cancellationToken)
        {
            EnsureMonitoringNode(node);
            string url = $"{node}jobview/{jobNumber}";
            using var request = CreateHttpRequest(HttpMethod.Get, url, url);
            using HttpResponseMessage response = await HttpClient.SendAsync(
                    request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Cannot inspect job {jobNumber}: HTTP {(int)response.StatusCode}.");
            EnsureMonitoringNode(node);
            JObject document = JObject.Parse(body);
            string returned = GetValueCaseInsensitive(document, "JobID")
                ?? GetValueCaseInsensitive(document, "JobId")
                ?? GetValueCaseInsensitive(document, "JobNum")
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(returned) ||
                !JobNumbersMatch(jobNumber, returned))
                throw new InvalidOperationException(
                    $"The jobview response does not confirm job {jobNumber}.");
            return document;
        }

        private async Task<string> DownloadDdContentAsync(
            string ddEntityName,
            CancellationToken cancellationToken)
        {
            string encodedEntityName =
                Uri.EscapeDataString(ddEntityName);

            string spoolUrl =
                $"{_nodeUrl}spool/{encodedEntityName}";

            try
            {
                using var spoolRequest =
                    CreateHttpRequest(
                        HttpMethod.Get,
                        spoolUrl,
                        spoolUrl);

                using HttpResponseMessage spoolResponse =
                    await HttpClient.SendAsync(
                            spoolRequest,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (spoolResponse.IsSuccessStatusCode)
                {
                    return await spoolResponse.Content
                        .ReadAsStringAsync()
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "[JES SPOOL ENDPOINT ERROR] " +
                    $"Entity={ddEntityName} | " +
                    $"Error={exception.Message}");
            }

            string ddViewUrl =
                $"{_nodeUrl}ddview/{encodedEntityName}";

            try
            {
                using var ddViewRequest =
                    CreateHttpRequest(
                        HttpMethod.Get,
                        ddViewUrl,
                        ddViewUrl);

                using HttpResponseMessage ddViewResponse =
                    await HttpClient.SendAsync(
                            ddViewRequest,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (ddViewResponse.IsSuccessStatusCode)
                {
                    return await ddViewResponse.Content
                        .ReadAsStringAsync()
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "[JES DDVIEW ENDPOINT ERROR] " +
                    $"Entity={ddEntityName} | " +
                    $"Error={exception.Message}");
            }

            return string.Empty;
        }

        private async Task<bool> TestConnectionAsync(
            string nodeUrl,
            CancellationToken cancellationToken)
        {
            try
            {
                string testUrl =
                    $"{nodeUrl}region-functionality";

                using var request =
                    CreateHttpRequest(
                        HttpMethod.Get,
                        testUrl,
                        testUrl);

                using HttpResponseMessage response =
                    await HttpClient.SendAsync(
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);

                Debug.WriteLine(
                    "[ESCWA CONNECTION TEST] " +
                    $"Server={ActiveServer} | " +
                    $"Region={ActiveRegion} | " +
                    $"Url={testUrl} | " +
                    $"Http={(int)response.StatusCode}");

                return response.IsSuccessStatusCode;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "[ESCWA CONNECTION TEST ERROR] " +
                    $"Server={ActiveServer} | " +
                    $"Region={ActiveRegion} | " +
                    $"Error={exception.Message}");

                return false;
            }
        }

        private static void ConfigureActiveNode(
            string baseUrl,
            string server,
            string environment)
        {
            ActiveEnvironment =
                environment;
            ActiveServer =
                server;
            ActiveRegion =
                ResolveBatchRegion(environment);

            _nodeUrl =
                $"{baseUrl}/native/v1/regions/" +
                $"{ActiveServer}/86/{ActiveRegion}/";

            ActiveNodeUrl =
                _nodeUrl;

            Debug.WriteLine(
                "[ESCWA REGION] " +
                $"Environment={ActiveEnvironment} | " +
                $"Server={ActiveServer} | " +
                $"Region={ActiveRegion} | " +
                $"NodeUrl={ActiveNodeUrl}");
        }

        private static string ResolveBatchRegion(
            string environment)
        {
            /*
             * Important: keep the ESCWA region aligned with the
             * environment used by the API. Do not redirect Q to BATCHD
             * without an explicit, verified infrastructure mapping.
             */
            return environment switch
            {
                "D" => "BATCHD",
                "Q" => "BATCHQ",
                "A" => "BATCHA",
                "P" => "BATCHP",
                _ => throw new ArgumentException(
                    $"Unsupported Mainframe environment: " +
                    $"'{environment}'.",
                    nameof(environment))
            };
        }

        private static string NormalizeEnvironment(
            string? environment)
        {
            string value =
                string.IsNullOrWhiteSpace(environment)
                    ? "D"
                    : environment.Trim().ToUpperInvariant();

            if (value.StartsWith("D", StringComparison.Ordinal))
            {
                return "D";
            }

            if (value.StartsWith("Q", StringComparison.Ordinal))
            {
                return "Q";
            }

            if (value.StartsWith("A", StringComparison.Ordinal))
            {
                return "A";
            }

            if (value.StartsWith("P", StringComparison.Ordinal))
            {
                return "P";
            }

            return value;
        }

        private static bool IsSessionCookieValid(
            Cookie? cookie)
        {
            return cookie != null &&
                   (cookie.Expires == DateTime.MinValue ||
                    cookie.Expires > DateTime.Now.AddMinutes(1));
        }

        private static string ExtractJobNameFromJcl(
            string? jclContent)
        {
            if (string.IsNullOrWhiteSpace(jclContent))
            {
                return "UNKNOWN";
            }

            Match match =
                JobNameRegex.Match(jclContent);

            return match.Success
                ? match.Groups["name"]
                    .Value
                    .Trim()
                    .ToUpperInvariant()
                : "UNKNOWN";
        }

        private static string NormalizeJobNumber(string? jobNumber)
        {
            string value = (jobNumber ?? string.Empty).Trim();
            Match match = Regex.Match(value, @"^[Jj]?(?<number>[0-9]{5,10})$");
            return match.Success ? "J" + match.Groups["number"].Value : string.Empty;
        }

        private static bool JobNumbersMatch(
            string left,
            string right)
        {
            string normalizedLeft =
                NormalizeJobNumber(left);
            string normalizedRight =
                NormalizeJobNumber(right);

            return !string.IsNullOrWhiteSpace(normalizedLeft) &&
                normalizedLeft.Equals(
                normalizedRight,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetValueCaseInsensitive(
            JObject document,
            string key)
        {
            return document.Properties()
                .FirstOrDefault(
                    property =>
                        property.Name.Equals(
                            key,
                            StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.ToString()
                .Trim();
        }

        private static string? GetTokenValueCaseInsensitive(
            JToken token,
            string key)
        {
            if (token is not JObject objectToken)
            {
                return null;
            }

            return GetValueCaseInsensitive(
                objectToken,
                key);
        }

        private static void EnsureConnected()
        {
            if (string.IsNullOrWhiteSpace(_nodeUrl))
            {
                throw new InvalidOperationException(
                    "Not connected to the ESCWA server. " +
                    "Call LogonAsync first.");
            }
        }

        private static HttpRequestMessage CreateHttpRequest(
            HttpMethod method,
            string url,
            string? referer)
        {
            var request =
                new HttpRequestMessage(
                    method,
                    url);

            if (!string.IsNullOrWhiteSpace(referer))
            {
                request.Headers.Referrer =
                    new Uri(referer);
            }

            return request;
        }
    }
}