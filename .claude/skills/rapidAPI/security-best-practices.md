# Security Best Practices

Comprehensive security configuration for RapidAPI providers.

## Authentication Layers

### Layer 1: X-RapidAPI-Proxy-Secret (Required)

This is your **first line of defense**. Never skip this validation.

```python
# Python with FastAPI - Middleware approach
from fastapi import FastAPI, Request, HTTPException
from starlette.middleware.base import BaseHTTPMiddleware
import os

class RapidAPIAuthMiddleware(BaseHTTPMiddleware):
    async def dispatch(self, request: Request, call_next):
        # Skip health checks
        if request.url.path == "/health":
            return await call_next(request)
        
        secret = request.headers.get("X-RapidAPI-Proxy-Secret")
        expected = os.environ.get("RAPIDAPI_PROXY_SECRET")
        
        if not secret or secret != expected:
            raise HTTPException(
                status_code=403,
                detail="Invalid or missing RapidAPI proxy secret"
            )
        
        return await call_next(request)

app = FastAPI()
app.add_middleware(RapidAPIAuthMiddleware)
```

```typescript
// TypeScript with Express
import express, { Request, Response, NextFunction } from 'express';

const RAPIDAPI_PROXY_SECRET = process.env.RAPIDAPI_PROXY_SECRET;

const rapidApiAuth = (req: Request, res: Response, next: NextFunction) => {
  const secret = req.headers['x-rapidapi-proxy-secret'];
  
  if (secret !== RAPIDAPI_PROXY_SECRET) {
    return res.status(403).json({
      error: 'Unauthorized',
      message: 'Invalid RapidAPI proxy secret'
    });
  }
  
  next();
};

app.use(rapidApiAuth);
```

### Layer 2: IP Whitelist (Recommended)

Allow only RapidAPI Runtime IPs. Get current list from:
Provider Dashboard → Hub Listing → Gateway → "Rapid Runtime IP Addresses"

```python
# Python IP whitelist
RAPIDAPI_IPS = {
    "52.201.25.58",
    "52.70.204.155",
    "54.87.170.149",
    # ... add all from dashboard
}

def validate_ip(request):
    client_ip = request.headers.get("X-Forwarded-For", "").split(",")[0].strip()
    if client_ip not in RAPIDAPI_IPS:
        raise HTTPException(status_code=403, detail="IP not whitelisted")
```

**Note**: Update whitelist periodically as RapidAPI may add IPs.

### Layer 3: User-Level Authentication (Optional)

For APIs requiring user accounts:

```python
# Validate X-RapidAPI-User header
def get_current_user(request: Request):
    rapidapi_user = request.headers.get("X-RapidAPI-User")
    
    # Map RapidAPI user to your internal user
    user = db.get_user_by_rapidapi_id(rapidapi_user)
    
    if not user:
        # Auto-create user on first request
        user = db.create_user(rapidapi_id=rapidapi_user)
    
    return user
```

## Subscription-Based Access Control

Use `X-RapidAPI-Subscription` header to gate features:

```python
from enum import Enum
from functools import wraps

class SubscriptionTier(Enum):
    BASIC = "BASIC"
    PRO = "PRO"
    ULTRA = "ULTRA"
    MEGA = "MEGA"

def require_subscription(min_tier: SubscriptionTier):
    tier_order = [SubscriptionTier.BASIC, SubscriptionTier.PRO, 
                  SubscriptionTier.ULTRA, SubscriptionTier.MEGA]
    
    def decorator(func):
        @wraps(func)
        async def wrapper(request: Request, *args, **kwargs):
            subscription = request.headers.get("X-RapidAPI-Subscription", "BASIC")
            
            try:
                user_tier = SubscriptionTier(subscription)
            except ValueError:
                user_tier = SubscriptionTier.BASIC
            
            if tier_order.index(user_tier) < tier_order.index(min_tier):
                raise HTTPException(
                    status_code=403,
                    detail=f"This endpoint requires {min_tier.value} subscription or higher"
                )
            
            return await func(request, *args, **kwargs)
        return wrapper
    return decorator

# Usage
@app.get("/premium-endpoint")
@require_subscription(SubscriptionTier.PRO)
async def premium_endpoint(request: Request):
    return {"data": "premium content"}
```

## Threat Protection Configuration

### Enable in Dashboard

Hub Listing → Gateway → Security:

1. **Threat Protection**: Toggle ON
2. **Content-Type Requirement**: Toggle based on your needs
3. **Request Schema Validation**: Toggle ON (validates against OAS)
4. **Max Request Size**: Set reasonable limit (e.g., 10MB)

### Server-Side Input Validation

Never trust client input, even through RapidAPI:

```python
from pydantic import BaseModel, Field, validator
import re

class UserInput(BaseModel):
    query: str = Field(..., min_length=1, max_length=1000)
    limit: int = Field(default=10, ge=1, le=100)
    
    @validator('query')
    def sanitize_query(cls, v):
        # Remove potential SQL injection patterns
        dangerous_patterns = [
            r"(\bUNION\b|\bSELECT\b|\bDROP\b|\bINSERT\b|\bUPDATE\b|\bDELETE\b)",
            r"(--|;|\/\*|\*\/)",
            r"(<script|javascript:|on\w+=)"
        ]
        for pattern in dangerous_patterns:
            if re.search(pattern, v, re.IGNORECASE):
                raise ValueError("Invalid characters in query")
        return v
```

### SQL Injection Prevention

Always use parameterized queries:

```python
# BAD - SQL injection vulnerable
cursor.execute(f"SELECT * FROM users WHERE id = {user_id}")

# GOOD - Parameterized
cursor.execute("SELECT * FROM users WHERE id = ?", (user_id,))

# BEST - ORM with validation
user = await User.get_or_none(id=validated_user_id)
```

## Rate Limiting Implementation

RapidAPI enforces rate limits, but implement server-side as backup:

```python
from fastapi import Request, HTTPException
from collections import defaultdict
import time

class RateLimiter:
    def __init__(self, requests_per_second: int = 10):
        self.requests_per_second = requests_per_second
        self.requests = defaultdict(list)
    
    def is_allowed(self, user_id: str) -> bool:
        now = time.time()
        user_requests = self.requests[user_id]
        
        # Remove old requests
        user_requests[:] = [t for t in user_requests if now - t < 1.0]
        
        if len(user_requests) >= self.requests_per_second:
            return False
        
        user_requests.append(now)
        return True

rate_limiter = RateLimiter(requests_per_second=10)

@app.middleware("http")
async def rate_limit_middleware(request: Request, call_next):
    user_id = request.headers.get("X-RapidAPI-User", "anonymous")
    
    if not rate_limiter.is_allowed(user_id):
        raise HTTPException(
            status_code=429,
            detail="Rate limit exceeded",
            headers={"Retry-After": "1"}
        )
    
    return await call_next(request)
```

## OAuth 2.0 Implementation

### Client Credentials Flow

For machine-to-machine authentication:

```python
# Token endpoint
@app.post("/oauth/token")
async def get_token(
    grant_type: str = Form(...),
    client_id: str = Form(...),
    client_secret: str = Form(...)
):
    if grant_type != "client_credentials":
        raise HTTPException(400, "Unsupported grant type")
    
    # Validate client credentials
    client = await validate_client(client_id, client_secret)
    if not client:
        raise HTTPException(401, "Invalid client credentials")
    
    # Generate access token
    token = create_access_token(client_id)
    
    return {
        "access_token": token,
        "token_type": "Bearer",
        "expires_in": 3600
    }
```

### Configure in RapidAPI

Definition → Security → New Scheme:
- Name: "OAuth2"
- Grant Type: Client Credentials
- Token URL: `https://your-api.com/oauth/token`
- Client Authentication: Header or Body

## Secret Management

### Environment Variables

Never hardcode secrets:

```python
# config.py
import os
from pydantic import BaseSettings

class Settings(BaseSettings):
    rapidapi_proxy_secret: str
    database_url: str
    jwt_secret: str
    
    class Config:
        env_file = ".env"

settings = Settings()
```

### Secret Rotation

Implement rotation without downtime:

```python
# Support multiple valid secrets during rotation
VALID_SECRETS = [
    os.environ.get("RAPIDAPI_PROXY_SECRET"),
    os.environ.get("RAPIDAPI_PROXY_SECRET_PREVIOUS")  # Old secret during rotation
]

def validate_secret(provided: str) -> bool:
    return any(
        provided == secret 
        for secret in VALID_SECRETS 
        if secret is not None
    )
```

## Logging Security

### What to Log

```python
import logging
from datetime import datetime

logger = logging.getLogger("api.security")

def log_request(request: Request, response_status: int):
    logger.info({
        "timestamp": datetime.utcnow().isoformat(),
        "user": request.headers.get("X-RapidAPI-User"),
        "subscription": request.headers.get("X-RapidAPI-Subscription"),
        "endpoint": request.url.path,
        "method": request.method,
        "status": response_status,
        "ip": request.headers.get("X-Forwarded-For", "unknown"),
        # DO NOT log:
        # - Request bodies (may contain PII)
        # - Authorization headers
        # - API keys
    })
```

### PII Protection

Configure RapidAPI logging: Hub Listing → Gateway → Logging Configurations

Disable for sensitive endpoints:
- Request body logging
- Response body logging
- Header logging (if sensitive)

## Error Handling Security

Don't leak internal details:

```python
@app.exception_handler(Exception)
async def generic_exception_handler(request: Request, exc: Exception):
    # Log full error internally
    logger.error(f"Unhandled exception: {exc}", exc_info=True)
    
    # Return sanitized error to client
    return JSONResponse(
        status_code=500,
        content={
            "error": "Internal server error",
            "message": "An unexpected error occurred",
            "request_id": request.state.request_id  # For support correlation
        }
    )
```

## Security Checklist

### Pre-Launch

- [ ] X-RapidAPI-Proxy-Secret validation implemented
- [ ] All endpoints have input validation
- [ ] SQL queries are parameterized
- [ ] Secrets stored in environment variables
- [ ] Error responses don't leak internals
- [ ] Logging configured (without PII)
- [ ] Rate limiting implemented
- [ ] HTTPS enforced

### RapidAPI Configuration

- [ ] Threat protection enabled
- [ ] Request schema validation enabled
- [ ] Appropriate request size limits
- [ ] Proper timeout configuration
- [ ] Logging settings reviewed

### Ongoing

- [ ] Monitor for unusual traffic patterns
- [ ] Rotate secrets quarterly
- [ ] Review access logs monthly
- [ ] Update IP whitelist as needed
- [ ] Dependency security updates
