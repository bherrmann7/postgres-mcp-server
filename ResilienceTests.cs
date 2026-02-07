using System;
using System.Threading.Tasks;
using Xunit;

namespace PostgresMcpServer.Tests;

public class ResilienceTests
{
    [Fact]
    public async Task TestConnection_WithValidDatabase_ReturnsSuccess()
    {
        // This test verifies the connection works with all resilience features enabled
        // You'll need a valid database configured in your credentials file

        var result = await PostgresDatabaseTools.TestConnection("test");

        Assert.Contains("\"success\": true", result);
        Assert.Contains("poolingEnabled", result);
        Assert.Contains("keepAlive", result);
    }

    [Fact]
    public async Task ExecuteQuery_WithSimpleQuery_ReturnsResults()
    {
        // Test that queries work with retry logic and health checks
        var result = await PostgresDatabaseTools.ExecuteQuery("SELECT 1 as test_value", "test");

        Assert.Contains("\"success\": true", result);
        Assert.Contains("test_value", result);
    }

    [Fact]
    public async Task ListAvailableDatabases_ShowsResilienceSettings()
    {
        // Verify resilience settings are exposed
        var result = await PostgresDatabaseTools.ListAvailableDatabases();

        Assert.Contains("\"success\": true", result);
        Assert.Contains("resilienceSettings", result);
        Assert.Contains("maxRetryAttempts", result);
        Assert.Contains("keepAliveSeconds", result);
    }

    [Fact]
    public async Task ExecuteQuery_WithInvalidDatabase_ReturnsError()
    {
        // Test error handling
        var result = await PostgresDatabaseTools.ExecuteQuery("SELECT 1", "nonexistent_database");

        Assert.Contains("\"success\": false", result);
        Assert.Contains("error", result);
    }

    // Note: Testing actual retry logic requires simulating network failures
    // This would typically be done with integration tests using tools like:
    // - Testcontainers to spin up/down PostgreSQL
    // - Toxiproxy to simulate network failures
    // - Docker with network policies
}
