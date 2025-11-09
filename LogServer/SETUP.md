# Log Server Setup Guide

This guide will walk you through setting up the logging server, from development to production deployment.

## Quick Start (Development)

1. **Clone the Repository**
   ```bash
   git clone <your-repo-url>
   cd LogServer
   ```

2. **Install Dependencies**
   ```bash
   npm install
   ```

3. **Start the Server**
   ```bash
   npm start
   ```
   The server will run on `http://localhost:3000`

## Production Setup

### 1. Server Requirements

- Node.js 16.x or later
- PM2 (for process management)
- NGINX (for reverse proxy)
- SSL certificate (for HTTPS)

### 2. Installation Steps

1. **Install Node.js and PM2**
   ```bash
   # Install Node.js (Ubuntu/Debian)
   curl -fsSL https://deb.nodesource.com/setup_16.x | sudo -E bash -
   sudo apt-get install -y nodejs

   # Install PM2 globally
   sudo npm install -g pm2
   ```

2. **Install NGINX**
   ```bash
   sudo apt-get update
   sudo apt-get install nginx
   ```

3. **Get SSL Certificate**
   ```bash
   # Install Certbot
   sudo apt-get install certbot python3-certbot-nginx

   # Get certificate
   sudo certbot --nginx -d logs.yourdomain.com
   ```

### 3. Server Configuration

1. **Create Environment File**
   ```bash
   # Create .env file
   touch .env
   ```

   Add the following to `.env`:
   ```
   PORT=3000
   API_KEY=your-secure-api-key
   NODE_ENV=production
   ```

2. **Configure NGINX**
   Create a new NGINX configuration:
   ```bash
   sudo nano /etc/nginx/sites-available/log-server
   ```

   Add the following configuration:
   ```nginx
   server {
       listen 443 ssl;
       server_name logs.yourdomain.com;

       ssl_certificate /etc/letsencrypt/live/logs.yourdomain.com/fullchain.pem;
       ssl_certificate_key /etc/letsencrypt/live/logs.yourdomain.com/privkey.pem;

       location / {
           proxy_pass http://localhost:3000;
           proxy_http_version 1.1;
           proxy_set_header Upgrade $http_upgrade;
           proxy_set_header Connection 'upgrade';
           proxy_set_header Host $host;
           proxy_cache_bypass $http_upgrade;
           proxy_set_header X-Real-IP $remote_addr;
           proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
           proxy_set_header X-Forwarded-Proto $scheme;
       }
   }
   ```

   Enable the site:
   ```bash
   sudo ln -s /etc/nginx/sites-available/log-server /etc/nginx/sites-enabled/
   sudo nginx -t
   sudo systemctl restart nginx
   ```

3. **Start the Server with PM2**
   ```bash
   # Start the server
   pm2 start server.js --name log-server

   # Save PM2 configuration
   pm2 save

   # Set up PM2 to start on boot
   pm2 startup
   ```

### 4. Testing the Setup

1. **Test the Server**
   ```bash
   # Check if the server is running
   pm2 status

   # View logs
   pm2 logs log-server

   # Test the API
   curl -X POST http://localhost:3000/logs \
     -H "Content-Type: application/json" \
     -H "X-API-Key: your-secure-api-key" \
     -d '{"entries":[{"timestamp":"2025-05-10 14:30:00","prompt":"test"}]}'
   ```

2. **Test HTTPS**
   Visit `https://logs.yourdomain.com` in your browser

### 5. Monitoring Setup

1. **Install Monitoring Tools**
   ```bash
   # Install PM2 monitoring
   pm2 install pm2-logrotate
   pm2 install pm2-server-monit
   ```

2. **Configure Log Rotation**
   ```bash
   pm2 set pm2-logrotate:max_size 10M
   pm2 set pm2-logrotate:retain 7
   ```

### 6. Backup Setup

1. **Create Backup Script**
   ```bash
   # Create backup directory
   mkdir -p /backup/logs

   # Create backup script
   nano /usr/local/bin/backup-logs.sh
   ```

   Add the following to the script:
   ```bash
   #!/bin/bash
   TIMESTAMP=$(date +%Y%m%d_%H%M%S)
   tar -czf /backup/logs/logs_$TIMESTAMP.tar.gz /path/to/logs
   find /backup/logs -type f -mtime +7 -delete
   ```

   Make it executable:
   ```bash
   chmod +x /usr/local/bin/backup-logs.sh
   ```

2. **Set up Cron Job**
   ```bash
   # Add to crontab
   crontab -e
   ```

   Add the following line:
   ```
   0 0 * * * /usr/local/bin/backup-logs.sh
   ```

## Troubleshooting

### Common Issues

1. **Server Won't Start**
   ```bash
   # Check PM2 logs
   pm2 logs log-server

   # Check system logs
   journalctl -u nginx
   ```

2. **SSL Issues**
   ```bash
   # Check SSL certificate
   sudo certbot certificates

   # Renew certificate
   sudo certbot renew --dry-run
   ```

3. **Permission Issues**
   ```bash
   # Fix log directory permissions
   sudo chown -R $USER:$USER /path/to/logs
   sudo chmod -R 755 /path/to/logs
   ```

## Maintenance

### Regular Tasks

1. **Update Dependencies**
   ```bash
   npm update
   pm2 reload log-server
   ```

2. **Check Disk Space**
   ```bash
   df -h
   du -sh /path/to/logs
   ```

3. **Monitor Performance**
   ```bash
   pm2 monit
   ```

### Security Updates

1. **Update System**
   ```bash
   sudo apt-get update
   sudo apt-get upgrade
   ```

2. **Update Node.js**
   ```bash
   # Using nvm
   nvm install node
   nvm use node
   ```

## Support

For issues and support:
1. Check the [GitHub Issues](https://github.com/your-repo/issues)
2. Review the troubleshooting guide
3. Contact support with detailed logs 