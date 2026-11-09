using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Sql;
using AutoActivator.Utils;

namespace AutoActivator.Services
{
    public class ExtractionService
    {
        public ExtractionService()
        {
        }

        public async Task<ExtractionResult> PerformExtractionAsync(
            string targetContract,
            string envSuffix,
            bool saveIndividualFile = true,
            bool isDemandId = false,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(targetContract))
                throw new ArgumentException("The input value is empty.", nameof(targetContract));

            token.ThrowIfCancellationRequested();

            envSuffix = Settings.NormalizeEnvironmentSuffix(envSuffix);
            string cleanedInput = targetContract.Replace("\u00A0", "").Replace("\uFEFF", "").Trim();
            var db = new DatabaseManager(envSuffix);
            var envQueries = SqlQueries.GetQueries(envSuffix);

            string cleanedContract = cleanedInput;
            string eliaUconId = "Not found";
            string eliaDemandId = "Not found";
            string internalIdString = "Not found";
            string premiumAmount = "0";
            long? internalIdValue = null;

            // 1. Resolve full context (DemandId, UconId, ContractNumber, InternalId, GrossPremium) in a single query
            string contextQueryKey = isDemandId ? "GET_FULL_CONTEXT_BY_DEMAND" : "GET_FULL_CONTEXT_BY_CONTRACT";
            string contextParamName = isDemandId ? "@DemandId" : "@ContractNumber";

            using (var dtContext = await db.GetDataAsync(
                envQueries[contextQueryKey],
                new Dictionary<string, object> { { contextParamName, cleanedInput } },
                token).ConfigureAwait(false))
            {
                if (dtContext.Rows.Count > 0)
                {
                    DataRow ctxRow = dtContext.Rows[0];

                    if (dtContext.Columns.Contains("ContractNumber") && ctxRow["ContractNumber"] != DBNull.Value)
                    {
                        string? cNum = ctxRow["ContractNumber"]?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(cNum))
                            cleanedContract = cNum;
                    }

                    if (dtContext.Columns.Contains("UconId") && ctxRow["UconId"] != DBNull.Value)
                    {
                        string? uId = ctxRow["UconId"]?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(uId))
                            eliaUconId = uId;
                    }

                    if (dtContext.Columns.Contains("DemandId") && ctxRow["DemandId"] != DBNull.Value)
                    {
                        string? dId = ctxRow["DemandId"]?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(dId))
                            eliaDemandId = dId;
                    }

                    if (dtContext.Columns.Contains("InternalId") && ctxRow["InternalId"] != DBNull.Value)
                    {
                        internalIdValue = Convert.ToInt64(ctxRow["InternalId"]);
                        internalIdString = internalIdValue.Value.ToString();
                    }

                    if (dtContext.Columns.Contains("GrossPremium") && ctxRow["GrossPremium"] != DBNull.Value)
                    {
                        string? gPrem = ctxRow["GrossPremium"]?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(gPrem))
                            premiumAmount = gPrem;
                    }
                }
                else if (isDemandId)
                {
                    throw new Exception($"No associated contract found for Demand ID {cleanedInput} in environment {envSuffix} ({Settings.DbConfig.GetLifeDatabase(envSuffix)}).");
                }
            }

            token.ThrowIfCancellationRequested();

            // 2. Fallback lookup if contract was searched by ContractNumber and exists only in LISA (not in ELIA)
            if (!internalIdValue.HasValue && eliaUconId == "Not found")
            {
                var parameters = new Dictionary<string, object> { { "@ContractNumber", cleanedContract } };
                var lisaTask = db.GetDataAsync(envQueries["GET_INTERNAL_ID"], parameters, token);
                var eliaTask = db.GetDataAsync(envQueries["GET_ELIA_ID"], parameters, token);

                await Task.WhenAll(lisaTask, eliaTask).ConfigureAwait(false);

                using (var dtLisa = await lisaTask.ConfigureAwait(false))
                using (var dtElia = await eliaTask.ConfigureAwait(false))
                {
                    if (dtLisa.Rows.Count > 0 && dtLisa.Rows[0]["NO_CNT"] != DBNull.Value)
                    {
                        internalIdValue = Convert.ToInt64(dtLisa.Rows[0]["NO_CNT"]);
                        internalIdString = internalIdValue.Value.ToString();
                    }

                    if (dtElia.Rows.Count > 0 && dtElia.Rows[0]["IT5UCONAIDN"] != DBNull.Value)
                    {
                        eliaUconId = dtElia.Rows[0]["IT5UCONAIDN"]?.ToString()?.Trim() ?? "Not found";
                    }
                }
            }

            if (!internalIdValue.HasValue && eliaUconId == "Not found")
            {
                throw new Exception($"Contract {cleanedContract} not found. Verify that you are targeting the correct database ({envSuffix} — {Settings.DbConfig.GetServerName(envSuffix)}).");
            }

            var sbLisa = new StringBuilder();
            var sbElia = new StringBuilder();

            #region LISA SECTION
            token.ThrowIfCancellationRequested();

            if (internalIdValue.HasValue)
            {
                await ExtractAndAppendTablesAsync(
                    envSuffix,
                    envQueries,
                    SqlQueries.LisaTables,
                    "@InternalId",
                    internalIdValue.Value,
                    sbLisa,
                    null,
                    token).ConfigureAwait(false);
            }
            else
            {
                sbLisa.AppendLine($"### DIAGNOSTIC : Contract {cleanedContract} missing in LISA ({Settings.DbConfig.GetApplicationDatabase(envSuffix)}) ###");
            }
            #endregion

            #region ELIA SECTION
            token.ThrowIfCancellationRequested();

            if (eliaUconId != "Not found")
            {
                var demandIds = new List<string>();
                using (var dtDemand = await db.GetDataAsync(
                    envQueries["GET_ELIA_DEMAND_IDS"],
                    new Dictionary<string, object> { { "@EliaId", eliaUconId } },
                    token).ConfigureAwait(false))
                {
                    if (dtDemand.Columns.Contains("IT5HDMDAIDN"))
                    {
                        foreach (DataRow row in dtDemand.Rows)
                        {
                            string? dId = row["IT5HDMDAIDN"]?.ToString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(dId))
                                demandIds.Add(dId);
                        }
                    }
                }

                if (demandIds.Count > 0)
                    eliaDemandId = string.Join(", ", demandIds);

                // Extract ELIA tables and capture IT5UPRPUBRU directly from FJ1.TB5UPRP if not already populated
                await ExtractAndAppendTablesAsync(
                    envSuffix,
                    envQueries,
                    SqlQueries.EliaTables,
                    "@EliaId",
                    eliaUconId,
                    sbElia,
                    (tableName, dt) =>
                    {
                        if (tableName.Equals("FJ1.TB5UPRP", StringComparison.OrdinalIgnoreCase) &&
                            dt.Rows.Count > 0 &&
                            dt.Columns.Contains("IT5UPRPUBRU"))
                        {
                            string? rawPrem = dt.Rows[0]["IT5UPRPUBRU"]?.ToString()?.Trim();
                            if (!string.IsNullOrEmpty(rawPrem))
                                premiumAmount = rawPrem;
                        }
                    },
                    token).ConfigureAwait(false);

                if (demandIds.Count > 0)
                {
                    await ExtractAndAppendTablesAsync(
                        envSuffix,
                        envQueries,
                        SqlQueries.EliaDemandTables,
                        "@DemandIds",
                        string.Join(",", demandIds),
                        sbElia,
                        null,
                        token).ConfigureAwait(false);
                }
            }
            else
            {
                sbElia.AppendLine($"### ELIA SECTION : NO DATA FOUND IN {Settings.DbConfig.GetLifeDatabase(envSuffix)} ###");
            }
            #endregion

            token.ThrowIfCancellationRequested();

            string finalLisaContent = sbLisa.ToString();
            string finalEliaContent = sbElia.ToString();
            string savedFilePath = Settings.OutputDir;

            if (saveIndividualFile)
            {
                Directory.CreateDirectory(Settings.OutputDir);
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                char envLetter = !string.IsNullOrEmpty(envSuffix) ? char.ToUpperInvariant(envSuffix[0]) : 'U';
                string safeContractName = SanitizeFileName(cleanedContract);
                string combinedPath = Path.Combine(Settings.OutputDir, $"Extraction_{envLetter}_Uniq_{safeContractName}_{timestamp}.csv");

                string lisaHeader = $"================================================================================\n=== SECTION LISA (INTERNAL ID: {internalIdString} | DB: {Settings.DbConfig.GetApplicationDatabase(envSuffix)}) ===\n================================================================================\n";
                string eliaHeader = $"================================================================================\n=== SECTION ELIA (UCON ID: {eliaUconId} | DB: {Settings.DbConfig.GetLifeDatabase(envSuffix)}) ===\n================================================================================\n";

                try
                {
                    await WriteIndividualExtractionFileAsync(combinedPath, lisaHeader, finalLisaContent, eliaHeader, finalEliaContent).ConfigureAwait(false);
                    savedFilePath = combinedPath;
                }
                catch (IOException)
                {
                    string alternativePath = Path.Combine(Settings.OutputDir, $"Extraction_{envLetter}_Uniq_{safeContractName}_{timestamp}_{Guid.NewGuid().ToString("N").Substring(0, 4)}.csv");
                    await WriteIndividualExtractionFileAsync(alternativePath, lisaHeader, finalLisaContent, eliaHeader, finalEliaContent).ConfigureAwait(false);
                    savedFilePath = alternativePath;
                }
            }

            return new ExtractionResult
            {
                ContractReference = cleanedContract,
                FilePath = savedFilePath,
                StatusMessage = $"Extraction saved | ID: {internalIdString}",
                InternalId = internalIdString,
                UconId = eliaUconId,
                DemandId = eliaDemandId,
                LisaContent = finalLisaContent,
                EliaContent = finalEliaContent,
                Premium = premiumAmount
            };
        }

        private static async Task WriteIndividualExtractionFileAsync(string path, string lisaHeader, string lisaContent, string eliaHeader, string eliaContent)
        {
            using (StreamWriter writer = new StreamWriter(path, false, Encoding.UTF8))
            {
                await writer.WriteAsync(lisaHeader).ConfigureAwait(false);
                await writer.WriteAsync(lisaContent).ConfigureAwait(false);
                await writer.WriteLineAsync().ConfigureAwait(false);
                await writer.WriteAsync(eliaHeader).ConfigureAwait(false);
                await writer.WriteAsync(eliaContent).ConfigureAwait(false);
            }
        }

        private static string SanitizeFileName(string input)
        {
            if (string.IsNullOrEmpty(input)) return "Unknown";
            char[] invalidChars = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                sb.Append(invalidChars.Contains(c) ? '_' : c);
            }
            return sb.ToString();
        }

        private async Task ExtractAndAppendTablesAsync(
            string envSuffix,
            IReadOnlyDictionary<string, string> envQueries,
            IReadOnlyList<string> tablesList,
            string parameterName,
            object parameterValue,
            StringBuilder sb,
            Action<string, DataTable>? onTableExtracted,
            CancellationToken token = default)
        {
            string[] resultsArray = new string[tablesList.Count];

            using (var semaphore = new SemaphoreSlim(10))
            {
                var tasks = new List<Task>(tablesList.Count);
                var db = new DatabaseManager(envSuffix);
                var queryParams = new Dictionary<string, object> { { parameterName, parameterValue } };

                async Task ExtractSingleTableAsync(int index, string table)
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();

                        await semaphore.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            token.ThrowIfCancellationRequested();

                            if (envQueries.TryGetValue(table, out string? sql) && !string.IsNullOrEmpty(sql))
                            {
                                using (var dt = await db.GetDataAsync(sql, queryParams, token).ConfigureAwait(false))
                                {
                                    token.ThrowIfCancellationRequested();

                                    onTableExtracted?.Invoke(table, dt);

                                    var tempSb = new StringBuilder();
                                    CsvFormatter.AddTableToBuffer(tempSb, table, dt);
                                    resultsArray[index] = tempSb.ToString();
                                }
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        resultsArray[index] = $"### TABLE : {table} | EXTRACTION ERROR\nSQL Error: {ex.Message}\n";
                    }
                }

                for (int i = 0; i < tablesList.Count; i++)
                {
                    tasks.Add(ExtractSingleTableAsync(i, tablesList[i]));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            foreach (var content in resultsArray)
            {
                if (!string.IsNullOrEmpty(content))
                {
                    sb.Append(content);
                }
            }
        }
    }
}