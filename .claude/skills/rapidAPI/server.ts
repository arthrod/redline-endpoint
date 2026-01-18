/**
 * RapidAPI-Ready Express/TypeScript Backend Template
 *
 * Usage:
 *   bun run server.ts
 *   # or
 *   npx tsx server.ts
 *
 * Dependencies (package.json):
 *   {
 *     "dependencies": {
 *       "express": "^4.21",
 *       "helmet": "^8.0",
 *       "cors": "^2.8",
 *       "zod": "^3.23"
 *     },
 *     "devDependencies": {
 *       "@types/express": "^5.0",
 *       "@types/cors": "^2.8",
 *       "typescript": "^5.7"
 *     }
 *   }
 */

import express, { Request, Response, NextFunction, RequestHandler } from 'express';
import helmet from 'helmet';
import cors from 'cors';
import { z } from 'zod';
import crypto from 'crypto';

// Configuration
const config = {
  port: parseInt(process.env.PORT || '8000'),
  rapidapiProxySecret: process.env.RAPIDAPI_PROXY_SECRET || '',
  debug: process.env.DEBUG === 'true',
};

// Types
interface RapidAPIHeaders {
  user: string;
  subscription: string;
  proxySecret: string | undefined;
  forwardedFor: string;
}

declare global {
  namespace Express {
    interface Request {
      requestId: string;
      rapidapi: RapidAPIHeaders;
    }
  }
}

// Subscription tiers in order
const SUBSCRIPTION_TIERS = ['BASIC', 'PRO', 'ULTRA', 'MEGA', 'CUSTOM'] as const;
type SubscriptionTier = typeof SUBSCRIPTION_TIERS[number];

// Logger
const logger = {
  info: (event: string, data?: Record<string, unknown>) => {
    console.log(JSON.stringify({ 
      timestamp: new Date().toISOString(), 
      level: 'info', 
      event, 
      ...data 
    }));
  },
  warn: (event: string, data?: Record<string, unknown>) => {
    console.warn(JSON.stringify({ 
      timestamp: new Date().toISOString(), 
      level: 'warn', 
      event, 
      ...data 
    }));
  },
  error: (event: string, error: Error, data?: Record<string, unknown>) => {
    console.error(JSON.stringify({ 
      timestamp: new Date().toISOString(), 
      level: 'error', 
      event,
      error: error.message,
      stack: config.debug ? error.stack : undefined,
      ...data 
    }));
  },
};

// App setup
const app = express();

// Security middleware
app.use(helmet());
app.use(cors({
  origin: 'https://rapidapi.com',
  credentials: true,
  methods: ['GET', 'POST', 'PUT', 'DELETE', 'PATCH', 'OPTIONS'],
  allowedHeaders: ['Content-Type', 'X-RapidAPI-Key', 'X-RapidAPI-Host', 'X-RapidAPI-Proxy-Secret'],
}));
app.use(express.json({ limit: '10mb' }));

// Request ID middleware
app.use((req: Request, _res: Response, next: NextFunction) => {
  req.requestId = crypto.randomUUID();
  next();
});

// Extract RapidAPI headers
app.use((req: Request, _res: Response, next: NextFunction) => {
  req.rapidapi = {
    user: (req.headers['x-rapidapi-user'] as string) || 'anonymous',
    subscription: (req.headers['x-rapidapi-subscription'] as string) || 'BASIC',
    proxySecret: req.headers['x-rapidapi-proxy-secret'] as string | undefined,
    forwardedFor: (req.headers['x-forwarded-for'] as string) || req.ip || 'unknown',
  };
  next();
});

// Request logging middleware
app.use((req: Request, res: Response, next: NextFunction) => {
  const start = Date.now();

  res.on('finish', () => {
    if (req.path !== '/health') {
      logger.info('api_request', {
        requestId: req.requestId,
        method: req.method,
        path: req.path,
        status: res.statusCode,
        durationMs: Date.now() - start,
        user: req.rapidapi.user,
        subscription: req.rapidapi.subscription,
      });
    }
  });

  next();
});

// RapidAPI proxy secret verification middleware
const verifyRapidAPISecret: RequestHandler = (req, res, next) => {
  // Skip for health check
  if (req.path === '/health') {
    return next();
  }

  // Skip if not configured (development mode)
  if (!config.rapidapiProxySecret) {
    return next();
  }

  if (req.rapidapi.proxySecret !== config.rapidapiProxySecret) {
    logger.warn('auth_failed', {
      requestId: req.requestId,
      reason: 'invalid_proxy_secret',
    });

    return res.status(403).json({
      error: 'FORBIDDEN',
      message: 'Invalid or missing RapidAPI proxy secret',
      requestId: req.requestId,
    });
  }

  next();
};

app.use(verifyRapidAPISecret);

// Subscription tier check middleware factory
const requireSubscription = (minTier: SubscriptionTier): RequestHandler => {
  return (req, res, next) => {
    const userTierIndex = SUBSCRIPTION_TIERS.indexOf(req.rapidapi.subscription as SubscriptionTier);
    const requiredTierIndex = SUBSCRIPTION_TIERS.indexOf(minTier);

    // Treat unknown tiers as BASIC
    const effectiveUserIndex = userTierIndex === -1 ? 0 : userTierIndex;

    if (effectiveUserIndex < requiredTierIndex) {
      return res.status(403).json({
        error: 'SUBSCRIPTION_REQUIRED',
        message: `This endpoint requires ${minTier} subscription or higher`,
        requestId: req.requestId,
      });
    }

    next();
  };
};

// --- Schemas ---

const DataQuerySchema = z.object({
  query: z.string().min(1).max(1000),
  limit: z.coerce.number().int().min(1).max(100).default(10),
});

const AnalyzeRequestSchema = z.object({
  query: z.string().min(1).max(1000),
  options: z.object({
    includeMetadata: z.boolean().default(false),
  }).optional(),
});

// --- Endpoints ---

// Health check (no auth)
app.get('/health', (_req: Request, res: Response) => {
  res.json({
    status: 'healthy',
    version: '1.0.0',
    timestamp: new Date().toISOString(),
  });
});

// Data search (all tiers)
app.get('/data', (req: Request, res: Response) => {
  const parsed = DataQuerySchema.safeParse(req.query);

  if (!parsed.success) {
    return res.status(400).json({
      error: 'VALIDATION_ERROR',
      message: 'Invalid query parameters',
      details: parsed.error.flatten().fieldErrors,
      requestId: req.requestId,
    });
  }

  const { query, limit } = parsed.data;

  // Simulate data retrieval
  const results = Array.from({ length: Math.min(limit, 5) }, (_, i) => ({
    id: i + 1,
    name: `Result ${i + 1}`,
    query,
    relevance: Math.random().toFixed(2),
  }));

  res.json({
    results,
    total: results.length,
    query,
    requestId: req.requestId,
  });
});

// Advanced analysis (PRO+ only)
app.post('/data/analyze', requireSubscription('PRO'), (req: Request, res: Response) => {
  const parsed = AnalyzeRequestSchema.safeParse(req.body);

  if (!parsed.success) {
    return res.status(400).json({
      error: 'VALIDATION_ERROR',
      message: 'Invalid request body',
      details: parsed.error.flatten().fieldErrors,
      requestId: req.requestId,
    });
  }

  const { query, options } = parsed.data;

  // Simulate analysis
  const analysis = {
    query,
    wordCount: query.split(/\s+/).length,
    charCount: query.length,
    sentiment: 'positive',
    confidence: 0.85,
  };

  res.json({
    analysis,
    metadata: options?.includeMetadata ? {
      user: req.rapidapi.user,
      subscription: req.rapidapi.subscription,
      processedAt: new Date().toISOString(),
    } : undefined,
    requestId: req.requestId,
  });
});

// Premium endpoint (ULTRA+ only)
app.get('/premium/insights', requireSubscription('ULTRA'), (req: Request, res: Response) => {
  res.json({
    insights: [
      { type: 'trend', value: 'increasing', confidence: 0.92 },
      { type: 'pattern', value: 'seasonal', confidence: 0.88 },
    ],
    user: req.rapidapi.user,
    requestId: req.requestId,
  });
});

// --- Error Handlers ---

// 404 handler
app.use((_req: Request, res: Response) => {
  res.status(404).json({
    error: 'NOT_FOUND',
    message: 'Endpoint not found',
  });
});

// Global error handler
app.use((err: Error, req: Request, res: Response, _next: NextFunction) => {
  logger.error('unhandled_exception', err, { requestId: req.requestId });

  res.status(500).json({
    error: 'INTERNAL_ERROR',
    message: 'An unexpected error occurred',
    requestId: req.requestId,
    ...(config.debug && { stack: err.stack }),
  });
});

// Start server
app.listen(config.port, () => {
  logger.info('api_startup', { 
    port: config.port, 
    debug: config.debug,
    proxySecretConfigured: !!config.rapidapiProxySecret,
  });
});
