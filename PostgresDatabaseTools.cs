using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using ModelContextProtocol.Server;

namespace PostgresMcpServer;

[McpServerToolType]
public static class PostgresDatabaseTools
{
    // Connection retry settings
    private const int MaxRetryAttempts = 3;
    private const int InitialRetryDelayMs = 500;
    private const int CommandTimeoutSeconds = 120; // 2 minutes for long-running queries
    private const int ConnectionTimeoutSeconds = 30;

    // Helper method to get connection string by database name with pooling and resilience settings
    private static string GetConnectionString(string database)
    {
        // Load configuration to get connection string for specified database
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

        // Load credentials from user's home directory
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var credentialsPath = Path.Combine(homeDirectory, ".postgres-mcp-server-creds.json");

        if (File.Exists(credentialsPath))
        {
            try
            {
                // Read the credentials file directly and merge with configuration
                var credentialsJson = File.ReadAllText(credentialsPath);
                var credentialsConfig = JsonSerializer.Deserialize<JsonElement>(credentialsJson);

                // Create a memory configuration source from the credentials
                var credentialsDict = new Dictionary<string, string?>();
                if (credentialsConfig.TryGetProperty("ConnectionStrings", out var credConnectionStrings))
                {
                    foreach (var prop in credConnectionStrings.EnumerateObject())
                    {
                        credentialsDict[$"ConnectionStrings:{prop.Name}"] = prop.Value.GetString();
                    }
                }
                configBuilder.AddInMemoryCollection(credentialsDict);
            }
            catch (Exception)
            {
                // Ignore credentials file loading errors - will fall back to appsettings.json
            }
        }

        var configuration = configBuilder.Build();
        var connectionString = configuration.GetConnectionString(database);

        if (string.IsNullOrEmpty(connectionString))
        {
            throw new ArgumentException($"No connection string found for database '{database}'. Available databases can be found using ListAvailableDatabases()");
        }

        // Build connection string with resilience settings
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            // Connection timeout
            Timeout = ConnectionTimeoutSeconds,
            CommandTimeout = CommandTimeoutSeconds,

            // Connection pooling settings (enabled by default but explicit is better)
            Pooling = true,
            MinPoolSize = 1,  // Keep at least 1 connection warm
            MaxPoolSize = 20, // Allow up to 20 concurrent connections
            ConnectionIdleLifetime = 300, // Close idle connections after 5 minutes
            ConnectionPruningInterval = 10, // Check for idle connections every 10 seconds

            // Keepalive to detect broken connections
            KeepAlive = 30, // Send keepalive every 30 seconds
            TcpKeepAliveTime = 30,
            TcpKeepAliveInterval = 10,

            // Auto-prepare frequently used statements
            MaxAutoPrepare = 10,
            AutoPrepareMinUsages = 2,

            // Reliability settings
            LoadBalanceHosts = false, // Set to true if using multiple hosts
            TargetSessionAttributes = null, // Can be set to "read-write" or "read-only" if needed

            // Performance
            NoResetOnClose = false, // Reset connection state when returned to pool (safer)
            ServerCompatibilityMode = ServerCompatibilityMode.NoTypeLoading // Faster startup
        };

        return builder.ConnectionString;
    }

    // Helper method to check if an exception is transient and should be retried
    private static bool IsTransientException(Exception ex)
    {
        if (ex is NpgsqlException npgEx)
        {
            // PostgreSQL error codes that are typically transient
            return npgEx.SqlState switch
            {
                // Connection errors
                "08000" => true, // connection_exception
                "08003" => true, // connection_does_not_exist
                "08006" => true, // connection_failure
                "08001" => true, // sqlclient_unable_to_establish_sqlconnection
                "08004" => true, // sqlserver_rejected_establishment_of_sqlconnection

                // Serialization/deadlock errors
                "40001" => true, // serialization_failure
                "40P01" => true, // deadlock_detected

                // Resource errors
                "53000" => true, // insufficient_resources
                "53100" => true, // disk_full
                "53200" => true, // out_of_memory
                "53300" => true, // too_many_connections

                _ => false
            };
        }

        // Network-related exceptions
        return ex is System.Net.Sockets.SocketException
            || ex is System.IO.IOException
            || ex is TimeoutException
            || (ex.InnerException != null && IsTransientException(ex.InnerException));
    }

    // Retry logic wrapper for database operations
    private static async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, string operationName)
    {
        int attempt = 0;
        int delay = InitialRetryDelayMs;
        Exception? lastException = null;

        while (attempt < MaxRetryAttempts)
        {
            attempt++;
            try
            {
                return await operation();
            }
            catch (Exception ex)
            {
                lastException = ex;

                // Don't retry non-transient errors
                if (!IsTransientException(ex) || attempt >= MaxRetryAttempts)
                {
                    throw;
                }

                // Log retry attempt
                Console.Error.WriteLine($"[Retry] {operationName} attempt {attempt}/{MaxRetryAttempts} failed: {ex.Message}");
                Console.Error.WriteLine($"[Retry] Waiting {delay}ms before retry...");

                await Task.Delay(delay);

                // Exponential backoff with jitter
                delay = Math.Min(delay * 2 + Random.Shared.Next(0, 100), 5000);
            }
        }

        // This shouldn't be reached, but just in case
        throw lastException ?? new Exception($"Operation {operationName} failed after {MaxRetryAttempts} attempts");
    }

    // Helper method to validate connection health
    private static async Task<bool> ValidateConnectionAsync(NpgsqlConnection connection)
    {
        try
        {
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync();
            }

            // Quick health check query
            using var command = new NpgsqlCommand("SELECT 1", connection)
            {
                CommandTimeout = 5 // Quick timeout for health check
            };
            await command.ExecuteScalarAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Helper method to log exceptions to /tmp/ex
    private static void LogException(Exception ex, string methodName, string sql = "", int? attemptNumber = null)
    {
        try
        {
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Method: {methodName}\n";
            if (attemptNumber.HasValue)
            {
                logEntry += $"Attempt: {attemptNumber}\n";
            }
            logEntry += $"SQL: {sql}\n";
            logEntry += $"Exception Type: {ex.GetType().FullName}\n";
            logEntry += $"Message: {ex.Message}\n";
            if (ex is NpgsqlException npgEx)
            {
                logEntry += $"SQL State: {npgEx.SqlState}\n";
                logEntry += $"Is Transient: {IsTransientException(ex)}\n";
            }
            logEntry += $"Stack Trace: {ex.StackTrace}\n";
            if (ex.InnerException != null)
            {
                logEntry += $"Inner Exception: {ex.InnerException.GetType().FullName} - {ex.InnerException.Message}\n";
            }
            logEntry += new string('-', 80) + "\n\n";

            Console.Error.WriteLine(logEntry);
        }
        catch
        {
            // Ignore logging errors to prevent infinite loops
        }
    }

    [McpServerTool, Description("Execute a SELECT query against the PostgreSQL database and return results as JSON")]
    public static async Task<string> ExecuteQuery(
        string sql,
        [Description("Database name to use (e.g., 'myapp', 'production', 'test')")]
        string database)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            try
            {
                var connectionString = GetConnectionString(database);
                await using var connection = new NpgsqlConnection(connectionString);

                // Validate and open connection
                if (!await ValidateConnectionAsync(connection))
                {
                    throw new InvalidOperationException("Failed to establish valid database connection");
                }

                await using var command = new NpgsqlCommand(sql, connection)
                {
                    CommandTimeout = CommandTimeoutSeconds
                };
                await using var reader = await command.ExecuteReaderAsync();

                var results = new List<Dictionary<string, object?>>();

                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object?>();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var columnName = reader.GetName(i);
                        var value = reader.IsDBNull(i) ? null : reader.GetValue(i);

                        // Convert PostgreSQL types to JSON-serializable types
                        if (value is DateTime dt)
                            value = dt.ToString("yyyy-MM-dd HH:mm:ss");
                        else if (value is decimal dec)
                            value = dec;
                        // PostgreSQL types are generally already JSON-serializable

                        row[columnName] = value;
                    }
                    results.Add(row);
                }

                var response = new
                {
                    success = true,
                    rowCount = results.Count,
                    database = database,
                    data = results
                };

                return JsonSerializer.Serialize(response, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            }
            catch (Exception ex)
            {
                LogException(ex, nameof(ExecuteQuery), sql);

                var errorResponse = new
                {
                    success = false,
                    error = ex.Message,
                    database = database,
                    sqlState = ex is NpgsqlException nex ? nex.SqlState : null,
                    isTransient = IsTransientException(ex),
                    suggestion = IsTransientException(ex)
                        ? "This appears to be a transient error. The operation was retried automatically."
                        : "This error requires attention and cannot be automatically retried."
                };

                return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            }
        }, nameof(ExecuteQuery));
    }

    [McpServerTool, Description("Execute a non-query SQL statement (INSERT, UPDATE, DELETE, CREATE, etc.) against the PostgreSQL database")]
    public static async Task<string> ExecuteNonQuery(
        string sql,
        [Description("Database name to use (e.g., 'myapp', 'production', 'test')")]
        string database)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            try
            {
                var connectionString = GetConnectionString(database);
                await using var connection = new NpgsqlConnection(connectionString);

                // Validate and open connection
                if (!await ValidateConnectionAsync(connection))
                {
                    throw new InvalidOperationException("Failed to establish valid database connection");
                }

                await using var command = new NpgsqlCommand(sql, connection)
                {
                    CommandTimeout = CommandTimeoutSeconds
                };
                var rowsAffected = await command.ExecuteNonQueryAsync();

                var response = new
                {
                    success = true,
                    rowsAffected = rowsAffected,
                    database = database,
                    message = $"Command executed successfully. {rowsAffected} rows affected."
                };

                return JsonSerializer.Serialize(response, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            }
            catch (Exception ex)
            {
                LogException(ex, nameof(ExecuteNonQuery), sql);

                var errorResponse = new
                {
                    success = false,
                    error = ex.Message,
                    database = database,
                    sqlState = ex is NpgsqlException nex ? nex.SqlState : null,
                    isTransient = IsTransientException(ex),
                    suggestion = IsTransientException(ex)
                        ? "This appears to be a transient error. The operation was retried automatically."
                        : "This error requires attention and cannot be automatically retried."
                };

                return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            }
        }, nameof(ExecuteNonQuery));
    }

    [McpServerTool, Description("Test the PostgreSQL database connection")]
    public static async Task<string> TestConnection(
        [Description("Database name to test (e.g., 'myapp', 'production', 'test')")]
        string database)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            try
            {
                var connectionString = GetConnectionString(database);
                await using var connection = new NpgsqlConnection(connectionString);

                // Validate and open connection
                if (!await ValidateConnectionAsync(connection))
                {
                    throw new InvalidOperationException("Failed to establish valid database connection");
                }

                // Test with a more comprehensive query
                await using var command = new NpgsqlCommand(@"
                    SELECT
                        NOW() as current_time,
                        current_database() as database_name,
                        pg_backend_pid() as process_id,
                        version() as server_version
                ", connection)
                {
                    CommandTimeout = 10 // Quick timeout for health check
                };

                await using var reader = await command.ExecuteReaderAsync();
                await reader.ReadAsync();

                var connStringBuilder = new NpgsqlConnectionStringBuilder(connection.ConnectionString);

                var response = new
                {
                    success = true,
                    message = "Connection successful",
                    database = database,
                    serverTime = reader.GetDateTime(0).ToString("yyyy-MM-dd HH:mm:ss"),
                    databaseName = reader.GetString(1),
                    processId = reader.GetInt32(2),
                    serverVersion = reader.GetString(3),
                    poolingEnabled = connStringBuilder.Pooling,
                    connectionTimeout = connStringBuilder.Timeout,
                    commandTimeout = connStringBuilder.CommandTimeout,
                    keepAlive = connStringBuilder.KeepAlive
                };

                return JsonSerializer.Serialize(response, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            }
            catch (Exception ex)
            {
                LogException(ex, nameof(TestConnection), "SELECT NOW()");

                var errorResponse = new
                {
                    success = false,
                    error = ex.Message,
                    database = database,
                    sqlState = ex is NpgsqlException nex ? nex.SqlState : null,
                    isTransient = IsTransientException(ex),
                    suggestion = IsTransientException(ex)
                        ? "Connection test failed with a transient error. Retrying automatically."
                        : "Connection test failed. Please check your connection string and database availability."
                };

                return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            }
        }, nameof(TestConnection));
    }

    [McpServerTool, Description("List available databases/connection strings")]
    public static Task<string> ListAvailableDatabases()
    {
        try
        {
            var configBuilder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

            // Load credentials from user's home directory
            var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var credentialsPath = Path.Combine(homeDirectory, ".postgres-mcp-server-creds.json");

            if (File.Exists(credentialsPath))
            {
                // Read the credentials file directly and merge with configuration
                var credentialsJson = File.ReadAllText(credentialsPath);
                var credentialsConfig = JsonSerializer.Deserialize<JsonElement>(credentialsJson);

                // Create a memory configuration source from the credentials
                var credentialsDict = new Dictionary<string, string?>();
                if (credentialsConfig.TryGetProperty("ConnectionStrings", out var credConnectionStrings))
                {
                    foreach (var prop in credConnectionStrings.EnumerateObject())
                    {
                        credentialsDict[$"ConnectionStrings:{prop.Name}"] = prop.Value.GetString();
                    }
                }
                configBuilder.AddInMemoryCollection(credentialsDict);
            }

            var configuration = configBuilder.Build();
            var connectionStrings = configuration.GetSection("ConnectionStrings");
            var databases = new List<string>();

            foreach (var child in connectionStrings.GetChildren())
            {
                databases.Add(child.Key);
            }

            var response = new
            {
                success = true,
                availableDatabases = databases,
                credentialsSource = File.Exists(credentialsPath) ? credentialsPath : "appsettings.json",
                resilienceSettings = new
                {
                    maxRetryAttempts = MaxRetryAttempts,
                    commandTimeoutSeconds = CommandTimeoutSeconds,
                    connectionTimeoutSeconds = ConnectionTimeoutSeconds,
                    poolingEnabled = true,
                    keepAliveSeconds = 30
                }
            };

            return Task.FromResult(JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch (Exception ex)
        {
            // Only log exceptions that aren't related to missing credentials files
            if (!ex.Message.Contains("configuration file") && !ex.Message.Contains("was not found"))
            {
                LogException(ex, nameof(ListAvailableDatabases));
            }

            var errorResponse = new
            {
                success = false,
                error = ex.Message
            };

            return Task.FromResult(JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
    }
}
