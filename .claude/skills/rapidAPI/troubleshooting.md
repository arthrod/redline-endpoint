# Troubleshooting Guide

Common issues and solutions for RapidAPI providers.

## HTTP Status Codes

### 400 Bad Request

**Symptoms**: Consumer receives 400 with validation error

**Causes & Solutions**:

| Cause | Solution |
|-------|----------|
| Missing required parameter | Check OAS spec matches implementation |
| Invalid parameter format | Improve error messages with specifics |
| Schema validation failed | Verify request body matches schema |
| Content-Type mismatch | Document required Content-Type headers |

**Example improved error response**:
```json
{
  "error": "VALIDATION_ERROR",
  "message": "Request validation failed",
  "details": {
    "location": {"type": "required", "message": "location is required"},
    "units": {"type": "enum", "message": "units must be one of: metric, imperial, kelvin"}
  }
}
```

### 401 Unauthorized

**Symptoms**: Consumer can't access API despite valid subscription

**Causes & Solutions**:

| Cause | Solution |
|-------|----------|
| API requires additional auth beyond RapidAPI | Configure OAuth/API key in Security settings |
| OAuth token expired | Implement token refresh flow |
| User not subscribed to required plan | Check X-RapidAPI-Subscription header |
| Consumer using wrong API key | Verify key in RapidAPI dashboard |

### 403 Forbidden

**Symptoms**: Valid authentication but access denied

**Common causes**:

1. **X-RapidAPI-Proxy-Secret validation failing**
   ```python
   # Debug: Log the received secret
   received = request.headers.get("X-RapidAPI-Proxy-Secret")
   expected = os.environ.get("RAPIDAPI_PROXY_SECRET")
   logger.debug(f"Received: {received[:10]}..., Expected: {expected[:10]}...")
   ```

2. **IP whitelist blocking RapidAPI**
   - Add all RapidAPI Runtime IPs to your whitelist
   - Check with: Hub Listing → Gateway → Runtime IP Addresses

3. **CORS blocking test requests**
   - Configure CORS headers for rapidapi.com origin
   - See CORS section below

4. **Geo-blocking**
   - RapidAPI Runtime operates from US/EU data centers
   - Whitelist relevant regions

### 404 Not Found

**Symptoms**: Endpoint exists but returns 404

**Causes & Solutions**:

| Cause | Solution |
|-------|----------|
| Base URL mismatch | Verify API Specs → Settings → Base URL |
| Version routing issue | Check version prefix in paths |
| Path parameter encoding | URL-decode path params server-side |
| Trailing slash mismatch | Handle both `/endpoint` and `/endpoint/` |

### 429 Too Many Requests

**Symptoms**: Rate limit errors

**Debug steps**:

1. Check consumer's plan limits in Monetize tab
2. Verify rate limit configuration matches plan
3. Check if consumer is batching requests incorrectly

**Recommended response headers**:
```python
return JSONResponse(
    status_code=429,
    content={"error": "Rate limit exceeded"},
    headers={
        "X-RateLimit-Limit": str(limit),
        "X-RateLimit-Remaining": "0",
        "X-RateLimit-Reset": str(reset_timestamp),
        "Retry-After": str(seconds_until_reset)
    }
)
```

### 500 Internal Server Error

**Symptoms**: Your API crashes

**Immediate actions**:

1. Check server logs for stack trace
2. Review recent deployments
3. Check external dependency status
4. Monitor resource usage (CPU, memory, connections)

**Prevention**:
```python
# Global exception handler
@app.exception_handler(Exception)
async def handle_exception(request: Request, exc: Exception):
    # Log full error
    logger.exception(f"Unhandled exception: {exc}")
    
    # Return sanitized response
    return JSONResponse(
        status_code=500,
        content={
            "error": "INTERNAL_ERROR",
            "message": "An unexpected error occurred",
            "request_id": request.state.request_id
        }
    )
```

### 502 Bad Gateway

**Symptoms**: RapidAPI can't reach your server

**Causes**:

1. Server not running
2. DNS resolution failure
3. SSL certificate issues
4. Firewall blocking requests

**Debug**: Test direct access to your server bypassing RapidAPI:
```bash
curl -v https://your-api-server.com/health
```

### 503 Service Unavailable

**Symptoms**: Server temporarily overloaded

**Solutions**:

1. Scale server resources
2. Implement request queuing
3. Add caching layer
4. Review rate limits

### 504 Gateway Timeout

**Symptoms**: Request takes too long

**Solutions**:

| Solution | When to use |
|----------|-------------|
| Increase timeout | Up to 180s max (Hub Listing → Gateway) |
| Optimize queries | Database slow queries |
| Add caching | Repeated expensive operations |
| Async processing | Long-running jobs (return job ID, poll for result) |

## CORS Issues

**Symptoms**: "Access-Control-Allow-Origin" errors in browser testing

If not using Rapid Runtime, configure your server:

```python
# FastAPI
from fastapi.middleware.cors import CORSMiddleware

app.add_middleware(
    CORSMiddleware,
    allow_origins=["https://rapidapi.com"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)
```

```javascript
// Express
const cors = require('cors');

app.use(cors({
  origin: 'https://rapidapi.com',
  credentials: true,
  methods: ['GET', 'POST', 'PUT', 'DELETE', 'PATCH', 'OPTIONS'],
  allowedHeaders: ['Content-Type', 'X-RapidAPI-Key', 'X-RapidAPI-Host']
}));
```

**Handle OPTIONS preflight**:
```python
@app.options("/{path:path}")
async def options_handler():
    return Response(
        status_code=200,
        headers={
            "Access-Control-Allow-Origin": "https://rapidapi.com",
            "Access-Control-Allow-Methods": "GET, POST, PUT, DELETE, OPTIONS",
            "Access-Control-Allow-Headers": "Content-Type, X-RapidAPI-Key, X-RapidAPI-Host",
        }
    )
```

## SSL/TLS Issues

**Symptoms**: SSL handshake failures

**Checklist**:

- [ ] Certificate valid and not expired
- [ ] Certificate covers correct domain
- [ ] Full certificate chain included
- [ ] TLS 1.2+ supported
- [ ] Strong cipher suites enabled

**Test SSL**:
```bash
openssl s_client -connect your-api.com:443 -servername your-api.com
```

## Analytics Discrepancies

**Symptoms**: RapidAPI analytics don't match server logs

**Common causes**:

1. **Caching**: Rapid Runtime may cache responses (check Cache-Control headers)
2. **Time zones**: RapidAPI uses UTC
3. **Failed requests**: RapidAPI counts requests that fail before reaching your server
4. **Test calls**: Dashboard testing counts toward analytics

## Endpoint Not Appearing

**Symptoms**: Endpoint configured but not visible in listing

**Checklist**:

1. Endpoint saved (not just drafted)
2. Version status is "Active" or "Current"
3. No OAS syntax errors
4. Endpoint has required method and path
5. Clear browser cache and refresh

## Subscription/Billing Issues

### Consumer says they're subscribed but can't access

1. Verify subscription in Studio → Hub Listing → Community tab
2. Check if correct version is subscribed
3. Confirm plan includes required endpoints
4. Check for account-level issues on consumer's side

### Payouts not received

1. Verify minimum threshold met ($50)
2. Check payment details in Payouts settings
3. Review transaction status in Monetize → Transactions
4. Contact RapidAPI support with transaction IDs

## Performance Degradation

### Diagnosing slow responses

Use RapidAPI's testing across data centers:
Provider Dashboard → Testing → Run from multiple locations

**Breakdown analysis**:
```
Total Response Time = DNS + Connection + TLS + TTFB + Transfer
                    = Network latency + Server processing + Data transfer

Focus on:
- TTFB (Time to First Byte): Server processing time
- Transfer: Response size optimization
```

### Optimization checklist

1. [ ] Database queries optimized (indexes, explain plans)
2. [ ] N+1 queries eliminated
3. [ ] Caching implemented (Redis/Memcached)
4. [ ] Response compression enabled (gzip)
5. [ ] Connection pooling configured
6. [ ] Async I/O for external calls
7. [ ] CDN for static responses

## Logging Best Practices

### Request tracing

Add request ID for end-to-end debugging:

```python
import uuid
from fastapi import Request

@app.middleware("http")
async def add_request_id(request: Request, call_next):
    request_id = str(uuid.uuid4())
    request.state.request_id = request_id
    
    response = await call_next(request)
    response.headers["X-Request-ID"] = request_id
    
    return response
```

### Structured logging

```python
import structlog

logger = structlog.get_logger()

@app.middleware("http")
async def log_requests(request: Request, call_next):
    start_time = time.time()
    
    response = await call_next(request)
    
    duration = time.time() - start_time
    
    logger.info(
        "api_request",
        request_id=request.state.request_id,
        method=request.method,
        path=request.url.path,
        status=response.status_code,
        duration_ms=round(duration * 1000, 2),
        user=request.headers.get("X-RapidAPI-User"),
        subscription=request.headers.get("X-RapidAPI-Subscription"),
    )
    
    return response
```

## Getting Support

### RapidAPI Support Channels

1. **Documentation**: docs.rapidapi.com
2. **Community Forum**: community.rapidapi.com
3. **Support Tickets**: Via dashboard
4. **Enterprise Support**: Dedicated account manager

### Information to Include

When reporting issues, provide:

- API name and ID
- Affected endpoint(s)
- Request example (sanitized)
- Error response
- Request ID (if available)
- Timestamp (UTC)
- Steps to reproduce
