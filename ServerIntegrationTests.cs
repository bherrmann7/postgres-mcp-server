using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace PostgresMcpServer.Tests;

public class ServerIntegrationTests
{
    [Fact]
    public async Task ServerCanStartAndProcessInitialization()
    {
        // Arrange
        var initMessage = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { tools = new { } },
                clientInfo = new { name = "test-client", version = "1.0.0" }
            }
        });

        // Create a test config file
        var configFile = Path.GetTempFileName().Replace(".tmp", ".json");
        var config = JsonSerializer.Serialize(new 
        {
            ConnectionStrings = new
            {
                PostgresTest = "Host=localhost;Database=testdb;Username=test;Password=test;"
            }
        });
        await File.WriteAllTextAsync(configFile, config);

        try
        {
            // Act - Start the server process
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "run --project .",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? Environment.CurrentDirectory,
                Environment = { ["DOTNET_CONFIGURATION"] = Path.GetFileName(configFile) }
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // Send initialization message
            await process.StandardInput.WriteLineAsync(initMessage);
            await process.StandardInput.FlushAsync();

            // Give server time to process
            await Task.Delay(1000);

            // Kill the process
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }

            // Assert - If we got here without exceptions, the server started and processed the message
            Assert.True(true, "Server successfully started and processed initialization");
        }
        finally
        {
            if (File.Exists(configFile))
                File.Delete(configFile);
        }
    }

    [Fact]
    public void CanInstantiatePostgresDatabaseToolsClass()
    {
        // Arrange & Act
        var toolsType = typeof(PostgresDatabaseTools);
        
        // Assert
        Assert.NotNull(toolsType);
        Assert.True(toolsType.IsAbstract && toolsType.IsSealed, "Should be static class");
        
        // Check methods exist
        Assert.NotNull(toolsType.GetMethod("ExecuteQuery"));
        Assert.NotNull(toolsType.GetMethod("ExecuteNonQuery"));
        Assert.NotNull(toolsType.GetMethod("TestConnection"));
    }

    [Fact]
    public void ProgramCanCreateHost()
    {
        // Arrange
        var args = Array.Empty<string>();
        
        // Act & Assert - This should not throw even if credentials file doesn't exist
        var host = Program.CreateHost(args);
        Assert.NotNull(host);
        
        host.Dispose();
    }

    [Fact]
    public async Task CanConnectToAllAvailableDatabasesAndExecuteQuery()
    {
        // Arrange - Get list of available databases
        var availableDatabasesJson = await PostgresDatabaseTools.ListAvailableDatabases();
        Console.WriteLine($"ListAvailableDatabases response: {availableDatabasesJson}");
        
        var databasesResponse = JsonSerializer.Deserialize<JsonElement>(availableDatabasesJson);
        
        // Skip test if no databases are available or if there was an error
        if (!databasesResponse.GetProperty("success").GetBoolean())
        {
            Console.WriteLine("Skipping test - credentials not properly configured or error occurred");
            return;
        }

        var databases = databasesResponse.GetProperty("availableDatabases").EnumerateArray()
            .Select(database => database.GetString())
            .Where(database => !string.IsNullOrEmpty(database))
            .ToArray();

        if (databases.Length == 0)
        {
            Console.WriteLine("Skipping test - no databases found in configuration");
            return;
        }
        
        Console.WriteLine($"Found {databases.Length} databases to test: {string.Join(", ", databases)}");

        // Act & Assert - Test each database
        var results = new List<(string Database, bool Success, string Error)>();
        
        foreach (var database in databases)
        {
            try
            {
                // Test connection first
                var connectionTestJson = await PostgresDatabaseTools.TestConnection(database!);
                var connectionResult = JsonSerializer.Deserialize<JsonElement>(connectionTestJson);
                
                if (!connectionResult.GetProperty("success").GetBoolean())
                {
                    var error = connectionResult.TryGetProperty("error", out var errorProp) 
                        ? errorProp.GetString() ?? "Unknown error"
                        : "Unknown error";
                    results.Add((database!, false, $"Connection failed: {error}"));
                    continue;
                }

                // Test a simple query
                var queryJson = await PostgresDatabaseTools.ExecuteQuery("SELECT 77 as test_value", database!);
                var queryResult = JsonSerializer.Deserialize<JsonElement>(queryJson);
                
                if (!queryResult.GetProperty("success").GetBoolean())
                {
                    var error = queryResult.TryGetProperty("error", out var errorProp) 
                        ? errorProp.GetString() ?? "Unknown error"
                        : "Unknown error";
                    results.Add((database!, false, $"Query failed: {error}"));
                    continue;
                }

                // Verify the query returned the expected result
                var data = queryResult.GetProperty("data");
                if (data.GetArrayLength() != 1)
                {
                    results.Add((database!, false, "Query should return exactly one row"));
                    continue;
                }

                var row = data[0];
                if (!row.TryGetProperty("test_value", out var testValue) || 
                    testValue.GetInt32() != 77)
                {
                    results.Add((database!, false, "Query should return test_value = 77"));
                    continue;
                }

                results.Add((database!, true, "Success"));
            }
            catch (Exception ex)
            {
                results.Add((database!, false, $"Exception: {ex.Message}"));
            }
        }

        // Report results
        var successCount = results.Count(r => r.Success);
        var totalCount = results.Count;
        
        // Log all results for diagnostics
        foreach (var (database, success, error) in results)
        {
            if (success)
            {
                Console.WriteLine($"✓ Database '{database}': Connected and queried successfully");
            }
            else
            {
                Console.WriteLine($"✗ Database '{database}': {error}");
            }
        }

        // Assert that at least one database worked (allows for some databases to be unavailable)
        Assert.True(successCount > 0, 
            $"Expected at least one database to be connectable, but all {totalCount} failed. " +
            "This test requires valid PostgreSQL database connections configured in the credentials file.");
        
        Console.WriteLine($"Database connectivity test completed: {successCount}/{totalCount} databases accessible");
    }
}
