using AutoActivator.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AutoActivator.Services
{
    public sealed partial class BatchActivationService
    {
        private static async Task WriteBatchReportAsync(
            string reportPath,
            DateTime batchStartTime,
            long totalBatchMilliseconds,
            bool skipPrime,
            string environment,
            string defaultProcess,
            string cus,
            string bucp,
            string cmdpmt,
            IReadOnlyList<ContractActivationTrace> orderedTraces,
            bool isCancelled,
            int successCount,
            int alreadyActiveCount,
            int errorCount,
            ActivationInputMode inputMode)
        {
            var builder = new StringBuilder(32768);
            int totalProcessed = orderedTraces.Count;
            double averageSeconds = totalProcessed > 0
                ? totalBatchMilliseconds / 1000.0 / totalProcessed
                : 0.0;
            double activeRate = totalProcessed > 0
                ? Math.Round((double)(successCount + alreadyActiveCount) / totalProcessed * 100.0, 1)
                : 0.0;

            int br23Count = orderedTraces.Count(trace => trace.IsBranch23);
            int br21Count = totalProcessed - br23Count;

            builder.AppendLine(
                "=========================================================================================================");
            builder.AppendLine(
                "               AUTO-ACTIVATOR - DETAILED BATCH ACTIVATION REPORT");
            builder.AppendLine(
                "=========================================================================================================");
            builder.AppendLine(
                $"Execution Date     : {batchStartTime:yyyy-MM-dd HH:mm:ss} -> {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine(
                $"Total Batch Time   : {totalBatchMilliseconds / 1000.0:F2}s ({averageSeconds:F2}s average/item)");
            builder.AppendLine(
                $"Batch State        : {(isCancelled ? "CANCELLED, PARTIAL REPORT" : "COMPLETED")}");
            builder.AppendLine(
                $"Environment        : {environment}000");
            builder.AppendLine(
                $"ESCWA Node         : {MicroFocusApiService.ActiveServer ?? "N/A"}");
            builder.AppendLine(
                $"Input Mode         : {inputMode}");
            builder.AppendLine(
                $"Default Process    : {defaultProcess}");
            builder.AppendLine(
                $"Business Routing   : CUS='{cus}' | Default BUCP='{(bucp ?? string.Empty).PadLeft(5, '0')}' | " +
                $"Default CMDPMT='{cmdpmt}' | Per-row SQL routing={(inputMode.UsesSqlAutocomplete() ? "ENABLED" : "DISABLED")} | " +
                $"Skip Prime={(skipPrime ? "YES" : "NO")}");
            builder.AppendLine(
                $"Branches Breakdown : Branche 21 = {br21Count} | Branche 23 = {br23Count}");
            builder.AppendLine(
                "---------------------------------------------------------------------------------------------------------");
            builder.AppendLine(
                $"Summary            : Processed={totalProcessed} | Activated={successCount} | " +
                $"Already Active={alreadyActiveCount} | Failed={errorCount} | Active Rate={activeRate:F1}%");
            builder.AppendLine(
                "=========================================================================================================");
            builder.AppendLine();

            AppendOverviewSection(builder, orderedTraces);
            AppendDiagnosticSection(builder, orderedTraces);
            AppendSuccessfulSection(builder, orderedTraces);

            using var writer = new StreamWriter(
                reportPath,
                append: false,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            await writer.WriteAsync(builder.ToString()).ConfigureAwait(false);
        }

        private static void AppendOverviewSection(
            StringBuilder builder,
            IReadOnlyList<ContractActivationTrace> traces)
        {
            builder.AppendLine(
                "=========================================================================================================");
            builder.AppendLine(
                "SECTION 1 - QUICK OVERVIEW (WITH EXACT EXCEL ROW & BRANCH 21/23)");
            builder.AppendLine(
                "=========================================================================================================");
            builder.AppendLine(
                "ExcelRow | Input / Demand   | Contract        | JCL ID    | " +
                "Product | Proc | Ch | Canal          | BUCP  | CMD | Branche    | Premium    | Duration | Status");
            builder.AppendLine(
                "---------+------------------+-----------------+-----------+" +
                "---------+------+----+----------------+-------+-----+------------+------------+----------+----------------");

            foreach (ContractActivationTrace trace in traces)
            {
                string duration = $"{trace.TotalDurationMs / 1000.0:F1}s";
                string status = trace.FinalStatus switch
                {
                    "SUCCESS" => "SUCCESS",
                    "ALREADY_ACTIVE" => "ALREADY ACTIVE",
                    "CANCELLED" => "CANCELLED",
                    _ => "FAILED: " + trace.FailureStage
                };

                builder.AppendLine(
                    $"#{trace.RowNum,-7} | " +
                    $"{Truncate(trace.RawInput, 16),-16} | " +
                    $"{Truncate(trace.ResolvedContract, 15),-15} | " +
                    $"{Truncate(trace.JclContract, 9),-9} | " +
                    $"{Truncate(trace.ProductCode, 7),-7} | " +
                    $"{Truncate(trace.Channel, 4),-4} | " +
                    $"{Truncate(trace.EliaChannelCode, 2),-2} | " +
                    $"{Truncate(ResolveBusinessChannelName(trace.EliaChannelCode), 14),-14} | " +
                    $"{Truncate(trace.Bucp, 5),-5} | " +
                    $"{Truncate(trace.Cmdpmt, 3),-3} | " +
                    $"{Truncate(trace.BranchDisplay, 10),-10} | " +
                    $"{Truncate(trace.RawPremium, 10),-10} | " +
                    $"{duration,8} | " +
                    status);
            }

            builder.AppendLine();
        }

        private static void AppendDiagnosticSection(
            StringBuilder builder,
            IReadOnlyList<ContractActivationTrace> traces)
        {
            List<ContractActivationTrace> nonActivated = traces
                .Where(trace => !string.Equals(trace.FinalStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                .ToList();

            builder.AppendLine(
                "=========================================================================================================");
            builder.AppendLine(
                $"SECTION 2 - NON-ACTIVATED DIAGNOSTICS ({nonActivated.Count})");
            builder.AppendLine(
                "=========================================================================================================");

            if (nonActivated.Count == 0)
            {
                builder.AppendLine("All processed items completed successfully.");
                builder.AppendLine();
                return;
            }

            foreach (ContractActivationTrace trace in nonActivated)
            {
                ActivationDataService.AppendContractTraceDetails(
                    builder,
                    trace,
                    includeHeaderBanner: true);
            }
        }

        private static void AppendSuccessfulSection(
            StringBuilder builder,
            IReadOnlyList<ContractActivationTrace> traces)
        {
            List<ContractActivationTrace> successful = traces
                .Where(trace => string.Equals(trace.FinalStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                .ToList();

            builder.AppendLine(
                "=========================================================================================================");
            builder.AppendLine(
                $"SECTION 3 - SUCCESSFUL EXECUTION TIMELINES ({successful.Count})");
            builder.AppendLine(
                "=========================================================================================================");

            if (successful.Count == 0)
            {
                builder.AppendLine("No item completed with SUCCESS.");
                return;
            }

            foreach (ContractActivationTrace trace in successful)
            {
                ActivationDataService.AppendContractTraceDetails(
                    builder,
                    trace,
                    includeHeaderBanner: true);
            }
        }

        private static string BuildReportPath(
            string outputDirectory,
            bool skipPrime,
            bool cancelled,
            bool includeUniqueSuffix = false)
        {
            string skipSuffix = skipPrime ? "_SKIP_PRIME" : string.Empty;
            string cancellationSuffix = cancelled ? "_PARTIAL_CANCELLED" : string.Empty;
            string uniqueSuffix = includeUniqueSuffix
                ? "_" + Guid.NewGuid().ToString("N").Substring(0, 6)
                : string.Empty;

            string fileName =
                $"Activation_Batch{skipSuffix}{cancellationSuffix}_{DateTime.Now:yyyyMMdd_HHmmss}{uniqueSuffix}.txt";

            return Path.Combine(outputDirectory, fileName);
        }

        private static string Truncate(string? value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "-";
            }

            if (maximumLength <= 1)
            {
                return value.Substring(0, 1);
            }

            return value.Length <= maximumLength
                ? value
                : value.Substring(0, maximumLength - 1) + "…";
        }
    }
}
