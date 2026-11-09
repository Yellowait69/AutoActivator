using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AutoActivator.Config;

namespace AutoActivator.Services
{
    public enum DsnDirection
    {
        Read,
        Write
    }

    public enum TransferMode
    {
        Text,
        Binary
    }

    /// <summary>
    /// Service responsible for parsing, cleaning, and injecting variables into JCL (Job Control Language) templates.
    /// Includes an in-memory template cache and compiled regex cache to eliminate repetitive SMB network I/O
    /// during single and batch contract activations.
    /// </summary>
    public class JclProcessorService
    {
        private readonly string _jclDirectory;

        private static readonly string TempShare = "FILES.FIBE.FORTIS";
        private static readonly string WriteTempFolder = @"\elia\11 - Technical Architecture\11 - IS Tooling\01 - Tools\FTP-Write\";
        private static readonly string ReadTempFolder = @"\elia\11 - Technical Architecture\11 - IS Tooling\01 - Tools\FTP-Read\";

        // In-memory cache of cleaned JCL templates (key: full normalized path or directory+jobName)
        private static readonly ConcurrentDictionary<string, string> _cleanedJclCache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Cache of compiled Regex patterns per variable name for fast JCL variable substitution
        private static readonly ConcurrentDictionary<string, Regex[]> _variableRegexCache =
            new ConcurrentDictionary<string, Regex[]>(StringComparer.OrdinalIgnoreCase);

        public static readonly string[] StandardActivationJobs =
        {
            "ADDPRCT", "LVPP06U", "LVPG22U", "LI1J04D0", "LI1J04D2"
        };

        // Confirmed in LVChain: CM is a real LVPG22U variable whose valid value
        // is intentionally blank. Presence of the key means it is resolved.
        private static readonly HashSet<string> VariablesAllowingBlankValue =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CM"
            };

        public JclProcessorService(string jclDirectory)
        {
            _jclDirectory = jclDirectory;
        }

        /// <summary>
        /// Pre-loads and caches all standard activation JCL templates in parallel from the network share.
        /// </summary>
        public async Task PreloadTemplatesAsync(IEnumerable<string> jobNames = null)
        {
            var targets = jobNames ?? StandardActivationJobs;
            var tasks = targets.Select(job => GetCleanedTemplateAsync(job));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private async Task<string> GetCleanedTemplateAsync(string jobName)
        {
            string cleanJobName = jobName.Trim().ToUpperInvariant().Replace(".JCL", "");
            string cacheKey = $"{_jclDirectory}|{cleanJobName}";

            if (_cleanedJclCache.TryGetValue(cacheKey, out string cachedContent))
            {
                return cachedContent;
            }

            string fileName = cleanJobName + ".JCL";
            string filePath = Path.Combine(_jclDirectory, fileName);

            if (!File.Exists(filePath)) filePath = Path.Combine(_jclDirectory, jobName);
            if (!File.Exists(filePath)) throw new FileNotFoundException($"JCL file not found on network share: {filePath}");

            string rawContent;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            {
                rawContent = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            string correctedContent = DoCorrections(rawContent);
            _cleanedJclCache[cacheKey] = correctedContent;
            return correctedContent;
        }

        /// <summary>
        /// Returns the %% variables detected in the JCL template that have no value in the provided dictionary.
        /// </summary>
        public async Task<List<string>> GetMissingVariablesAsync(
            string jobName,
            IReadOnlyDictionary<string, string> variables)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
            ArgumentNullException.ThrowIfNull(variables);

            string cleanJobName = jobName.Trim().ToUpperInvariant().Replace(".JCL", "");
            string template = await GetCleanedTemplateAsync(cleanJobName).ConfigureAwait(false);
            var canonicalVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, string> entry in variables)
            {
                string name = NormalizeVariableName(entry.Key);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    canonicalVariables[name] = entry.Value;
                }
            }

            return FindAllVars(template)
                .Select(NormalizeVariableName)
                .Where(name => !name.Equals("JOBNAM", StringComparison.OrdinalIgnoreCase))
                .Where(name =>
                {
                    if (!canonicalVariables.TryGetValue(name, out string? value) || value == null)
                    {
                        return true;
                    }

                    if (string.Equals(value.Trim(), "?", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("%%", StringComparison.Ordinal))
                    {
                        return true;
                    }

                    return string.IsNullOrWhiteSpace(value) &&
                           !VariablesAllowingBlankValue.Contains(name);
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<string> GetPreparedJclAsync(string jobName, Dictionary<string, string> variables, int count)
        {
            string cleanJobName = jobName.Trim().ToUpperInvariant().Replace(".JCL", "");
            string correctedContent = await GetCleanedTemplateAsync(cleanJobName).ConfigureAwait(false);
            return ApplyVariables(cleanJobName, correctedContent, variables, count);
        }

        private string DoCorrections(string content)
        {
            StringBuilder output = new StringBuilder(content.Length);

            foreach (string line in content.Replace("\r", "").Split('\n'))
            {
                if (line.StartsWith("//*", StringComparison.Ordinal)) continue;

                string processedLine = line;

                if (processedLine.Contains(" IF ") && processedLine.Contains("RC<"))
                {
                    processedLine = processedLine.Replace("RC<", "RC < ");
                }

                if (processedLine.Length > 72 && int.TryParse(processedLine.Substring(72).Trim(), out int _))
                    output.AppendLine(processedLine.Substring(0, 72));
                else
                    output.AppendLine(processedLine);
            }

            StringBuilder output2 = new StringBuilder(output.Length);
            string[] lines = output.ToString().Replace("\r", "").Split('\n');

            bool existingJobcard = false;
            bool jobCardFound = false;

            foreach (string line in lines)
            {
                if (!jobCardFound && line.ToUpperInvariant().Contains(" JOB "))
                {
                    existingJobcard = true;
                    jobCardFound = true;
                }

                if (!existingJobcard)
                    output2.AppendLine(line);

                if (existingJobcard && !line.Trim().EndsWith(",", StringComparison.Ordinal))
                    existingJobcard = false;
            }

            return output2.ToString().TrimEnd('\n', '\r');
        }

        private string ApplyVariables(string cleanJobName, string content, Dictionary<string, string> vars, int count)
        {
            ArgumentNullException.ThrowIfNull(vars);

            var localVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> entry in vars)
            {
                string canonicalName = NormalizeVariableName(entry.Key);
                if (!string.IsNullOrWhiteSpace(canonicalName))
                {
                    localVars[canonicalName] = entry.Value ?? string.Empty;
                }
            }

            if (!localVars.ContainsKey("JOBNAM"))
            {
                localVars.Add("JOBNAM", cleanJobName);
            }

            string env = localVars.TryGetValue("ENV", out string envVal) ? envVal : "D";
            string jobClass = localVars.TryGetValue("CLASS", out string clsVal) ? clsVal : "A";
            string username = localVars.TryGetValue("USERNAME", out string usrVal) && !string.IsNullOrEmpty(usrVal)
                ? usrVal
                : Environment.UserName;

            string schenv = "IM7T";
            if (env == "D") schenv = "IM7T";
            else if (env == "Q") schenv = "IM7C";
            else if (env == "A") schenv = "IM7Q";
            else if (env == "P") schenv = "IM7P";

            string jobHeaderName = localVars.TryGetValue("JOBNAM", out string jn) && !string.IsNullOrEmpty(jn)
                ? jn.Trim().ToUpperInvariant()
                : username + count;

            string jobcard = $"//{jobHeaderName} JOB CLASS={jobClass},SCHENV={schenv},NOTIFY={username}\r\n";

            string normalizedBody = content.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            string content2 = (jobcard + normalizedBody).Replace("\r\n\r\n", "\r\n").TrimEnd('\n', '\r');

            string tempContent = ApplyVarsCore(content2, localVars);
            int maxPasses = 10;
            while (content2 != tempContent && maxPasses-- > 0 && tempContent.Contains("%%"))
            {
                content2 = tempContent;
                tempContent = ApplyVarsCore(content2, localVars);
            }
            content2 = tempContent;

            StringBuilder content3 = new StringBuilder(content2.Length);
            foreach (string line in content2.Replace("\r", "").Split('\n'))
            {
                if (line.StartsWith("+", StringComparison.Ordinal))
                {
                    if (line.Length > 1) content3.AppendLine(line.Substring(1));
                    else content3.AppendLine("");
                }
                else
                {
                    content3.AppendLine(line);
                }
            }

            return content3.ToString().TrimEnd('\n', '\r');
        }

        private static Regex[] GetCompiledPatternsForVar(string rawVarName)
        {
            string canonicalName = NormalizeVariableName(rawVarName);
            return _variableRegexCache.GetOrAdd(canonicalName, key =>
            {
                string escaped = Regex.Escape(key);
                const RegexOptions options =
                    RegexOptions.Compiled | RegexOptions.CultureInvariant;

                return new[]
                {
                    // %%CM. uses the final period as a concatenation delimiter. The
                    // period is consumed so a blank CM does not leave a stray dot.
                    new Regex(@"%%" + escaped + @"\.", options),
                    new Regex(@"%%" + escaped + @"(?=%)", options),
                    new Regex(@"%%" + escaped + @"(?=[, \n\r])", options),
                    new Regex(@"%%" + escaped + @"(?=')", options),
                    new Regex(@"%%" + escaped + @"(?=\))", options),
                    new Regex(@"%%" + escaped + @"$", options)
                };
            });
        }

        private string ApplyVarsCore(
            string content,
            Dictionary<string, string> variables)
        {
            if (string.IsNullOrEmpty(content) || !content.Contains("%%", StringComparison.Ordinal))
            {
                return content;
            }

            string result = content;
            foreach (KeyValuePair<string, string> entry in variables)
            {
                string variableName = NormalizeVariableName(entry.Key);
                if (string.IsNullOrWhiteSpace(variableName) ||
                    result.IndexOf("%%" + variableName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                string value = entry.Value ?? string.Empty;
                if (value == "''")
                {
                    value = string.Empty;
                }
                else if (value.StartsWith("'", StringComparison.Ordinal) &&
                         value.EndsWith("'", StringComparison.Ordinal) &&
                         value.Length >= 3)
                {
                    value = value.Substring(1, value.Length - 2).Replace("''", "'");
                }

                // MatchEvaluator inserts the literal value. It does not interpret '$'
                // as a regex replacement token, so JOBNAMI=LVPP06U$ is preserved.
                MatchEvaluator evaluator = _ => value;
                foreach (Regex pattern in GetCompiledPatternsForVar(variableName))
                {
                    result = pattern.Replace(result, evaluator);
                }
            }

            return result;
        }

        private static string NormalizeVariableName(string? rawName)
        {
            string name = (rawName ?? string.Empty).Trim().ToUpperInvariant();
            if (name.StartsWith("%%", StringComparison.Ordinal))
            {
                name = name.Substring(2);
            }

            // A final dot in %%CM. is a JCL concatenation delimiter, not part of
            // the dictionary key.
            return name.TrimEnd('.');
        }

        public List<string> FindDSNs(string jclContent)
        {
            StringBuilder content = new StringBuilder();
            List<string> jclLines = jclContent.Replace("\r", "").Split('\n').ToList();
            foreach (string jclLine in jclLines)
            {
                if (jclLine.StartsWith("//*", StringComparison.Ordinal)) continue;
                content.AppendLine(jclLine);
            }
            string cleanContent = content.ToString();

            HashSet<string> dsnList = new HashSet<string>();

            List<string> patterns = new List<string>
            {
                @"DSN=([^,\r\n]+)([,% \n\r])",
            };

            foreach (string pattern in patterns)
            {
                var matches = Regex.Matches(cleanContent, pattern);

                for (int i = 0; i < matches.Count; i++)
                {
                    var value = matches[i].Groups[1].Value;
                    if (value.StartsWith("&&", StringComparison.Ordinal)) continue;
                    dsnList.Add((value ?? "").Trim());
                }
            }

            return dsnList.OrderBy(x => x).ToList();
        }

        public List<string> FindAllVars(string jclContent)
        {
            if (string.IsNullOrEmpty(jclContent))
            {
                return new List<string>();
            }

            var content = new StringBuilder();
            foreach (string jclLine in jclContent.Replace("\r", string.Empty).Split('\n'))
            {
                if (!jclLine.StartsWith("//*", StringComparison.Ordinal))
                {
                    content.AppendLine(jclLine);
                }
            }

            var variables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(
                         content.ToString(),
                         @"%%(?<name>[A-Z][A-Z0-9\-]*)(?=\.|%|[, '\)\s]|$)",
                         RegexOptions.CultureInvariant))
            {
                string name = NormalizeVariableName(match.Groups["name"].Value);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    variables.Add(name);
                }
            }

            return variables.OrderBy(value => value).ToList();
        }

        public string GenerateFtpJcl(DsnDirection direction, string dsn, string tempFileName, TransferMode transferMode)
        {
            return GenerateFtpJcl(direction, new List<Tuple<string, string>> { new Tuple<string, string>(dsn, tempFileName) }, transferMode);
        }

        public string GenerateFtpJcl(DsnDirection direction, List<Tuple<string, string>> dsnAndFile, TransferMode transferMode)
        {
            string uid = Settings.DbConfig.Uid ?? "";
            string pwd = Settings.DbConfig.Pwd ?? "";

            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(pwd))
            {
                throw new InvalidOperationException("Credentials are not defined.");
            }

            string jclTemplate =
@"//##USER##T JOB CLASS=I
//TRANSFER EXEC PGM=MFFTP,
//            PARM='##SERVER##'
//SYSPRINT DD SYSOUT=*
//OUTPUT   DD SYSOUT=*
//INPUT    DD *
AG\##USER## ##PASS##
CD '##PATH##'
##OPTIONS##LOCSTAT
##INSTRUCTION##
QUIT
/*
//ENVVAR   DD *
MFFTP_TRANSLATE_SAFETY=OFF
MFFTP_SENDEOL=CRLF
MFFTP_PROCESS_TRAILS_ONGET=FALSE
/*";

            string tempFolder;
            string instructionTemplate;

            switch (direction)
            {
                case DsnDirection.Read:
                    tempFolder = ReadTempFolder;
                    instructionTemplate = "PUT ##DSN## +\r\n##FILE##";
                    break;
                case DsnDirection.Write:
                    tempFolder = WriteTempFolder;
                    instructionTemplate = "GET ##FILE## +\r\n'##DSN##' (REP";
                    break;
                default:
                    throw new ArgumentException("Unknown DsnDirection");
            }

            StringBuilder instructions = new StringBuilder();
            foreach (var tuple in dsnAndFile)
            {
                string instruction = instructionTemplate
                    .Replace("##DSN##", tuple.Item1.ToUpperInvariant().Trim().Trim('\''))
                    .Replace("##FILE##", tuple.Item2.Trim().Trim('\''));
                instructions.AppendLine(instruction);
            }

            string options = transferMode == TransferMode.Text
                ? "LOCSITE ENCODING=SBCS\r\nLOCSITE SBDATACONN=TABSTD\r\n" : "";

            string jcl = jclTemplate
                .Replace("##USER##", uid)
                .Replace("##PASS##", pwd)
                .Replace("##SERVER##", TempShare)
                .Replace("##PATH##", tempFolder)
                .Replace("##OPTIONS##", options)
                .Replace("##INSTRUCTION##", instructions.ToString().TrimEnd('\n', '\r'));

            return jcl;
        }

        public string ComposeTempFileName(DsnDirection direction)
        {
            string uid = Settings.DbConfig.Uid ?? "UNKNOWN";
            string d = direction == DsnDirection.Read ? "R" : "S";
            return $"{uid}_{d}_{Guid.NewGuid()}.TXT";
        }

        public string GetReadTempFolderPath()
        {
            return $@"\\{TempShare}\{ReadTempFolder.Trim('\\')}";
        }

        public string GetWriteTempFolderPath()
        {
            return $@"\\{TempShare}\{WriteTempFolder.Trim('\\')}";
        }
    }
}