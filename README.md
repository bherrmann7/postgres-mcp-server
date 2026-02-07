# PostgreSQL MCP Server

A Model Context Protocol (MCP) server that provides PostgreSQL database access to AI assistants like Claude. This server enables AI agents to query, update, and manage PostgreSQL databases through a standardized interface.

## Features

- **Execute SELECT queries** - Query PostgreSQL databases and return results as JSON
- **Execute non-query statements** - Run INSERT, UPDATE, DELETE, CREATE, and other DDL/DML statements
- **Connection testing** - Verify database connectivity and get server information
- **Multiple database support** - Configure and switch between multiple PostgreSQL databases
- **Built-in resilience** - Automatic retry logic with exponential backoff for transient errors
- **Connection pooling** - Optimized connection management with configurable pool settings
- **Health checks** - Automatic connection validation before operations
- **TCP keepalive** - Proactive detection of broken connections

## Installation

### Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- PostgreSQL database (local or remote)
- Claude Desktop or other MCP-compatible client

### Build from Source

```bash
git clone https://github.com/bherrmann7/postgres-mcp-server.git
cd postgres-mcp-server
dotnet build
```

## Configuration

### 1. Create Credentials File

Create a file at `~/.postgres-mcp-server-creds.json` with your database connection strings:

```json
{
  "ConnectionStrings": {
    "myapp": "Host=localhost;Port=5432;Database=myapp;Username=postgres;Password=yourpassword",
    "production": "Host=prod.example.com;Port=5432;Database=proddb;Username=produser;Password=prodpass",
    "test": "Host=localhost;Port=5432;Database=testdb;Username=testuser;Password=testpass"
  }
}
```

**Security Note**: This file contains sensitive credentials. Ensure it has appropriate file permissions:
```bash
chmod 600 ~/.postgres-mcp-server-creds.json
```

### 2. Configure MCP Client

Add the server to your MCP client configuration. For Claude Desktop, edit:
- **macOS**: `~/Library/Application Support/Claude/claude_desktop_config.json`
- **Windows**: `%APPDATA%\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "postgres": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/postgres-mcp-server"
      ]
    }
  }
}
```

Or, if you've built the executable:

```json
{
  "mcpServers": {
    "postgres": {
      "command": "/absolute/path/to/postgres-mcp-server/bin/Debug/net9.0/PostgresMcpServer"
    }
  }
}
```

## Usage

Once configured, the AI assistant can use these tools:

### List Available Databases
```
List the available PostgreSQL databases
```

### Test Connection
```
Test connection to the 'myapp' database
```

### Execute Query
```
Run this query on the 'myapp' database:
SELECT * FROM users WHERE status = 'active' LIMIT 10
```

### Execute Non-Query
```
Update the 'myapp' database:
UPDATE users SET last_login = NOW() WHERE user_id = 123
```

## Resilience Features

This server includes comprehensive resilience improvements:

- **Automatic retries** - Up to 3 retry attempts for transient errors with exponential backoff
- **Connection pooling** - 1-20 connections maintained per database
- **Timeout configuration** - 30s connection timeout, 120s command timeout
- **TCP keepalive** - 30s intervals to detect broken connections
- **Health validation** - Quick health check before each operation
- **Prepared statements** - Automatic preparation of frequently used queries

See [RESILIENCE.md](RESILIENCE.md) for detailed information about resilience features and configuration.

## API Tools

### `execute_query`
Execute a SELECT query and return results as JSON.

**Parameters:**
- `sql` (string, required) - The SQL SELECT query to execute
- `database` (string, required) - Database name from your configuration

**Returns:** JSON with success status, row count, and data array

### `execute_non_query`
Execute INSERT, UPDATE, DELETE, CREATE, or other non-query SQL statements.

**Parameters:**
- `sql` (string, required) - The SQL statement to execute
- `database` (string, required) - Database name from your configuration

**Returns:** JSON with success status and rows affected

### `test_connection`
Test database connectivity and return server information.

**Parameters:**
- `database` (string, required) - Database name to test

**Returns:** JSON with connection details, server version, and configuration

### `list_available_databases`
List all configured database names.

**Returns:** JSON with array of available database names and resilience settings

## Security Considerations

1. **Credential Storage**: Store database credentials in `~/.postgres-mcp-server-creds.json` with restricted permissions
2. **SQL Injection**: This server executes SQL directly - ensure the AI assistant or user understands SQL injection risks
3. **Database Permissions**: Use database accounts with minimal required privileges
4. **Network Security**: Use SSL/TLS for remote database connections (add `SslMode=Require` to connection strings)
5. **Access Control**: Only grant access to trusted AI assistants and users

## Testing

Run the included tests:

```bash
dotnet test
```

The test suite includes:
- Server initialization tests
- Connection pool tests
- Retry logic tests
- Integration tests with configured databases

## Troubleshooting

### Connection Errors
- Verify PostgreSQL is running and accessible
- Check connection string format and credentials
- Ensure firewall allows connections to PostgreSQL port (default 5432)
- Review logs in stderr output

### "No connection string found for database"
- Check that `~/.postgres-mcp-server-creds.json` exists
- Verify the database name matches a key in ConnectionStrings
- Use `list_available_databases` to see configured databases

### Queries Timing Out
- Default timeout is 120 seconds for commands
- Optimize slow queries with indexes
- Consider increasing `CommandTimeoutSeconds` in `PostgresDatabaseTools.cs`

### Too Many Connections
- Default pool size is 20 connections per database
- Check for connection leaks (ensure proper disposal)
- Increase `MaxPoolSize` if needed for high concurrency

## Development

### Project Structure
```
postgres-mcp-server/
├── Program.cs                    # MCP server entry point
├── PostgresDatabaseTools.cs      # Database tool implementations
├── ServerIntegrationTests.cs     # Integration tests
├── ResilienceTests.cs           # Resilience feature tests
├── appsettings.json             # Logging configuration
├── RESILIENCE.md                # Resilience documentation
└── CHANGES_SUMMARY.md           # Change history
```

### Building and Running

```bash
# Build
dotnet build

# Run
dotnet run

# Run tests
dotnet test

# Publish self-contained executable (Linux)
dotnet publish -c Release -r linux-x64 --self-contained

# Publish self-contained executable (macOS)
dotnet publish -c Release -r osx-arm64 --self-contained

# Publish self-contained executable (Windows)
dotnet publish -c Release -r win-x64 --self-contained
```

## Contributing

Contributions are welcome! Please feel free to submit issues or pull requests.

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Resources

- [Model Context Protocol](https://modelcontextprotocol.io/) - MCP specification and documentation
- [Npgsql](https://www.npgsql.org/) - PostgreSQL data provider for .NET
- [PostgreSQL Documentation](https://www.postgresql.org/docs/) - Official PostgreSQL docs

## Author

Bob Herrmann - [@bherrmann7](https://github.com/bherrmann7)

## Acknowledgments

- Built using the [ModelContextProtocol](https://github.com/modelcontextprotocol/servers) .NET SDK
- Inspired by the need for robust database access in AI-assisted development
