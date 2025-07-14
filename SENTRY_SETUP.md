# Sentry Integration Setup for UpdateServer

## Overview
This project now includes Sentry SDK integration for error tracking, performance monitoring, and application insights.

## Setup Instructions

### 1. Get Your Sentry DSN
1. Go to [sentry.io](https://sentry.io/) and create a free account
2. Create a new project for your application
3. Copy the DSN (Data Source Name) from your project settings

### 2. Configure Sentry Settings
Edit the `app.config` file and replace the placeholder values:

```xml
<add key="SentryDsn" value="YOUR_ACTUAL_SENTRY_DSN_HERE" />
<add key="SentryEnvironment" value="Production" />
<add key="SentryRelease" value="UpdateServer@1.0.0" />
<add key="SentryDebug" value="false" />
<add key="SentryTracesSampleRate" value="1.0" />
```

### 3. Configuration Options

| Setting | Description | Default | Examples |
|---------|-------------|---------|----------|
| `SentryDsn` | Your Sentry project DSN | `YOUR_SENTRY_DSN_HERE` | `https://abc123@o123456.ingest.sentry.io/123456` |
| `SentryEnvironment` | Application environment | `Development` | `Development`, `Staging`, `Production` |
| `SentryRelease` | Release version | `UpdateServer@1.0.0` | `UpdateServer@1.2.3` |
| `SentryDebug` | Enable debug logging | `false` | `true`, `false` |
| `SentryTracesSampleRate` | Performance monitoring sample rate | `1.0` | `0.0` to `1.0` |

### 4. Using the Enhanced Logging

The project includes enhanced logging methods that automatically send data to Sentry:

```csharp
// Log informational messages
FileLogger.LogInfo("User connected", "logs/info.log");

// Log warnings
FileLogger.LogWarning("Configuration file not found", "logs/warnings.log");

// Log errors with exceptions
try {
    // Your code here
} catch (Exception ex) {
    FileLogger.LogError("Failed to process request", ex, "logs/errors.log");
}

// Log errors without exceptions
FileLogger.LogError("Invalid configuration detected", null, "logs/errors.log");
```

### 5. What Gets Sent to Sentry

- **Exceptions**: All unhandled exceptions are automatically captured
- **Error Messages**: Messages logged with `LogError()`
- **Warning Messages**: Messages logged with `LogWarning()`
- **Breadcrumbs**: Application flow tracking (start, info messages, exit)
- **Performance Data**: Transaction traces (if enabled)
- **Context**: Environment, release version, and other metadata

### 6. Production Considerations

For production environments:
- Set `SentryEnvironment` to `"Production"`
- Set `SentryDebug` to `"false"`
- Consider reducing `SentryTracesSampleRate` to `0.1` or `0.25` for high-traffic applications
- Use environment-specific release versions (e.g., `UpdateServer@1.0.0-prod`)

### 7. Security Notes

- Never commit your actual DSN to version control
- Consider using environment variables for sensitive configuration
- Review Sentry's data scrubbing settings to ensure no sensitive data is sent

### 8. Troubleshooting

If Sentry integration isn't working:
1. Check that your DSN is correct
2. Verify network connectivity to Sentry's ingest endpoints
3. Enable debug mode temporarily: `<add key="SentryDebug" value="true" />`
4. Check the application logs for Sentry-related messages

### 9. Monitoring Dashboard

Once configured, you can monitor your application at:
- Sentry Dashboard: `https://sentry.io/organizations/YOUR_ORG/projects/YOUR_PROJECT/`
- View errors, performance metrics, and user impact
- Set up alerts for critical issues

## Files Modified

- `Program.cs` - Added Sentry initialization and error handling
- `SentryConfig.cs` - Configuration management (new file)
- `app.config` - Added Sentry configuration settings
- `UpdateServer.csproj` - Added Sentry package reference
- `packages.config` - Added Sentry NuGet package

## Support

For Sentry-specific issues, refer to the [Sentry .NET SDK documentation](https://docs.sentry.io/platforms/dotnet/).
