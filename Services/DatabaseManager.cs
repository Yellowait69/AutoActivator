using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using AutoActivator.Config;

namespace AutoActivator.Services
{
    public class DatabaseManager
    {
        private const int DefaultCommandTimeoutSeconds = 60;

        private const string InsertPaymentQuery = @"
            INSERT INTO LV.PRCTT0 (
                C_STE, NO_CNT, C_MD_PMT, D_REF_PRM, NO_ORD_RCP, TSTAMP_CRT_RCT,
                C_TY_RCT, D_BISM_DVA, D_BISM_DCOR, M_PAY, NM_CP,
                T_ADR_1_CP, T_ADR_2_CP, C_ETAT_RCP, T_COMMU, NO_BUR_SERV,
                NO_AVT, PC_COM, PC_FR_GEST, NO_IBAN_CP, C_BIC_CP,
                NM_AUTEUR_CRT, D_CRT, TY_DMOD, D_ORGN_DEV, C_ORGN_DEV
            ) VALUES (
                'A', @no_cnt, '6', @d_ref, '1', @tstamp,
                '1', @d_ref, @d_ref, @amount, @nom_cp,
                @adr_1, @adr_2, 'B', @commu, @no_bur,
                '0', 0.0245, 0.0105, @iban, @bic,
                @auteur, @d_ref, 'O', @d_ref, 'EUR'
            )";

        private readonly string _connectionString;
        public string EnvironmentName { get; }

        public DatabaseManager(string envSuffix)
        {
            EnvironmentName = Settings.NormalizeEnvironmentSuffix(envSuffix);
            _connectionString = Settings.DbConfig.GetConnectionString(EnvironmentName);
        }

        /// <summary>
        /// Connexion explicite vers une base nommée (ex. LV000PD0_Q000, LV000SD0_Q000).
        /// </summary>
        public DatabaseManager(string envSuffix, string databaseName)
        {
            EnvironmentName = Settings.NormalizeEnvironmentSuffix(envSuffix);
            DatabaseName = databaseName;
            _connectionString = Settings.DbConfig.GetConnectionString(EnvironmentName, databaseName);
        }

        public string DatabaseName { get; } = string.Empty;

        private static void AddParameters(SqlCommand command, Dictionary<string, object> parameters)
        {
            if (parameters == null) return;

            foreach (var param in parameters)
            {
                command.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
            }
        }

        private static void ConfigurePaymentCommand(
            SqlCommand command,
            string contractInternalId,
            decimal amount,
            DateTime? paymentDate,
            string simulatedName,
            string simulatedAddress1,
            string simulatedAddress2,
            string simulatedIban,
            string simulatedBic,
            string bureauNumber,
            string authorId)
        {
            DateTime now = DateTime.Now;
            DateTime referenceDate = paymentDate ?? now;
            DateTime timestamp = (paymentDate.HasValue && paymentDate.Value.TimeOfDay == TimeSpan.Zero)
                ? paymentDate.Value.AddHours(12)
                : now;

            string idStr = contractInternalId?.Trim() ?? string.Empty;
            string fakeCommu = $"820{(idStr.Length > 9 ? idStr.Substring(0, 9) : idStr)}99";

            command.CommandTimeout = DefaultCommandTimeoutSeconds;
            command.Parameters.AddWithValue("@no_cnt", contractInternalId ?? string.Empty);
            command.Parameters.AddWithValue("@d_ref", referenceDate.Date);
            command.Parameters.AddWithValue("@tstamp", timestamp);
            command.Parameters.AddWithValue("@amount", amount);
            command.Parameters.AddWithValue("@commu", fakeCommu);
            command.Parameters.AddWithValue("@nom_cp", simulatedName ?? "TEST AUTOMATION");
            command.Parameters.AddWithValue("@adr_1", simulatedAddress1 ?? "TEST STREET 1");
            command.Parameters.AddWithValue("@adr_2", simulatedAddress2 ?? "1000 BRUSSELS");
            command.Parameters.AddWithValue("@no_bur", bureauNumber ?? "12831");
            command.Parameters.AddWithValue("@iban", simulatedIban ?? "BE47001304609580");
            command.Parameters.AddWithValue("@bic", simulatedBic ?? "GEBABEBB");
            command.Parameters.AddWithValue("@auteur", authorId ?? "AUTO_TEST");
        }

        #region ASYNCHRONOUS METHODS (OPTIMIZED FOR SPEED AND PARALLELISM)

        public async Task<bool> TestConnectionAsync(CancellationToken token = default)
        {
            try
            {
                using (SqlConnection connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync(token).ConfigureAwait(false);
                    using (SqlCommand command = new SqlCommand("SELECT 1", connection) { CommandTimeout = 15 })
                    {
                        var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                        if (result != null && result.ToString() == "1")
                        {
                            Console.WriteLine($"[INFO] Successful connection to the database (Environment: {EnvironmentName})");
                            return true;
                        }
                    }
                }
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SqlException ex)
            {
                Console.WriteLine($"[ERROR] SQL connection failure (Code: {ex.Number}) : {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Connection failure (General error) : {ex.Message}");
                return false;
            }
        }

        public async Task<DataTable> GetDataAsync(string query, Dictionary<string, object> parameters = null, CancellationToken token = default)
        {
            DataTable dataTable = new DataTable();

            try
            {
                using (SqlConnection connection = new SqlConnection(_connectionString))
                using (SqlCommand command = new SqlCommand(query, connection) { CommandTimeout = DefaultCommandTimeoutSeconds })
                {
                    AddParameters(command, parameters);

                    await connection.OpenAsync(token).ConfigureAwait(false);

                    using (SqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        dataTable.Load(reader);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SqlException ex)
            {
                Console.WriteLine($"[ERROR] SQL Error (Code {ex.Number}) on query: {query}\nDetails: {ex.Message}");
                throw new Exception($"DB Error ({ex.Number}) : {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] System Error on query: {query}\nDetails: {ex.Message}");
                throw new Exception($"System Error : {ex.Message}", ex);
            }

            return dataTable;
        }

        public async Task<bool> InjectPaymentAsync(
            string contractInternalId,
            decimal amount,
            DateTime? paymentDate = null,
            string simulatedName = "TEST AUTOMATION",
            string simulatedAddress1 = "TEST STREET 1",
            string simulatedAddress2 = "1000 BRUSSELS",
            string simulatedIban = "BE47001304609580",
            string simulatedBic = "GEBABEBB",
            string bureauNumber = "12831",
            string authorId = "AUTO_TEST",
            CancellationToken token = default)
        {
            try
            {
                using (SqlConnection connection = new SqlConnection(_connectionString))
                using (SqlCommand command = new SqlCommand(InsertPaymentQuery, connection))
                {
                    ConfigurePaymentCommand(
                        command, contractInternalId, amount, paymentDate,
                        simulatedName, simulatedAddress1, simulatedAddress2,
                        simulatedIban, simulatedBic, bureauNumber, authorId);

                    await connection.OpenAsync(token).ConfigureAwait(false);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                    Console.WriteLine($"[INFO] Payment of {amount} EUR successfully injected (Contract: {contractInternalId} | Env: {EnvironmentName})");
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SqlException ex)
            {
                Console.WriteLine($"[ERROR] SQL FAILURE during payment injection (Code: {ex.Number}) : {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] System FAILURE during payment injection : {ex.Message}");
                return false;
            }
        }

        #endregion

        #region SYNCHRONOUS METHODS (KEPT FOR BACKWARD COMPATIBILITY)

        public bool TestConnection()
        {
            try
            {
                using (SqlConnection connection = new SqlConnection(_connectionString))
                {
                    connection.Open();
                    using (SqlCommand command = new SqlCommand("SELECT 1", connection) { CommandTimeout = 15 })
                    {
                        var result = command.ExecuteScalar();
                        if (result != null && result.ToString() == "1")
                        {
                            Console.WriteLine($"[INFO] Successful connection to the database (Environment: {EnvironmentName})");
                            return true;
                        }
                    }
                }
                return false;
            }
            catch (SqlException ex)
            {
                Console.WriteLine($"[ERROR] SQL connection failure (Code: {ex.Number}) : {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Connection failure (General error) : {ex.Message}");
                return false;
            }
        }

        public DataTable GetData(string query, Dictionary<string, object> parameters = null)
        {
            DataTable dataTable = new DataTable();

            try
            {
                using (SqlConnection connection = new SqlConnection(_connectionString))
                using (SqlCommand command = new SqlCommand(query, connection) { CommandTimeout = DefaultCommandTimeoutSeconds })
                {
                    AddParameters(command, parameters);

                    connection.Open();
                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        dataTable.Load(reader);
                    }
                }
            }
            catch (SqlException ex)
            {
                Console.WriteLine($"[ERROR] SQL Error (Code {ex.Number}) on query: {query}\nDetails: {ex.Message}");
                throw new Exception($"DB Error ({ex.Number}) : {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] System Error on query: {query}\nDetails: {ex.Message}");
                throw new Exception($"System Error : {ex.Message}", ex);
            }

            return dataTable;
        }

        public bool InjectPayment(
            string contractInternalId,
            decimal amount,
            DateTime? paymentDate = null,
            string simulatedName = "TEST AUTOMATION",
            string simulatedAddress1 = "TEST STREET 1",
            string simulatedAddress2 = "1000 BRUSSELS",
            string simulatedIban = "BE47001304609580",
            string simulatedBic = "GEBABEBB",
            string bureauNumber = "12831",
            string authorId = "AUTO_TEST")
        {
            try
            {
                using (SqlConnection connection = new SqlConnection(_connectionString))
                using (SqlCommand command = new SqlCommand(InsertPaymentQuery, connection))
                {
                    ConfigurePaymentCommand(
                        command, contractInternalId, amount, paymentDate,
                        simulatedName, simulatedAddress1, simulatedAddress2,
                        simulatedIban, simulatedBic, bureauNumber, authorId);

                    connection.Open();
                    command.ExecuteNonQuery();

                    Console.WriteLine($"[INFO] Payment of {amount} EUR successfully injected (Contract: {contractInternalId} | Env: {EnvironmentName})");
                    return true;
                }
            }
            catch (SqlException ex)
            {
                Console.WriteLine($"[ERROR] SQL FAILURE during payment injection (Code: {ex.Number}) : {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] System FAILURE during payment injection : {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}