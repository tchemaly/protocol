# Unity Plugin Logging Server

A simple logging server for collecting and analyzing logs from the Unity plugin.

## Features

- Receives and stores logs from Unity plugin installations
- Tracks installation statistics
- Organizes logs by date
- Provides basic analytics
- CORS enabled for cross-origin requests
- Winston logging for server operations

## Installation and User Tracking

The server maintains detailed tracking information for each installation:

- **Installation ID**: Unique UUID for each installation
- **User ID**: Customizable user identifier (defaults to "anonymous")
- **Platform**: Unity platform (e.g., WindowsEditor, MacEditor)
- **Unity Version**: Version of Unity being used
- **Plugin Version**: Version of the Unity plugin

## Configuration System

The server supports a flexible configuration system:

- **Logging Status**: Master switch for enabling/disabling logging
- **Remote Logging**: Toggle for remote log transmission
- **Endpoint Configuration**: Customizable remote endpoint URL
- **Transmission Interval**: Configurable log transmission frequency (default: 5 minutes)

## Remote Logging Features

The server implements robust remote logging capabilities:

- **Queue Management**: In-memory queue for pending logs
- **Batch Processing**: Efficient batch transmission of logs
- **Error Handling**: Automatic retry on transmission failure
- **Network Resilience**: Handles network interruptions gracefully

## Log Entry Format

Logs are stored in a structured JSON format:

```json
{
  "entries": [
    {
      "timestamp": "2025-05-10 14:30:00",
      "prompt": "user message",
      "sessionId": 0,
      "sessionName": "Session Name",
      "actionType": "prompt",
      "details": "",
      "installationId": "550e8400-e29b-41d4-a716-446655440000",
      "userId": "user123",
      "platform": "WindowsEditor",
      "unityVersion": "2022.3.1f1",
      "pluginVersion": "1.0.0"
    }
  ]
}
```

### Log Types

1. **Prompt Logs**:
   - Contains user messages in the `prompt` field
   - Tracks conversation context
   - Includes session information

2. **Action Logs**:
   - Empty `prompt` field
   - Action-specific details in the `details` field
   - Tracks user interactions and system events

## Directory Structure

```
LogServer/
└── logs/
    ├── all/
    │   └── YYYY-MM-DD/
    │       └── HH-MM-SS.json
    ├── error.log
    └── combined.log
```

## Setup

1. Install Node.js if you haven't already
2. Install dependencies:
   ```bash
   cd LogServer
   npm install
   ```
3. Start the server:
   ```bash
   npm start
   ```

The server will run on port 3000 by default. You can change this by setting the PORT environment variable.

## Production Deployment

For production deployment, follow these essential steps:

### 1. Process Management with PM2

Install and configure PM2 for process management:

```bash
# Install PM2 globally
npm install -g pm2

# Start the server with PM2
pm2 start server.js --name log-server

# Save the PM2 configuration
pm2 save

# Set up PM2 to start on system boot
pm2 startup
```

### 2. HTTPS Setup with NGINX

Configure NGINX as a reverse proxy with HTTPS:

```nginx
server {
    listen 443 ssl;
    server_name logs.yourdomain.com;

    ssl_certificate /path/to/cert.pem;
    ssl_certificate_key /path/to/key.pem;

    location / {
        proxy_pass http://localhost:3000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
    }
}
```

### 3. Security Measures

#### Authentication
Add API key authentication:

```javascript
// Add to server.js
app.use((req, res, next) => {
    const key = req.headers['x-api-key'];
    if (key !== process.env.API_KEY) {
        return res.status(403).json({ error: 'Unauthorized' });
    }
    next();
});
```

#### Rate Limiting
Install and configure rate limiting:

```bash
npm install express-rate-limit
```

```javascript
// Add to server.js
const rateLimit = require('express-rate-limit');
app.use(rateLimit({ 
    windowMs: 1 * 60 * 1000,  // 1 minute
    max: 100  // limit each IP to 100 requests per windowMs
}));
```

### 4. Hosting Options

#### Self-Hosted (VPS)
- AWS EC2
- DigitalOcean
- Linode
- Vultr

#### Managed Services
- Render.com
- Railway
- Vercel (with Node backend)
- Fly.io

### 5. Data Storage

For production, consider:
- Using a database (MongoDB, PostgreSQL)
- Setting up persistent volumes
- Implementing data retention policies

## API Endpoints

### POST /logs
Receives log entries from the Unity plugin.

Request body format:
```json
{
  "entries": [
    {
      "timestamp": "2025-05-10 14:30:00",
      "prompt": "user message",
      "sessionId": 0,
      "sessionName": "Session Name",
      "actionType": "prompt",
      "details": "",
      "installationId": "550e8400-e29b-41d4-a716-446655440000",
      "userId": "user123",
      "platform": "WindowsEditor",
      "unityVersion": "2022.3.1f1",
      "pluginVersion": "1.0.0"
    }
  ]
}
```

### GET /stats
Returns statistics about all installations.

Response format:
```json
{
  "installations": [
    {
      "installationId": "550e8400-e29b-41d4-a716-446655440000",
      "firstSeen": "2025-05-10T14:30:00.000Z",
      "lastSeen": "2025-05-10T15:30:00.000Z",
      "userId": "user123",
      "platform": "WindowsEditor",
      "unityVersion": "2022.3.1f1",
      "pluginVersion": "1.0.0",
      "promptCount": 10,
      "actionCount": 5
    }
  ]
}
```

## Unity Plugin Configuration

To enable remote logging in the Unity plugin, update the `logging_config.json`:

```json
{
  "isRemoteLoggingEnabled": true,
  "remoteLogEndpoint": "http://your-server:3000/logs"
}
```

## Monitoring and Management

The server provides several ways to monitor and manage logs:

1. **Real-time Logging**: View logs as they are received
2. **Installation Statistics**: Track usage across different installations
3. **Error Monitoring**: Automatic error logging and tracking
4. **Performance Metrics**: Track server performance and resource usage

## Security Considerations

### Required for Public Use
- ✅ HTTPS encryption
- ✅ Authentication (API keys or JWT)
- ✅ Rate limiting
- ✅ Secure storage
- ✅ Process management (PM2)

### Recommended
- ⚠️ Reverse proxy (NGINX)
- ⚠️ Error monitoring (Sentry)
- ⚠️ Analytics dashboard
- ⚠️ Regular security audits

### Privacy
- Implement data retention policies
- Hash user IDs client-side
- Include privacy policy
- Avoid collecting PII without consent

## Future Enhancements

Planned features for future releases:

1. **Authentication System**: Secure endpoint access
2. **Web Interface**: Visual log management
3. **Advanced Analytics**: Usage patterns and insights
4. **Alert System**: Customizable notifications
5. **Data Retention**: Configurable log retention policies

## Troubleshooting

### Common Issues

1. **Server Not Starting**
   - Check PM2 logs: `pm2 logs log-server`
   - Verify port availability
   - Check environment variables

2. **Connection Issues**
   - Verify HTTPS configuration
   - Check firewall settings
   - Validate API key configuration

3. **Performance Problems**
   - Monitor rate limits
   - Check database connections
   - Review log rotation settings

### Support

For issues and support:
1. Check the [GitHub Issues](https://github.com/your-repo/issues)
2. Review the troubleshooting guide
3. Contact support with detailed logs 