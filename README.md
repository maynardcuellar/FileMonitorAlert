# Folder Text Monitor

## Overview

Folder Text Monitor is a .NET console application that monitors folders for file activity and searches file contents for specified text patterns. When matches are detected, the application can generate email alerts and send notifications to Microsoft Teams via webhook integration.

## Key Features

- Real-time file monitoring using FileSystemWatcher
- Initial scan of existing files on startup
- Monitoring of file creation, modification, rename, and deletion events
- Configurable text search criteria
- Optional case-insensitive searching
- Optional recursive subfolder monitoring
- Email notification support
- Microsoft Teams webhook integration
- Duplicate event suppression through debouncing
- Alert acknowledgment and throttling
- Retry handling for locked files
- JSON-based configuration

## How It Works

1. Loads configuration from appsettings.json.
2. Performs an initial scan of existing files.
3. Watches a target directory for file changes.
4. Searches files for a specified text string.
5. Tracks previously acknowledged files.
6. Sends alerts when new occurrences are detected.
7. Prevents notification flooding through configurable pause intervals.

## Configuration

### appsettings.json

```json
{
  "PauseAlertMinutes": 30,
  "Email": {
    "Enabled": true,
    "SmtpServer": "smtp.company.com",
    "SmtpPort": 587,
    "Username": "alerts@company.com",
    "From": "alerts@company.com",
    "To": "operations@company.com",
    "Subject": "Folder Monitor Alert",
    "EnableSsl": true
  },
  "Teams": {
    "WebhookUrl": "https://your-webhook-url"
  }
}
```

## Environment Variables

| Variable | Description |
|-----------|-------------|
| FILEMONITOR_SMTP_PASSWORD | SMTP account password |
| FILEMONITOR_TEAMS_WEBHOOK_URL | Overrides Teams webhook URL |

## Usage

```bash
FolderTextMonitor.exe <FolderPath> <SearchText> <FileMask>
```

### Example

```bash
FolderTextMonitor.exe D:\Logs ERROR .log --ignoreCase --includeSubdirs
```

### Optional Parameters

- --ignoreCase
- --includeSubdirs

## Alert Processing

The application keeps track of previously detected files and prevents duplicate notifications. Alerts are only generated when additional occurrences are discovered and the configured alert suppression interval has elapsed.

## Supported Notifications

### Email

- SMTP Authentication
- SSL/TLS Support
- Configurable recipients and subject lines

### Microsoft Teams

- Incoming Webhook Integration
- JSON Message Payloads

## Future Enhancements

- SMS notifications
- Regular expression matching
- Multiple search terms
- Windows Service hosting
- Dashboard reporting
- Database logging
- Adaptive Cards for Teams

## Author

**Maynard Cuellar**

LinkedIn: https://linkedin.com/in/maynardcuellar

Website: http://www.maynardcuellar.com
