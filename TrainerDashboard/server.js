require('dotenv').config();
const express = require('express');
const mongoose = require('mongoose');
const cors = require('cors');

const app = express();
const PORT = process.env.PORT || 3000;
const MONGODB_URI = process.env.MONGODB_URI || 'mongodb://127.0.0.1:27017/coalminevr';

// ==========================================
// Middleware
// ==========================================
app.use(cors());
app.use(express.json());

// Incoming request logger (helpful for tracking Unity VR / AR payload submissions)
app.use((req, res, next) => {
  const timestamp = new Date().toISOString();
  console.log(`[${timestamp}] ${req.method} ${req.originalUrl}`);
  next();
});

// ==========================================
// Step 2: Session Schema & Model
// ==========================================
const sessionSchema = new mongoose.Schema(
  {
    traineeId: {
      type: String,
      required: [true, 'traineeId is required'],
      trim: true
    },
    mode: {
      type: String,
      required: [true, 'mode is required'],
      enum: {
        values: ['AR', 'VR'],
        message: 'Invalid mode: "{VALUE}". Mode must be either "AR" or "VR"'
      },
      uppercase: true,
      trim: true
    },
    score: {
      type: Number,
      required: [true, 'score is required'],
      min: [0, 'score cannot be negative']
    },
    totalAnswered: {
      type: Number,
      required: [true, 'totalAnswered is required'],
      min: [0, 'totalAnswered cannot be negative']
    },
    deviceModel: {
      type: String,
      trim: true,
      default: 'Unknown Device'
    },
    timestamp: {
      type: Date,
      default: Date.now
    }
  },
  {
    versionKey: false
  }
);

// Optimize query performance for dashboard sorting
sessionSchema.index({ timestamp: -1 });

const Session = mongoose.model('Session', sessionSchema);

// ==========================================
// Step 3: API Endpoints
// ==========================================

/**
 * GET /health
 * Returns service status, database connectivity, and uptime.
 */
app.get('/health', (req, res) => {
  const dbStates = ['disconnected', 'connected', 'connecting', 'disconnecting'];
  const currentDbState = dbStates[mongoose.connection.readyState] || 'unknown';

  res.status(200).json({
    status: 'ok',
    message: 'CoalMineVR Backend Service is healthy and operational',
    database: currentDbState,
    uptime: `${Math.floor(process.uptime())}s`,
    timestamp: new Date().toISOString()
  });
});

/**
 * POST /api/sessions
 * Accepts JSON payload from Unity VR/AR client and saves the session to MongoDB.
 */
app.post('/api/sessions', async (req, res) => {
  try {
    const { traineeId, mode, score, totalAnswered, deviceModel, timestamp } = req.body;

    const newSession = new Session({
      traineeId,
      mode,
      score,
      totalAnswered,
      deviceModel,
      ...(timestamp && { timestamp })
    });

    const savedSession = await newSession.save();

    console.log(`[Session Saved] Trainee: ${savedSession.traineeId} | Mode: ${savedSession.mode} | Score: ${savedSession.score}/${savedSession.totalAnswered}`);

    return res.status(201).json({
      success: true,
      message: 'Training session recorded successfully',
      data: savedSession
    });
  } catch (error) {
    if (error.name === 'ValidationError') {
      const validationErrors = Object.values(error.errors).map((err) => err.message);
      return res.status(400).json({
        success: false,
        error: 'Validation Error',
        details: validationErrors
      });
    }

    console.error('Error saving session:', error);
    return res.status(500).json({
      success: false,
      error: 'Internal Server Error',
      message: 'Failed to record session'
    });
  }
});

/**
 * GET /api/dashboard/results
 * Returns all trainee sessions sorted by newest first.
 */
app.get('/api/dashboard/results', async (req, res) => {
  try {
    const sessions = await Session.find().sort({ timestamp: -1 });

    return res.status(200).json({
      success: true,
      count: sessions.length,
      data: sessions
    });
  } catch (error) {
    console.error('Error fetching dashboard results:', error);
    return res.status(500).json({
      success: false,
      error: 'Internal Server Error',
      message: 'Failed to retrieve dashboard results'
    });
  }
});

// 404 Handler for undefined routes
app.use((req, res) => {
  res.status(404).json({
    success: false,
    error: 'Route not found'
  });
});

// ==========================================
// Database Connection & Server Start
// ==========================================
async function startServer() {
  try {
    console.log('Connecting to MongoDB...');
    await mongoose.connect(MONGODB_URI);
    console.log(' MongoDB connected successfully.');

    app.listen(PORT, () => {
      console.log(`===============================================`);
      console.log(` CoalMineVR Trainer Dashboard API Server`);
      console.log(` Server URL : http://localhost:${PORT}`);
      console.log(` Health Check: http://localhost:${PORT}/health`);
      console.log(` Dashboard   : http://localhost:${PORT}/api/dashboard/results`);
      console.log(` POST Endpoint: http://localhost:${PORT}/api/sessions`);
      console.log(`===============================================`);
    });
  } catch (error) {
    console.error('❌ Failed to connect to MongoDB:', error.message);
    console.error('Make sure MongoDB is running locally or check your MONGODB_URI in the .env file.');
    process.exit(1);
  }
}

// Graceful shutdown handling
process.on('SIGINT', async () => {
  console.log('\nShutting down server...');
  await mongoose.connection.close();
  console.log('MongoDB connection closed. Process terminated.');
  process.exit(0);
});

startServer();
