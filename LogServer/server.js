const express = require('express');
const cors = require('cors');
const morgan = require('morgan');
const winston = require('winston');
const fs = require('fs');
const path = require('path');

const app = express();
const port = process.env.PORT || 3000;

// Configure Winston logger
const logger = winston.createLogger({
    level: 'info',
    format: winston.format.combine(
        winston.format.timestamp(),
        winston.format.json()
    ),
    transports: [
        new winston.transports.File({ 
            filename: 'logs/error.log', 
            level: 'error' 
        }),
        new winston.transports.File({ 
            filename: 'logs/combined.log' 
        })
    ]
});

// Create logs directory if it doesn't exist
const logsDir = path.join(__dirname, 'logs');
if (!fs.existsSync(logsDir)) {
    fs.mkdirSync(logsDir);
}

// Middleware
app.use(cors());
app.use(express.json());
app.use(morgan('combined'));

// Store installation statistics
const installationStats = new Map();

// Helper function to get date string
const getDateString = () => {
    const now = new Date();
    return now.toISOString().split('T')[0];
};

// Helper function to ensure log directory exists
const ensureLogDirectory = (type) => {
    const dir = path.join(logsDir, type, getDateString());
    if (!fs.existsSync(dir)) {
        fs.mkdirSync(dir, { recursive: true });
    }
    return dir;
};

// Helper function to save logs
const saveLogs = (logs, type) => {
    const dir = ensureLogDirectory(type);
    const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
    const filename = path.join(dir, `${timestamp}.json`);
    
    fs.writeFileSync(filename, JSON.stringify(logs, null, 2));
    logger.info(`Saved ${logs.entries.length} ${type} logs to ${filename}`);
};

// Update installation statistics
const updateStats = (log) => {
    const { installationId, userId, platform, unityVersion, pluginVersion } = log;
    
    if (!installationStats.has(installationId)) {
        installationStats.set(installationId, {
            firstSeen: new Date(),
            lastSeen: new Date(),
            userId,
            platform,
            unityVersion,
            pluginVersion,
            promptCount: 0,
            actionCount: 0
        });
    }
    
    const stats = installationStats.get(installationId);
    stats.lastSeen = new Date();
    
    if (log.actionType === 'prompt') {
        stats.promptCount++;
    } else {
        stats.actionCount++;
    }
};

// Routes
app.post('/logs', (req, res) => {
    try {
        const { entries } = req.body;
        
        if (!entries || !Array.isArray(entries)) {
            return res.status(400).json({ error: 'Invalid log format' });
        }

        // Process each log entry
        entries.forEach(entry => {
            updateStats(entry);
        });

        // Save logs
        saveLogs(req.body, 'all');
        
        res.json({ 
            success: true, 
            message: `Processed ${entries.length} log entries` 
        });
    } catch (error) {
        logger.error('Error processing logs:', error);
        res.status(500).json({ error: 'Internal server error' });
    }
});

// Get installation statistics
app.get('/stats', (req, res) => {
    const stats = Array.from(installationStats.entries()).map(([id, data]) => ({
        installationId: id,
        ...data
    }));
    
    res.json({ installations: stats });
});

// Start server
app.listen(port, () => {
    console.log(`Log server running on port ${port}`);
    logger.info(`Server started on port ${port}`);
}); 