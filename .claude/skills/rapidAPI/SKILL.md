---
name: rapidapi-provider
description: Comprehensive guide for publishing, configuring, and monetizing APIs on RapidAPI Hub. Use when creating new API listings, setting up endpoints, configuring security (X-RapidAPI-Proxy-Secret, OAuth, headers), designing pricing plans, optimizing for discoverability, or troubleshooting provider dashboard issues. Covers OpenAPI/OAS integration, versioning strategies, rate limiting, threat protection, and revenue optimization.
---

# RapidAPI Provider Skill

Complete guide for publishing and monetizing APIs on RapidAPI Hub.

## Quick Start Checklist

1. Create account at rapidapi.com/studio or [HUB_URL]/studio
2. Click "Add New API" → provide name (without "API" suffix), short description, category
3. Configure Base URL under API Specs → Settings
4. Define endpoints (REST, GraphQL, Kafka supported)
5. Set up security (at minimum, validate X-RapidAPI-Proxy-Secret)
6. Configure pricing plans under Hub Listing → Monetize
7. Test endpoints from multiple geographic locations
8. Publish and monitor via Analytics dashboard

## Architecture Overview

```
┌─────────────┐     ┌──────────────────┐     ┌─────────────┐
│ API Consumer│────▶│  Rapid Runtime   │────▶│  Your API   │
│             │◀────│  (Proxy Layer)   │◀────│  Server     │
└─────────────┘     └──────────────────┘     └─────────────┘
                            │
                    Adds headers:
                    - X-RapidAPI-Proxy-Secret
                    - X-RapidAPI-User
                    - X-RapidAPI-Subscription
                    - X-Forwarded-For
```

All requests flow through RapidAPI's Runtime proxy, which handles authentication, rate limiting, billing, and analytics.

## Endpoint Configuration

### Creating REST Endpoints

Navigate: My APIs → Definition → API Specs → Endpoints → Create REST Endpoint

Required fields:
- **Name**: Descriptive, appears in documentation sidebar
- **Method**: GET, POST, PUT, DELETE, PATCH
- **Path**: Route with optional path parameters using `{paramName}` syntax

Optional but recommended:
- **Description**: Markdown supported, explain what endpoint does
- **External Doc URL**: Link to detailed documentation
- **Request/Response examples**: Auto-generate from test calls

### Parameter Types

| Location | Use Case | Notes |
|----------|----------|-------|
| Path | Resource identifiers | `/users/{userId}` |
| Query | Filtering, pagination | `?limit=10&offset=0` |
| Header | Auth tokens, content-type | Custom headers consumer must send |
| Body | POST/PUT/PATCH payloads | JSON, form-data, multipart |

### Mock Responses

Enable during development to return predefined responses:

1. Edit endpoint → scroll to Mock Response
2. Specify status code, headers, body
3. Enable via slider
4. Consumers see "mock response" notification when testing

## Security Configuration

### X-RapidAPI-Proxy-Secret (Critical)

Every request from RapidAPI includes a unique `X-RapidAPI-Proxy-Secret` header. **Always validate this server-side.**

```python
# Python/FastAPI example
from fastapi import Header, HTTPException

RAPIDAPI_PROXY_SECRET = "your-secret-from-dashboard"

@app.get("/endpoint")
async def endpoint(
    x_rapidapi_proxy_secret: str = Header(None, alias="X-RapidAPI-Proxy-Secret")
):
    if x_rapidapi_proxy_secret != RAPIDAPI_PROXY_SECRET:
        raise HTTPException(status_code=403, detail="Unauthorized")
    # Process request...
```

```javascript
// Node.js/Express example
const RAPIDAPI_PROXY_SECRET = process.env.RAPIDAPI_PROXY_SECRET;

app.use((req, res, next) => {
  if (req.headers['x-rapidapi-proxy-secret'] !== RAPIDAPI_PROXY_SECRET) {
    return res.status(403).json({ error: 'Unauthorized' });
  }
  next();
});
```

Find your secret: Provider Dashboard → API Definition → Security tab

### Additional Security Headers (Auto-Injected)

| Header | Purpose |
|--------|---------|
| `X-RapidAPI-User` | Username of requester |
| `X-RapidAPI-Subscription` | Plan name (BASIC, PRO, etc.) |
| `X-RapidAPI-Version` | Runtime version |
| `X-Forwarded-For` | Original client IP |
| `X-Forwarded-Host` | Requested hostname |

### Secret Headers & Parameters

Hide credentials from consumers while passing them to your API:

Navigate: Definition → API Specs → Settings → Secret Headers & Parameters

Use cases:
- Internal API keys consumers shouldn't see
- Environment-specific configuration
- Service account tokens

### Threat Protection

Enable in Hub Listing → Gateway:

1. **SQL/JS Injection Protection**: Blocks malicious payloads (requires Content-Type header)
2. **Request Schema Validation**: Validates path, query, headers against OpenAPI spec
3. **Request Size Limit**: Maximum payload size (default: no limit)
4. **Proxy Timeout**: Response timeout (default: 180s, max: 180s)

### OAuth 2.0 Configuration

Supported grant types:
- Client Credentials
- Authorization Code (with optional PKCE)
- Password

Configure: Definition → Security → New Scheme

Required fields vary by grant type:
- Token URL
- Authorization URL (for auth code flow)
- Scopes (optional)
- Client Authentication: Header or Body

## Pricing & Monetization

### Plan Types

| Type | Best For | Configuration |
|------|----------|---------------|
| Free | Trial/Discovery | Max 500K requests/month on rapidapi.com |
| Monthly Subscription | Predictable usage | Fixed price + optional overage |
| Pay Per Use | Variable workloads | Per-request pricing |
| Tiered | Volume discounts | Different rates at usage thresholds |

### Creating Plans

Navigate: Hub Listing → Monetize → Public Plans

Every plan requires:
- **Requests object**: Monthly/daily quota with overage pricing
- **Rate limit**: Requests per second/minute/hour

Recommended structure:
```
BASIC (Free)     → 100 requests/month, no overage, 1 req/sec
PRO ($29/mo)     → 10,000 requests/month, $0.003 overage, 10 req/sec
ULTRA ($99/mo)   → 100,000 requests/month, $0.001 overage, 50 req/sec
MEGA (Custom)    → Private plan for enterprise clients
```

### Monetization Best Practices

1. **Always offer a free tier**: Let developers test before committing
2. **Set reasonable overage fees**: <$1 per request for most APIs (high fees cause churn)
3. **Use Objects for granular billing**: Charge differently per endpoint/resource type
4. **Add Features**: Non-endpoint benefits (24h support, SLA guarantee, etc.)
5. **Private plans for enterprise**: Custom pricing for large clients
6. **Mark one plan as Recommended**: Guides consumer decisions

### Objects (Usage-Based Billing)

Define custom billing units beyond raw requests:

```
Objects:
- "AI Tokens" → charge per 1000 tokens processed
- "Images" → charge per image generated
- "Records" → charge per database record accessed
```

Endpoints can be associated with objects to track specific resource consumption.

### Revenue Expectations

- RapidAPI takes **20% commission** on rapidapi.com (industry standard)
- APILayer alternative: 15% commission
- Payouts processed via PayPal or bank transfer
- View transactions: Hub Listing → Monetize → Transactions

## OpenAPI/OAS Integration

### Supported Formats

- OpenAPI 3.0.x, 3.1.x (JSON or YAML)
- Postman Collections (JSON)
- GraphQL Schema (JSON, TXT, GRAPHQL, GRAPHQLS)

### RapidAPI Extensions

Custom `x-` fields enhance OAS documents:

```yaml
openapi: "3.0.0"
info:
  title: "My API"
  description: "Short description for search results"
  version: "1.0.0"
  x-category: "Data"
  x-long-description: "Detailed description for About tab"
  x-website: "https://myapi.com"
  x-public: true
  x-version-lifecycle: "active"  # active, beta, deprecated
  x-thumbnail: "https://example.com/logo.png"
  x-badges:
    - name: "Type"
      value: "REST"
```

### Auto-Generated Fields

After upload, RapidAPI adds:
```yaml
x-rapidapi-info:
  apiId: "api_xxxxx"
  apiVersionId: "apiversion_xxxxx"
x-gateways:
  - url: "https://your-api.p.rapidapi.com"
```

### Upload Methods

1. **Studio UI**: Hub Listing → API Specs → Import OpenAPI
2. **Platform API**: Programmatic upload with full metadata support

```javascript
// Platform API upload example
const formData = new FormData();
formData.append('file', fs.readFileSync('openapi.json'));
formData.append('ownerId', 'YOUR_OWNER_ID');

const response = await axios.post(
  'https://platformapi.p.rapidapi.com/v1/apis/rapidapi-file',
  formData,
  {
    headers: {
      'X-RapidAPI-Key': 'YOUR_KEY',
      'X-RapidAPI-Host': 'platformapi.p.rapidapi.com',
      ...formData.getHeaders()
    }
  }
);
```

## Versioning Strategy

### When to Create New Versions

- Breaking changes to request/response schema
- Endpoint deprecation
- Major functionality changes

Do NOT create new version for:
- Bug fixes
- New optional parameters
- Additional endpoints
- Performance improvements

### Version Lifecycle

| Status | Visibility | Use Case |
|--------|------------|----------|
| Draft | Hidden | Development/testing |
| Active | Public | Current recommended version |
| Deprecated | Limited | Visible only to existing users |

Set version status: API Specs dropdown → Version Settings

### Migration Best Practices

1. Keep deprecated versions running for 6-12 months
2. Document migration path in version description
3. Use x-version-lifecycle in OAS to signal status
4. Notify subscribers via email before deprecation

## Performance Optimization

### Latency Targets

| API Type | Good | Acceptable | Poor |
|----------|------|------------|------|
| Simple lookup | <100ms | <300ms | >500ms |
| Data processing | <500ms | <1s | >2s |
| AI/ML inference | <2s | <5s | >10s |

### Optimization Checklist

**Server-side:**
- [ ] Database query optimization (indexes, connection pooling)
- [ ] Response compression (gzip/brotli)
- [ ] Caching layer (Redis/Memcached)
- [ ] Async processing for heavy operations
- [ ] Geographic distribution (multi-region deployment)

**API design:**
- [ ] Pagination for list endpoints
- [ ] Field selection (`?fields=id,name`)
- [ ] Sparse responses by default
- [ ] Batch endpoints for multiple operations

**RapidAPI-specific:**
- [ ] Test from all 9 geographic data centers
- [ ] Monitor via Analytics dashboard
- [ ] Set appropriate timeout values
- [ ] Configure CDN for static responses if applicable

### Geographic Testing

Use RapidAPI's built-in testing across data centers:

Provider Dashboard → Testing → Select locations → Run tests

Optimize for regions where most consumers reside.

## Discoverability & Marketing

### Listing Optimization

**Name**: Clear, specific, no "API" suffix (auto-appended)
- ✅ "Weather Forecast"
- ❌ "Weather Forecast API"

**Short Description**: 1-2 sentences, keyword-rich, appears in search results

**Long Description** (About tab):
- Use cases with examples
- Data sources/accuracy
- Update frequency
- Integration benefits

**Tags/Badges**: Add relevant name-value pairs for filtering

### Popularity Score Factors

Score from 1-10 based on:
- Request volume
- Unique users
- User growth rate
- Error rate (lower is better)
- Average latency (lower is better)

### Marketing Activities

1. **Documentation quality**: Tutorials, example use cases, code samples
2. **External presence**: Blog posts, developer forums, SEO content
3. **Spotlights**: Showcase successful integrations on About tab
4. **Response to reviews**: Engage with user feedback
5. **Regular updates**: Active APIs rank higher

## Monitoring & Analytics

### Key Metrics

| Metric | Location | Action Threshold |
|--------|----------|------------------|
| Average Latency | Analytics → Overview | >500ms: optimize |
| Error Rate | Analytics → Overview | >5%: investigate |
| Request Volume | Analytics → Requests | Sudden drops: check health |
| Revenue | Monetize → Transactions | Track vs. forecast |

### Logging Configuration

Control what RapidAPI logs: Hub Listing → Gateway → Logging Configurations

Options:
- Disable request body logging (PII protection)
- Disable response body logging
- Disable header logging

Note: Basic analytics (call count, latency, status) always collected.

### Scheduled Testing

Set up recurring health checks:

Testing → Schedule → Select frequency (6h, 12h, 24h free; 1m, 5m premium)

Monitors endpoint availability and notifies on failures.

## Common Issues & Solutions

### 403 Forbidden

Causes:
1. Invalid X-RapidAPI-Proxy-Secret validation
2. IP whitelist blocking RapidAPI IPs
3. CORS misconfiguration

Solution: Whitelist RapidAPI Runtime IPs (see Gateway tab for list)

### 504 Gateway Timeout

Causes:
1. Backend processing exceeds proxy timeout (180s max)
2. Slow database queries
3. External service dependencies

Solutions:
- Implement async processing with webhooks
- Add caching
- Optimize queries
- Increase timeout (up to 180s)

### Rate Limit Issues

If consumers hit 429 Too Many Requests:
1. Check plan rate limits vs. actual usage
2. Consider higher limits for paid plans
3. Implement client-side rate limiting guidance

### CORS Errors (Browser Testing)

If not using Rapid Runtime, configure your API gateway:

```
Access-Control-Allow-Origin: https://rapidapi.com
Access-Control-Allow-Methods: GET, POST, PUT, DELETE, OPTIONS
Access-Control-Allow-Headers: X-RapidAPI-Key, X-RapidAPI-Host, Content-Type
```

## Reference: Runtime IP Whitelist

Allow these IPs if your API uses IP-based security:

```
See: Hub Listing → Gateway → "Rapid Runtime IP Addresses"
```

IPs are region-specific; whitelist all for global coverage.

## Reference: Payout Schedule

- Payouts processed monthly for previous month's revenue
- Minimum payout threshold: $50
- Payment methods: PayPal, bank transfer (availability varies by region)
- 20% platform fee deducted automatically
