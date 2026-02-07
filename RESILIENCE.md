# PostgreSQL MCP Server - Resilience Improvements

## Overview
This document describes the resilience improvements made to the PostgreSQL MCP Server to handle connection failures, timeouts, and transient errors gracefully.

## Key Improvements

### 1. Timeout Configuration
**Problem**: No explicit timeouts could lead to operations hanging indefinitely.

**Solution**:
- **Connection Timeout**: 30 seconds (configured in connection string)
- **Command Timeout**: 120 seconds for long-running queries
- **Health Check Timeout**: 5 seconds for quick validation

### 2. Automatic Retry Logic
**Problem**: Transient errors (network blips, temporary connection issues) caused immediate failures.

**Solution**:
- Automatic retry with exponential backoff
- Maximum 3 retry attempts for transient errors
- Initial delay: 500ms, doubles with each retry (max 5 seconds)
- Random jitter added to prevent thundering herd

**Transient Errors Detected**:
- Connection errors (08xxx SQL states)
- Deadlocks and serialization failures (40001, 40P01)
- Resource exhaustion (53xxx SQL states)
- Network exceptions (SocketException, IOException, TimeoutException)

### 3. Connection Pooling Configuration
**Problem**: Default connection pooling settings weren't optimized for resilience.

**Solution**:
```
Pooling: Enabled
MinPoolSize: 1 (keeps at least one warm connection)
MaxPoolSize: 20 (limits concurrent connections)
ConnectionIdleLifetime: 300 seconds (closes stale connections)
ConnectionPruningInterval: 10 seconds (cleanup frequency)
```

### 4. Connection Health Validation
**Problem**: No validation that connections are alive before use.

**Solution**:
- `ValidateConnectionAsync()` method checks connection health
- Executes quick "SELECT 1" before actual queries
- 5-second timeout for health checks
- Automatically opens closed connections

### 5. TCP Keepalive
**Problem**: Broken connections not detected until query execution.

**Solution**:
```
KeepAlive: 30 seconds
TcpKeepAliveTime: 30 seconds
TcpKeepAliveInterval: 10 seconds
```
This detects broken connections proactively and prevents them from being returned from the pool.

### 6. Statement Preparation
**Problem**: Repeated queries weren't optimized.

**Solution**:
```
MaxAutoPrepare: 10 (cache up to 10 prepared statements)
AutoPrepareMinUsages: 2 (prepare after 2 uses)
```

### 7. Enhanced Error Reporting
**Problem**: Generic error messages didn't indicate if retry was possible.

**Solution**:
- Error responses include `isTransient` flag
- Helpful suggestions for transient vs. permanent errors
- SQL State codes included for PostgreSQL errors
- Detailed logging with retry attempt numbers

## Configuration Constants

You can adjust these constants in `PostgresDatabaseTools.cs`:

```csharp
private const int MaxRetryAttempts = 3;              // Number of retry attempts
private const int InitialRetryDelayMs = 500;        // Starting retry delay
private const int CommandTimeoutSeconds = 120;       // Query timeout
private const int ConnectionTimeoutSeconds = 30;     // Connection timeout
```

## Testing Resilience

### Test Connection Failures
```bash
# Simulate network issues
# On macOS/Linux:
sudo pfctl -a "test" -f - <<EOF
block drop proto tcp from any to <db-server-ip> port 5432
EOF

# Wait a moment, then restore
sudo pfctl -a "test" -F all
```

### Test Slow Queries
```sql
-- This will timeout after 120 seconds
SELECT pg_sleep(130);
```

### Test Connection Pool Exhaustion
Run 25+ concurrent queries to exceed MaxPoolSize.

## Best Practices Applied

1. **Connection Pooling**: Always enabled and properly configured
2. **Dispose Pattern**: Using `await using` for proper cleanup
3. **Timeout Strategy**: Multiple timeout layers (connection, command, health check)
4. **Retry Strategy**: Exponential backoff with jitter
5. **Health Checks**: Validate before use, not just on errors
6. **Keepalive**: Detect broken connections proactively
7. **Prepared Statements**: Cache frequently used queries
8. **Error Classification**: Distinguish transient from permanent errors
9. **Resource Management**: Idle connection pruning
10. **Observability**: Detailed logging with retry information

## Monitoring

### Key Metrics to Watch
- Retry frequency and success rate
- Connection pool utilization
- Query timeout occurrences
- Average query execution time
- Connection acquisition time

### Log Analysis
Look for these patterns in logs:
```
[Retry] ExecuteQuery attempt 1/3 failed: ...
[Retry] Waiting 500ms before retry...
```

## Performance Considerations

### Connection Pooling
- **Pros**: Eliminates connection establishment overhead (100-500ms)
- **Cons**: Holds connections in memory
- **Recommendation**: Adjust `MaxPoolSize` based on your workload

### Keepalive
- **Pros**: Early detection of broken connections
- **Cons**: Slight network overhead
- **Recommendation**: Reduce interval if behind flaky network

### Prepared Statements
- **Pros**: Query planning optimization for repeated queries
- **Cons**: Memory overhead on server
- **Recommendation**: Increase `MaxAutoPrepare` for query-heavy workloads

## Troubleshooting

### "Too many connections" Error
**Cause**: MaxPoolSize (20) exceeded or database limit reached

**Solutions**:
1. Increase MaxPoolSize (carefully)
2. Check for connection leaks
3. Increase PostgreSQL `max_connections`

### Queries Still Timing Out
**Cause**: Query exceeds CommandTimeout (120s)

**Solutions**:
1. Optimize the query (add indexes, rewrite)
2. Increase CommandTimeoutSeconds constant
3. Consider async processing for long operations

### Retries Not Working
**Cause**: Error not classified as transient

**Solutions**:
1. Check logs for SQL State code
2. Add error code to `IsTransientException()` if appropriate
3. Some errors shouldn't be retried (syntax errors, constraint violations)

### Connection Pool Starvation
**Cause**: Connections not being returned to pool

**Solutions**:
1. Ensure all code uses `await using` pattern
2. Check for unhandled exceptions preventing disposal
3. Reduce ConnectionIdleLifetime if connections held too long

## Additional Resources

- [Npgsql Connection Pooling](https://www.npgsql.org/doc/connection-string-parameters.html#pooling)
- [PostgreSQL Error Codes](https://www.postgresql.org/docs/current/errcodes-appendix.html)
- [TCP Keepalive](https://tldp.org/HOWTO/TCP-Keepalive-HOWTO/)
- [Exponential Backoff](https://aws.amazon.com/blogs/architecture/exponential-backoff-and-jitter/)

## Future Enhancements

Consider adding:
1. **Circuit Breaker**: Stop trying after repeated failures
2. **Health Check Endpoint**: Monitor server health externally
3. **Metrics Export**: Prometheus/OpenTelemetry integration
4. **Connection String per Operation**: Read replicas for queries
5. **Query Timeout Hints**: Per-query timeout configuration
6. **Bulkhead Pattern**: Isolate connection pools by operation type
7. **Fallback Strategies**: Cached data or degraded functionality
