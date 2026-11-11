using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
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
            CancellationToken token = default,
            Action<int, int, string>? onStepProgress = null)
        {
            if (string.IsNullOrWhiteSpace(targetContract))
                throw new ArgumentException("The input value is empty.", nameof(targetContract));

            token.ThrowIfCancellationRequested();

            envSuffix = Settings.NormalizeEnvironmentSuffix(envSuffix);
            string cleanedInput = targetContract.Replace("\u00A0", "").Replace("\uFEFF", "").Trim();
            onStepProgress?.Invoke(1, 5, $"Résolution SQL Contrat / Demand ID ({cleanedInput}) en {envSuffix}...");
            var db = new DatabaseManager(envSuffix);
            var envQueries = SqlQueries.GetQueries(envSuffix);

            string cleanedContract = cleanedInput;
            string eliaUconId = "Not found";
            string eliaDemandId = "Not found";
            string internalIdString = "Not found";
            string premiumAmount = "0";
            string productCode = string.Empty;
            string processCode = string.Empty;
            string channelCode = string.Empty;
            string demandStatus = string.Empty;
            string prctBucp = string.Empty;
            string prctCmdpmt = string.Empty;
            string lisaMvtState = string.Empty;
            DateTime? firstPaymentDate = null;
            DateTime? paymentDate = null;
            DateTime? contractStartDate = null;
            DateTime? demandCreationDate = null;
            var discoveredFunds = new ConcurrentBag<string>();
            int fundFeatureCount = 0;
            int tablesExtractedCount = 0;
            long? internalIdValue = null;

            // 1. Resolve full context (DemandId, UconId, ContractNumber, InternalId, GrossPremium, Product, Process, Channel, FundFeatureCount)
            bool canQueryAsDemand = decimal.TryParse(
                cleanedInput,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _);
            bool effectiveFirstIsDemand = isDemandId && canQueryAsDemand;

            string contextQueryKey = effectiveFirstIsDemand ? "GET_FULL_CONTEXT_BY_DEMAND" : "GET_FULL_CONTEXT_BY_CONTRACT";
            string contextParamName = effectiveFirstIsDemand ? "@DemandId" : "@ContractNumber";

            DataTable dtContext = new DataTable();
            try
            {
                dtContext = await db.GetDataAsync(
                    envQueries[contextQueryKey],
                    new Dictionary<string, object> { { contextParamName, cleanedInput } },
                    token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[ExtractionService] Primary context query warning: {ex.Message}");
            }

            // Auto-detect fallback: if nothing was found with the selected mode, try the opposite mode (Demand ID <-> Contract Number)
            if (dtContext.Rows.Count == 0 && (!effectiveFirstIsDemand ? canQueryAsDemand : true))
            {
                dtContext.Dispose();
                dtContext = new DataTable();
                string alternateQueryKey = !effectiveFirstIsDemand ? "GET_FULL_CONTEXT_BY_DEMAND" : "GET_FULL_CONTEXT_BY_CONTRACT";
                string alternateParamName = !effectiveFirstIsDemand ? "@DemandId" : "@ContractNumber";
                try
                {
                    dtContext = await db.GetDataAsync(
                        envQueries[alternateQueryKey],
                        new Dictionary<string, object> { { alternateParamName, cleanedInput } },
                        token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine($"[ExtractionService] Alternate context query warning: {ex.Message}");
                }
            }

            using (dtContext)
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

                    if (dtContext.Columns.Contains("ProductCode") && ctxRow["ProductCode"] != DBNull.Value)
                    {
                        productCode = ctxRow["ProductCode"]?.ToString()?.Trim()?.ToUpperInvariant() ?? string.Empty;
                    }

                    if (dtContext.Columns.Contains("ProcessCode") && ctxRow["ProcessCode"] != DBNull.Value)
                    {
                        processCode = ctxRow["ProcessCode"]?.ToString()?.Trim()?.ToUpperInvariant() ?? string.Empty;
                    }

                    if (dtContext.Columns.Contains("ChannelCode") && ctxRow["ChannelCode"] != DBNull.Value)
                    {
                        channelCode = ctxRow["ChannelCode"]?.ToString()?.Trim()?.ToUpperInvariant() ?? string.Empty;
                    }

                    if (dtContext.Columns.Contains("DemandStatus") && ctxRow["DemandStatus"] != DBNull.Value)
                    {
                        demandStatus = ctxRow["DemandStatus"]?.ToString()?.Trim()?.ToUpperInvariant() ?? string.Empty;
                    }

                    if (dtContext.Columns.Contains("FundFeatureCount") && ctxRow["FundFeatureCount"] != DBNull.Value &&
                        int.TryParse(ctxRow["FundFeatureCount"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fCount))
                    {
                        fundFeatureCount = fCount;
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
                            premiumAmount = FormatDecimalString(gPrem);
                    }

                    if (dtContext.Columns.Contains("FirstPaymentDate") && ctxRow["FirstPaymentDate"] != DBNull.Value)
                    {
                        firstPaymentDate = TryParseSqlDate(ctxRow["FirstPaymentDate"]);
                    }

                    if (dtContext.Columns.Contains("PaymentDate") && ctxRow["PaymentDate"] != DBNull.Value)
                    {
                        paymentDate = TryParseSqlDate(ctxRow["PaymentDate"]);
                    }

                    if (dtContext.Columns.Contains("ContractStartDate") && ctxRow["ContractStartDate"] != DBNull.Value)
                    {
                        contractStartDate = TryParseSqlDate(ctxRow["ContractStartDate"]);
                    }
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

            // 2b. Check LISA Contract Database (LV000SD0) if SCNTT0 was not found in default application database
            if (!internalIdValue.HasValue && !string.IsNullOrWhiteSpace(cleanedContract))
            {
                try
                {
                    string lisaContractDbName = Settings.DbConfig.GetLisaContractDatabase(envSuffix);
                    if (!string.Equals(lisaContractDbName, Settings.DbConfig.GetApplicationDatabase(envSuffix), StringComparison.OrdinalIgnoreCase))
                    {
                        var lisaContractDb = new DatabaseManager(envSuffix, lisaContractDbName);
                        using var dtLisaContract = await lisaContractDb.GetDataAsync(
                            envQueries["GET_INTERNAL_ID"],
                            new Dictionary<string, object> { { "@ContractNumber", cleanedContract } },
                            token).ConfigureAwait(false);
                        if (dtLisaContract.Rows.Count > 0 && dtLisaContract.Rows[0]["NO_CNT"] != DBNull.Value)
                        {
                            internalIdValue = Convert.ToInt64(dtLisaContract.Rows[0]["NO_CNT"]);
                            internalIdString = internalIdValue.Value.ToString();
                        }
                    }
                }
                catch
                {
                    // Non-blocking fallback
                }
            }

            if (!internalIdValue.HasValue && eliaUconId == "Not found")
            {
                throw new Exception($"Contract {cleanedContract} not found. Verify that you are targeting the correct database ({envSuffix} — {Settings.DbConfig.GetServerName(envSuffix)}).");
            }

            string jclContract = ResolveJclContract(cleanedContract, internalIdString);

            var sbLisa = new StringBuilder();
            var sbElia = new StringBuilder();

            #region LISA SECTION
            token.ThrowIfCancellationRequested();
            onStepProgress?.Invoke(2, 5, $"Extraction Tables LISA (LV.*) pour {cleanedContract} (NO_CNT={internalIdString})...");

            if (internalIdValue.HasValue)
            {
                await ExtractAndAppendTablesAsync(
                    envSuffix,
                    envQueries,
                    SqlQueries.LisaTables,
                    "@InternalId",
                    internalIdValue.Value,
                    sbLisa,
                    (tableName, dt) =>
                    {
                        if (dt.Rows.Count > 0)
                            Interlocked.Increment(ref tablesExtractedCount);

                        if (tableName.Equals("LV.PRCTT0", StringComparison.OrdinalIgnoreCase) && dt.Rows.Count > 0)
                        {
                            DataRow lastRow = dt.Rows[dt.Rows.Count - 1];
                            if (dt.Columns.Contains("NO_BISM_BUCP") && lastRow["NO_BISM_BUCP"] != DBNull.Value)
                            {
                                string? b = lastRow["NO_BISM_BUCP"]?.ToString()?.Trim();
                                if (!string.IsNullOrWhiteSpace(b))
                                    prctBucp = b.PadLeft(5, '0');
                            }
                            if (dt.Columns.Contains("C_MD_PMT") && lastRow["C_MD_PMT"] != DBNull.Value)
                            {
                                string? c = lastRow["C_MD_PMT"]?.ToString()?.Trim();
                                if (!string.IsNullOrWhiteSpace(c))
                                    prctCmdpmt = c.ToUpperInvariant();
                            }
                            if ((premiumAmount == "0" || premiumAmount == "0.00") &&
                                dt.Columns.Contains("M_PAY") &&
                                lastRow["M_PAY"] != DBNull.Value)
                            {
                                string? m = lastRow["M_PAY"]?.ToString()?.Trim();
                                if (!string.IsNullOrWhiteSpace(m))
                                    premiumAmount = FormatDecimalString(m);
                            }
                        }
                        else if (tableName.Equals("LV.FMVDT0", StringComparison.OrdinalIgnoreCase) && dt.Rows.Count > 0)
                        {
                            foreach (DataRow row in dt.Rows)
                            {
                                if (dt.Columns.Contains("C_FONDS") && row["C_FONDS"] != DBNull.Value)
                                {
                                    string? f = row["C_FONDS"]?.ToString()?.Trim();
                                    if (!string.IsNullOrWhiteSpace(f))
                                        discoveredFunds.Add(f.ToUpperInvariant());
                                }
                                if (dt.Columns.Contains("C_ETAT_MVT_FONDS") && row["C_ETAT_MVT_FONDS"] != DBNull.Value)
                                {
                                    string? st = row["C_ETAT_MVT_FONDS"]?.ToString()?.Trim();
                                    if (!string.IsNullOrWhiteSpace(st))
                                        lisaMvtState = $"Mvt État {st}";
                                }
                            }
                        }
                        else if (tableName.Equals("LV.FMVGT0", StringComparison.OrdinalIgnoreCase) && dt.Rows.Count > 0 && string.IsNullOrWhiteSpace(lisaMvtState))
                        {
                            DataRow lastRow = dt.Rows[dt.Rows.Count - 1];
                            if (dt.Columns.Contains("C_ETAT_MVT_FONDS") && lastRow["C_ETAT_MVT_FONDS"] != DBNull.Value)
                            {
                                string? st = lastRow["C_ETAT_MVT_FONDS"]?.ToString()?.Trim();
                                if (!string.IsNullOrWhiteSpace(st))
                                    lisaMvtState = $"Mvt État {st}";
                            }
                        }
                    },
                    token).ConfigureAwait(false);
            }
            else
            {
                sbLisa.AppendLine($"### DIAGNOSTIC : Contract {cleanedContract} missing in LISA ({Settings.DbConfig.GetApplicationDatabase(envSuffix)}) ###");
            }
            #endregion

            #region ELIA SECTION
            token.ThrowIfCancellationRequested();
            onStepProgress?.Invoke(3, 5, $"Extraction Tables ELIA (FJ1.*) pour {cleanedContract} (UCON={eliaUconId})...");

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

                await ExtractAndAppendTablesAsync(
                    envSuffix,
                    envQueries,
                    SqlQueries.EliaTables,
                    "@EliaId",
                    eliaUconId,
                    sbElia,
                    (tableName, dt) =>
                    {
                        if (dt.Rows.Count > 0)
                            Interlocked.Increment(ref tablesExtractedCount);

                        if (tableName.Equals("FJ1.TB5UCON", StringComparison.OrdinalIgnoreCase) &&
                            dt.Rows.Count > 0)
                        {
                            if (!contractStartDate.HasValue && dt.Columns.Contains("IT5UCONDDEB"))
                            {
                                contractStartDate = TryParseSqlDate(dt.Rows[0]["IT5UCONDDEB"]);
                            }
                            if (string.Equals(cleanedContract, cleanedInput, StringComparison.OrdinalIgnoreCase) &&
                                dt.Columns.Contains("IT5UCONLREFEXN"))
                            {
                                foreach (DataRow uRow in dt.Rows)
                                {
                                    string? refExn = uRow["IT5UCONLREFEXN"]?.ToString()?.Trim();
                                    if (!string.IsNullOrWhiteSpace(refExn))
                                    {
                                        cleanedContract = refExn;
                                        break;
                                    }
                                }
                            }
                        }
                        else if (tableName.Equals("FJ1.TB5UPRP", StringComparison.OrdinalIgnoreCase) &&
                            dt.Rows.Count > 0)
                        {
                            if (dt.Columns.Contains("IT5UPRPUBRU"))
                            {
                                string? rawPrem = dt.Rows[0]["IT5UPRPUBRU"]?.ToString()?.Trim();
                                if (!string.IsNullOrEmpty(rawPrem))
                                    premiumAmount = FormatDecimalString(rawPrem);
                            }
                            if (!paymentDate.HasValue && dt.Columns.Contains("IT5UPRPDVRS"))
                            {
                                paymentDate = TryParseSqlDate(dt.Rows[0]["IT5UPRPDVRS"]);
                            }
                            if (!firstPaymentDate.HasValue && dt.Columns.Contains("IT5UPRPDPMRPAI"))
                            {
                                firstPaymentDate = TryParseSqlDate(dt.Rows[0]["IT5UPRPDPMRPAI"]);
                            }
                        }
                        else if (tableName.Equals("FJ1.TB5UGFF", StringComparison.OrdinalIgnoreCase) &&
                                 dt.Rows.Count > 0)
                        {
                            if (fundFeatureCount == 0)
                                fundFeatureCount = dt.Rows.Count;
                            foreach (DataRow row in dt.Rows)
                            {
                                if (dt.Columns.Contains("IT5UGFFCFND") && row["IT5UGFFCFND"] != DBNull.Value)
                                {
                                    string? f1 = row["IT5UGFFCFND"]?.ToString()?.Trim();
                                    if (!string.IsNullOrWhiteSpace(f1))
                                        discoveredFunds.Add(f1.ToUpperInvariant());
                                }
                                if (dt.Columns.Contains("IT5UGFFCFNDDSI") && row["IT5UGFFCFNDDSI"] != DBNull.Value)
                                {
                                    string? f2 = row["IT5UGFFCFNDDSI"]?.ToString()?.Trim();
                                    if (!string.IsNullOrWhiteSpace(f2))
                                        discoveredFunds.Add(f2.ToUpperInvariant());
                                }
                            }
                        }
                        else if (tableName.Equals("FJ1.TB5UPRF", StringComparison.OrdinalIgnoreCase) &&
                                 dt.Rows.Count > 0)
                        {
                            if (fundFeatureCount == 0)
                                fundFeatureCount = dt.Rows.Count;
                            foreach (DataRow row in dt.Rows)
                            {
                                if (dt.Columns.Contains("IT5UPRFCFND") && row["IT5UPRFCFND"] != DBNull.Value)
                                {
                                    string? f = row["IT5UPRFCFND"]?.ToString()?.Trim();
                                    if (!string.IsNullOrWhiteSpace(f))
                                        discoveredFunds.Add(f.ToUpperInvariant());
                                }
                            }
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
                        (tableName, dt) =>
                        {
                            if (dt.Rows.Count > 0)
                                Interlocked.Increment(ref tablesExtractedCount);

                            if (tableName.Equals("FJ1.TB5HDMD", StringComparison.OrdinalIgnoreCase) &&
                                dt.Rows.Count > 0)
                            {
                                DataRow lastDemandRow = dt.Rows[dt.Rows.Count - 1];
                                if (string.IsNullOrWhiteSpace(productCode) && dt.Columns.Contains("IT5HDMDCPRD"))
                                    productCode = lastDemandRow["IT5HDMDCPRD"]?.ToString()?.Trim()?.ToUpperInvariant() ?? string.Empty;
                                if (string.IsNullOrWhiteSpace(processCode) && dt.Columns.Contains("IT5HDMDCPCS"))
                                    processCode = lastDemandRow["IT5HDMDCPCS"]?.ToString()?.Trim()?.ToUpperInvariant() ?? string.Empty;
                                if (string.IsNullOrWhiteSpace(channelCode) && dt.Columns.Contains("IT5HDMDCCDI"))
                                    channelCode = lastDemandRow["IT5HDMDCCDI"]?.ToString()?.Trim()?.ToUpperInvariant() ?? string.Empty;
                                if (string.IsNullOrWhiteSpace(demandStatus) && dt.Columns.Contains("IT5HDMDCSTA"))
                                    demandStatus = lastDemandRow["IT5HDMDCSTA"]?.ToString()?.Trim()?.ToUpperInvariant() ?? string.Empty;
                                if (!demandCreationDate.HasValue && dt.Columns.Contains("IT5HDMDDCRE"))
                                    demandCreationDate = TryParseSqlDate(lastDemandRow["IT5HDMDDCRE"]);
                            }
                        },
                        token).ConfigureAwait(false);
                }
            }
            else
            {
                sbElia.AppendLine($"### ELIA SECTION : NO DATA FOUND IN {Settings.DbConfig.GetLifeDatabase(envSuffix)} ###");
            }
            #endregion

            token.ThrowIfCancellationRequested();

            List<string> uniqueFunds = discoveredFunds
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool isBranch23 =
                Settings.PreActivationRouting.IsKnownBranch23Product(productCode) ||
                fundFeatureCount > 0 ||
                uniqueFunds.Count > 0;
            string branchDisplay = isBranch23 ? "Branche 23" : "Branche 21";
            string branchDetectionReason = fundFeatureCount > 0 || uniqueFunds.Count > 0
                ? $"ELIA TB5UGFF/TB5UPRF ({Math.Max(fundFeatureCount, uniqueFunds.Count)} fonds/feature(s))"
                : Settings.PreActivationRouting.IsKnownBranch23Product(productCode)
                    ? $"ProductCode '{productCode}' référencé en Branche 23"
                    : $"ProductCode '{(string.IsNullOrWhiteSpace(productCode) ? "-" : productCode)}' sans fonds (Branche 21 Standard)";

            // Extract latest VNI (LV000GD0.LV.GFCRT0) for discovered Branch 23 funds
            onStepProgress?.Invoke(4, 5, $"Vérification VNI Branche 23 & Routage ({branchDisplay}) pour {cleanedContract}...");
            string latestVniInfo = string.Empty;
            if (uniqueFunds.Count > 0)
            {
                latestVniInfo = await TryExtractFundValuationsAsync(
                        envSuffix,
                        uniqueFunds,
                        sbLisa,
                        () => Interlocked.Increment(ref tablesExtractedCount),
                        token)
                    .ConfigureAwait(false);
            }
            onStepProgress?.Invoke(5, 5, $"Synthèse & écriture du snapshot CSV pour {cleanedContract} ({tablesExtractedCount} tables)...");

            string businessChannel = "-";
            string bucp = "-";
            string cmdpmt = "-";
            string routingRule = "-";

            string routingKey = !string.IsNullOrWhiteSpace(channelCode) ? channelCode : processCode;
            var confirmedRouting = PreActivationRoutingService.ResolveConfirmedPaymentRouting(routingKey);
            if (!string.IsNullOrWhiteSpace(confirmedRouting.Bucp))
            {
                businessChannel = confirmedRouting.BusinessChannel;
                bucp = confirmedRouting.Bucp;
                cmdpmt = confirmedRouting.Cmdpmt;
                routingRule = AutomaticActivationPlan.ResolveRouting(routingKey).Rule;
            }

            if (!string.IsNullOrWhiteSpace(prctBucp))
            {
                bucp = prctBucp;
            }
            if (!string.IsNullOrWhiteSpace(prctCmdpmt))
            {
                cmdpmt = prctCmdpmt;
            }
            if (businessChannel == "-" && bucp != "-")
            {
                businessChannel = bucp switch
                {
                    "00382" or "00182" => "BANK",
                    "80819" => "BROKER_NEW_GEN",
                    "49797" => "BROKER_LEGACY",
                    _ => "-"
                };
            }

            bool validDvrs = paymentDate.HasValue &&
                             (!contractStartDate.HasValue || paymentDate.Value.Date >= contractStartDate.Value.Date) &&
                             (!firstPaymentDate.HasValue || paymentDate.Value.Date >= firstPaymentDate.Value.Date);
            DateTime? resolvedRunDate = validDvrs
                ? paymentDate
                : (firstPaymentDate ?? contractStartDate ?? demandCreationDate ?? paymentDate);
            string runDateSource = validDvrs
                ? "TB5UPRP.IT5UPRPDVRS (DateVersement)"
                : firstPaymentDate.HasValue
                    ? "TB5UPRP.IT5UPRPDPMRPAI (DatePremierePrime)"
                    : contractStartDate.HasValue
                        ? "TB5UCON.IT5UCONDDEB (DateDebutContrat)"
                        : demandCreationDate.HasValue
                            ? "TB5HDMD.IT5HDMDDCRE"
                            : paymentDate.HasValue
                                ? "TB5UPRP.IT5UPRPDVRS"
                                : "-";
            string runDateDisplay = resolvedRunDate.HasValue
                ? resolvedRunDate.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                : "-";
            string consolidationDateDisplay = resolvedRunDate.HasValue
                ? resolvedRunDate.Value.AddDays(2).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                : "-";
            string amountJcl = decimal.TryParse(premiumAmount, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsedPrem) && parsedPrem > 0m
                ? AutomaticActivationPlan.FormatAmountForJcl(parsedPrem)
                : "-";

            string fundsSummary = "-";
            if (uniqueFunds.Count > 0)
            {
                var parts = new List<string> { string.Join(", ", uniqueFunds) };
                if (!string.IsNullOrWhiteSpace(lisaMvtState))
                    parts.Add(lisaMvtState);
                if (!string.IsNullOrWhiteSpace(latestVniInfo))
                    parts.Add(latestVniInfo);
                fundsSummary = string.Join(" | ", parts);
            }
            else if (isBranch23 && !string.IsNullOrWhiteSpace(lisaMvtState))
            {
                fundsSummary = lisaMvtState;
            }

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

                string lisaHeader =
                    "================================================================================\n" +
                    "=== SYNTHÈSE AUTOCOMPLETE SQL & ROUTAGE MÉTIER ===\n" +
                    $"Contrat (3-7-2)      : {cleanedContract} | JCL (9 chiffres / NO_CNT) : {jclContract} | LISA ID : {internalIdString}\n" +
                    $"Demand ID / ELIA ID  : {eliaDemandId} / UCON {eliaUconId} (Statut Demand : {(string.IsNullOrWhiteSpace(demandStatus) ? "-" : demandStatus)})\n" +
                    $"Produit / Process    : {(string.IsNullOrWhiteSpace(productCode) ? "-" : productCode)} / {(string.IsNullOrWhiteSpace(processCode) ? "-" : processCode)} (ChannelCode ELIA : {(string.IsNullOrWhiteSpace(channelCode) ? "-" : channelCode)})\n" +
                    $"Canal & Paiement     : {businessChannel} -> BUCP = {bucp} | CMDPMT = {cmdpmt} ({routingRule})\n" +
                    $"Prime Brute / JCL    : {premiumAmount} € -> AMOUNT = {amountJcl}\n" +
                    $"Date N (Run Date)    : {runDateDisplay} ({runDateSource}) | Date N+2 (Consolidation BR23) : {consolidationDateDisplay}\n" +
                    $"Branche & Fonds      : {branchDisplay} ({branchDetectionReason}) | Fonds : {fundsSummary}\n" +
                    "================================================================================\n" +
                    $"=== SECTION LISA (INTERNAL ID: {internalIdString} | JCL ID: {jclContract} | BUCP/CMD: {bucp}/{cmdpmt} | BRANCH: {branchDisplay} | FONDS: {fundsSummary} | DB: {Settings.DbConfig.GetApplicationDatabase(envSuffix)}) ===\n" +
                    "================================================================================\n";
                string eliaHeader =
                    "================================================================================\n" +
                    $"=== SECTION ELIA (UCON ID: {eliaUconId} | DEMAND: {eliaDemandId} | PRODUCT: {(string.IsNullOrWhiteSpace(productCode) ? "-" : productCode)} | PROCESS: {(string.IsNullOrWhiteSpace(processCode) ? "-" : processCode)} | CANAL: {businessChannel} ({(string.IsNullOrWhiteSpace(channelCode) ? "-" : channelCode)}) | PRIME: {premiumAmount} | BRANCH: {branchDisplay} | DB: {Settings.DbConfig.GetLifeDatabase(envSuffix)}) ===\n" +
                    "================================================================================\n";

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
                JclContract = jclContract,
                FilePath = savedFilePath,
                StatusMessage = $"Extraction saved | ID: {internalIdString} | {branchDisplay} | {fundsSummary}",
                InternalId = internalIdString,
                UconId = eliaUconId,
                DemandId = eliaDemandId,
                LisaContent = finalLisaContent,
                EliaContent = finalEliaContent,
                Premium = premiumAmount,
                AmountJcl = amountJcl,
                ProductCode = string.IsNullOrWhiteSpace(productCode) ? "-" : productCode,
                ProcessCode = string.IsNullOrWhiteSpace(processCode) ? "-" : processCode,
                ChannelCode = string.IsNullOrWhiteSpace(channelCode) ? "-" : channelCode,
                BusinessChannel = businessChannel,
                Bucp = bucp,
                Cmdpmt = cmdpmt,
                RoutingRule = routingRule,
                RunDate = runDateDisplay,
                RunDateSource = runDateSource,
                ConsolidationDate = consolidationDateDisplay,
                BranchDetectionReason = branchDetectionReason,
                DemandStatus = string.IsNullOrWhiteSpace(demandStatus) ? "-" : demandStatus,
                FundFeatureCount = fundFeatureCount,
                FundsSummary = fundsSummary,
                IsBranch23 = isBranch23,
                BranchDisplay = branchDisplay,
                TablesExtractedCount = tablesExtractedCount
            };
        }

        private static async Task<string> TryExtractFundValuationsAsync(
            string envSuffix,
            IReadOnlyList<string> funds,
            StringBuilder sbLisa,
            Action onTableAdded,
            CancellationToken token)
        {
            if (funds == null || funds.Count == 0)
                return string.Empty;

            try
            {
                string valuationDbName = Settings.DbConfig.GetFundValuationDatabase(envSuffix);
                var valuationDb = new DatabaseManager(envSuffix, valuationDbName);

                var parameters = new Dictionary<string, object>();
                var paramNames = new List<string>();
                for (int i = 0; i < funds.Count; i++)
                {
                    string pName = $"@f{i}";
                    paramNames.Add(pName);
                    parameters[pName] = funds[i];
                }

                string inClause = string.Join(", ", paramNames);
                string sql = $@"
SELECT TOP (30)
    LTRIM(RTRIM(C_FONDS)) AS C_FONDS,
    D_COURS,
    C_VALID_COURS,
    M_UNT_BANQ,
    D_INTRO_COURS,
    LTRIM(RTRIM(C_DEV)) AS C_DEV,
    LTRIM(RTRIM(NM_JOB_DMOD)) AS NM_JOB_DMOD
FROM [LV].[GFCRT0] WITH (NOLOCK)
WHERE LTRIM(RTRIM(C_FONDS)) IN ({inClause})
ORDER BY C_FONDS ASC, D_COURS DESC;";

                using DataTable dtVni = await valuationDb.GetDataAsync(sql, parameters, token).ConfigureAwait(false);
                if (dtVni.Rows.Count > 0)
                {
                    onTableAdded();
                    CsvFormatter.AddTableToBuffer(sbLisa, "LV.GFCRT0 (VNI FONDS)", dtVni);
                    DataRow latestRow = dtVni.Rows[0];
                    string latestDate = latestRow["D_COURS"] is DateTime d
                        ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : latestRow["D_COURS"]?.ToString() ?? "-";
                    string latestVal = latestRow["M_UNT_BANQ"] != DBNull.Value
                        ? Convert.ToDecimal(latestRow["M_UNT_BANQ"], CultureInfo.InvariantCulture).ToString("0.000", CultureInfo.InvariantCulture)
                        : "-";
                    return $"VNI {latestDate} ({latestVal})";
                }
            }
            catch
            {
                // Non-blocking if LV000GD0 is not reachable
            }

            return string.Empty;
        }

        private static string ResolveJclContract(string contractReference, string internalId)
        {
            string digitsOnly = new string((contractReference ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digitsOnly.Length == 12)
            {
                return digitsOnly.Substring(1, 9);
            }
            if (digitsOnly.Length == 9)
            {
                return digitsOnly;
            }

            string internalDigits = new string((internalId ?? string.Empty).Where(char.IsDigit).ToArray());
            if (internalDigits.Length == 9)
            {
                return internalDigits;
            }

            return "-";
        }

        private static DateTime? TryParseSqlDate(object? rawValue)
        {
            if (rawValue == null || rawValue == DBNull.Value)
            {
                return null;
            }

            if (rawValue is DateTime dt)
            {
                return dt.Year > 1900 ? dt.Date : null;
            }

            string text = rawValue.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            string[] formats = { "yyyy-MM-dd", "yyyyMMdd", "dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd HH:mm:ss" };
            if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed) ||
                DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                return parsed.Year > 1900 ? parsed.Date : null;
            }

            return null;
        }

        private static string FormatDecimalString(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "0";
            string cleaned = raw.Replace(",", ".").Trim();
            return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed)
                ? parsed.ToString("0.00", CultureInfo.InvariantCulture)
                : raw.Trim();
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
