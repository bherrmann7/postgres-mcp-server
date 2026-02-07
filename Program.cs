using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace PostgresMcpServer;

public static class Program
{
    public static void Main(string[] args)
    {
        // Redirect all stdout to stderr for MCP protocol
        Console.SetOut(Console.Error);
        var host = CreateHost(args);
        
        // Use synchronous Run() to block and keep the process alive
        host.Run();
    }
    
    public static IHost CreateHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory) // ensures we look next to the exe
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
                builder.Configuration.AddInMemoryCollection(credentialsDict);
                Console.Error.WriteLine($"\n\nLoaded credentials from: {credentialsPath}\n\n");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"\n\nERROR loading credentials file: {ex.Message}\n\n");
                Console.Error.WriteLine("Continuing without credentials file - tools may not work properly.\n");
            }
        }
        else
        {
            Console.Error.WriteLine($"\n\nWARNING: Credentials file not found at: {credentialsPath}\n\n");
            Console.Error.WriteLine("Please create this file with your PostgreSQL connection strings.\n");
        }
        Console.Error.Flush();
        
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();
        
        return builder.Build();
    }
}
