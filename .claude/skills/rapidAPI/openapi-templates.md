# OpenAPI Templates & Examples

Ready-to-use OpenAPI specifications with RapidAPI extensions.

## Minimal Template

Smallest valid OAS for RapidAPI:

```yaml
openapi: "3.0.3"
info:
  title: "My API"
  description: "Brief description for search results"
  version: "1.0.0"
servers:
  - url: "https://your-api-server.com"
paths:
  /endpoint:
    get:
      summary: "Get data"
      responses:
        "200":
          description: "Success"
          content:
            application/json:
              schema:
                type: object
```

## Full-Featured Template

Complete template with all RapidAPI extensions:

```yaml
openapi: "3.0.3"
info:
  title: "Weather Forecast"
  description: "Real-time weather data and forecasts for any location worldwide"
  version: "2.0.0"
  termsOfService: "https://yourapi.com/terms"
  contact:
    name: "API Support"
    email: "support@yourapi.com"
    url: "https://yourapi.com/support"
  license:
    name: "MIT"
    url: "https://opensource.org/licenses/MIT"
  
  # RapidAPI Extensions
  x-category: "Weather"
  x-long-description: |
    ## Overview
    Get accurate weather data for any location on Earth.
    
    ## Features
    - Current conditions
    - 7-day forecasts
    - Historical data
    - Severe weather alerts
    
    ## Use Cases
    - Travel planning apps
    - Agricultural monitoring
    - Event scheduling
    - Smart home automation
  x-website: "https://yourapi.com"
  x-public: true
  x-version-lifecycle: "active"
  x-thumbnail: "https://yourapi.com/logo.png"
  x-badges:
    - name: "Type"
      value: "REST"
    - name: "Authentication"
      value: "API Key"
    - name: "Update Frequency"
      value: "Real-time"

servers:
  - url: "https://api.yourweatherapi.com/v2"
    description: "Production server"
  - url: "https://sandbox.yourweatherapi.com/v2"
    description: "Sandbox for testing"

tags:
  - name: "Current Weather"
    description: "Real-time weather conditions"
  - name: "Forecasts"
    description: "Weather predictions"
  - name: "Historical"
    description: "Past weather data"

paths:
  /current:
    get:
      tags:
        - "Current Weather"
      summary: "Get current weather"
      description: "Returns current weather conditions for a location"
      operationId: "getCurrentWeather"
      parameters:
        - name: "location"
          in: "query"
          required: true
          description: "City name, coordinates (lat,lon), or postal code"
          schema:
            type: "string"
          examples:
            city:
              value: "London,UK"
              summary: "City name"
            coordinates:
              value: "51.5074,-0.1278"
              summary: "Latitude,Longitude"
            postal:
              value: "10001,US"
              summary: "Postal code with country"
        - name: "units"
          in: "query"
          required: false
          description: "Temperature units"
          schema:
            type: "string"
            enum: ["metric", "imperial", "kelvin"]
            default: "metric"
      responses:
        "200":
          description: "Successful response"
          content:
            application/json:
              schema:
                $ref: "#/components/schemas/CurrentWeather"
              example:
                location:
                  name: "London"
                  country: "UK"
                  lat: 51.5074
                  lon: -0.1278
                current:
                  temp: 15.5
                  feels_like: 14.2
                  humidity: 72
                  description: "Partly cloudy"
                  icon: "partly-cloudy"
        "400":
          $ref: "#/components/responses/BadRequest"
        "401":
          $ref: "#/components/responses/Unauthorized"
        "429":
          $ref: "#/components/responses/RateLimited"
        "500":
          $ref: "#/components/responses/InternalError"

  /forecast:
    get:
      tags:
        - "Forecasts"
      summary: "Get weather forecast"
      description: "Returns weather forecast for up to 14 days"
      operationId: "getForecast"
      parameters:
        - name: "location"
          in: "query"
          required: true
          schema:
            type: "string"
        - name: "days"
          in: "query"
          required: false
          description: "Number of days (1-14)"
          schema:
            type: "integer"
            minimum: 1
            maximum: 14
            default: 7
      responses:
        "200":
          description: "Successful response"
          content:
            application/json:
              schema:
                $ref: "#/components/schemas/Forecast"

  /history/{date}:
    get:
      tags:
        - "Historical"
      summary: "Get historical weather"
      description: "Returns weather data for a specific past date"
      operationId: "getHistoricalWeather"
      parameters:
        - name: "date"
          in: "path"
          required: true
          description: "Date in YYYY-MM-DD format"
          schema:
            type: "string"
            format: "date"
        - name: "location"
          in: "query"
          required: true
          schema:
            type: "string"
      responses:
        "200":
          description: "Successful response"
          content:
            application/json:
              schema:
                $ref: "#/components/schemas/HistoricalWeather"

components:
  schemas:
    Location:
      type: "object"
      properties:
        name:
          type: "string"
          description: "Location name"
        country:
          type: "string"
          description: "Country code"
        lat:
          type: "number"
          format: "float"
          description: "Latitude"
        lon:
          type: "number"
          format: "float"
          description: "Longitude"
    
    WeatherCondition:
      type: "object"
      properties:
        temp:
          type: "number"
          format: "float"
          description: "Temperature"
        feels_like:
          type: "number"
          format: "float"
          description: "Feels like temperature"
        humidity:
          type: "integer"
          minimum: 0
          maximum: 100
          description: "Humidity percentage"
        description:
          type: "string"
          description: "Weather description"
        icon:
          type: "string"
          description: "Weather icon code"
    
    CurrentWeather:
      type: "object"
      required:
        - "location"
        - "current"
      properties:
        location:
          $ref: "#/components/schemas/Location"
        current:
          $ref: "#/components/schemas/WeatherCondition"
        last_updated:
          type: "string"
          format: "date-time"
    
    Forecast:
      type: "object"
      properties:
        location:
          $ref: "#/components/schemas/Location"
        forecast:
          type: "array"
          items:
            type: "object"
            properties:
              date:
                type: "string"
                format: "date"
              day:
                $ref: "#/components/schemas/WeatherCondition"
              night:
                $ref: "#/components/schemas/WeatherCondition"
    
    HistoricalWeather:
      type: "object"
      properties:
        location:
          $ref: "#/components/schemas/Location"
        date:
          type: "string"
          format: "date"
        hourly:
          type: "array"
          items:
            type: "object"
            properties:
              time:
                type: "string"
                format: "time"
              conditions:
                $ref: "#/components/schemas/WeatherCondition"
    
    Error:
      type: "object"
      required:
        - "error"
        - "message"
      properties:
        error:
          type: "string"
          description: "Error code"
        message:
          type: "string"
          description: "Human-readable error message"
        details:
          type: "object"
          description: "Additional error details"

  responses:
    BadRequest:
      description: "Invalid request parameters"
      content:
        application/json:
          schema:
            $ref: "#/components/schemas/Error"
          example:
            error: "INVALID_LOCATION"
            message: "The provided location could not be found"
    
    Unauthorized:
      description: "Authentication failed"
      content:
        application/json:
          schema:
            $ref: "#/components/schemas/Error"
          example:
            error: "UNAUTHORIZED"
            message: "Invalid API key"
    
    RateLimited:
      description: "Rate limit exceeded"
      headers:
        X-RateLimit-Limit:
          schema:
            type: "integer"
          description: "Request limit per period"
        X-RateLimit-Remaining:
          schema:
            type: "integer"
          description: "Remaining requests in current period"
        Retry-After:
          schema:
            type: "integer"
          description: "Seconds until rate limit resets"
      content:
        application/json:
          schema:
            $ref: "#/components/schemas/Error"
          example:
            error: "RATE_LIMITED"
            message: "Too many requests. Please retry after 60 seconds"
    
    InternalError:
      description: "Internal server error"
      content:
        application/json:
          schema:
            $ref: "#/components/schemas/Error"
          example:
            error: "INTERNAL_ERROR"
            message: "An unexpected error occurred"

  securitySchemes:
    RapidAPI:
      type: "apiKey"
      in: "header"
      name: "X-RapidAPI-Key"
      description: "RapidAPI key from your dashboard"

security:
  - RapidAPI: []
```

## AI/ML API Template

For AI-powered APIs with token-based billing:

```yaml
openapi: "3.0.3"
info:
  title: "Text Analysis"
  description: "AI-powered text analysis including sentiment, entities, and summarization"
  version: "1.0.0"
  x-category: "Text Analysis"
  x-long-description: |
    ## Capabilities
    - Sentiment analysis (positive/negative/neutral)
    - Named entity recognition
    - Text summarization
    - Language detection
    
    ## Billing
    Charged per 1,000 tokens processed.
  x-public: true

paths:
  /analyze/sentiment:
    post:
      summary: "Analyze sentiment"
      description: "Determine the sentiment of text"
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: "object"
              required:
                - "text"
              properties:
                text:
                  type: "string"
                  maxLength: 10000
                  description: "Text to analyze (max 10,000 characters)"
            example:
              text: "I absolutely love this product! Best purchase ever."
      responses:
        "200":
          description: "Analysis complete"
          content:
            application/json:
              schema:
                type: "object"
                properties:
                  sentiment:
                    type: "string"
                    enum: ["positive", "negative", "neutral", "mixed"]
                  confidence:
                    type: "number"
                    minimum: 0
                    maximum: 1
                  scores:
                    type: "object"
                    properties:
                      positive:
                        type: "number"
                      negative:
                        type: "number"
                      neutral:
                        type: "number"
                  tokens_used:
                    type: "integer"
                    description: "Number of tokens processed (for billing)"
              example:
                sentiment: "positive"
                confidence: 0.95
                scores:
                  positive: 0.92
                  negative: 0.03
                  neutral: 0.05
                tokens_used: 12
```

## Data API Template

For APIs returning structured data with pagination:

```yaml
openapi: "3.0.3"
info:
  title: "Company Database"
  description: "Access company profiles, financials, and industry data"
  version: "3.0.0"
  x-category: "Data"

paths:
  /companies:
    get:
      summary: "Search companies"
      parameters:
        - name: "q"
          in: "query"
          description: "Search query"
          schema:
            type: "string"
        - name: "industry"
          in: "query"
          description: "Filter by industry"
          schema:
            type: "string"
        - name: "country"
          in: "query"
          description: "Filter by country (ISO 3166-1 alpha-2)"
          schema:
            type: "string"
            pattern: "^[A-Z]{2}$"
        - name: "page"
          in: "query"
          description: "Page number"
          schema:
            type: "integer"
            minimum: 1
            default: 1
        - name: "per_page"
          in: "query"
          description: "Results per page"
          schema:
            type: "integer"
            minimum: 1
            maximum: 100
            default: 20
      responses:
        "200":
          description: "Search results"
          content:
            application/json:
              schema:
                type: "object"
                properties:
                  data:
                    type: "array"
                    items:
                      $ref: "#/components/schemas/CompanySummary"
                  pagination:
                    $ref: "#/components/schemas/Pagination"

  /companies/{id}:
    get:
      summary: "Get company details"
      parameters:
        - name: "id"
          in: "path"
          required: true
          schema:
            type: "string"
      responses:
        "200":
          description: "Company details"
          content:
            application/json:
              schema:
                $ref: "#/components/schemas/Company"

components:
  schemas:
    CompanySummary:
      type: "object"
      properties:
        id:
          type: "string"
        name:
          type: "string"
        industry:
          type: "string"
        country:
          type: "string"
        revenue:
          type: "number"
          nullable: true
    
    Company:
      allOf:
        - $ref: "#/components/schemas/CompanySummary"
        - type: "object"
          properties:
            description:
              type: "string"
            founded:
              type: "integer"
            employees:
              type: "integer"
            website:
              type: "string"
              format: "uri"
            financials:
              type: "object"
              properties:
                revenue:
                  type: "number"
                profit:
                  type: "number"
                market_cap:
                  type: "number"
    
    Pagination:
      type: "object"
      properties:
        page:
          type: "integer"
        per_page:
          type: "integer"
        total:
          type: "integer"
        total_pages:
          type: "integer"
        has_next:
          type: "boolean"
        has_prev:
          type: "boolean"
```

## Webhook-Enabled API Template

For APIs that support webhooks/callbacks:

```yaml
openapi: "3.0.3"
info:
  title: "Payment Processor"
  version: "1.0.0"

paths:
  /payments:
    post:
      summary: "Create payment"
      requestBody:
        content:
          application/json:
            schema:
              type: "object"
              required:
                - "amount"
                - "currency"
              properties:
                amount:
                  type: "integer"
                  description: "Amount in smallest currency unit"
                currency:
                  type: "string"
                  pattern: "^[A-Z]{3}$"
                callback_url:
                  type: "string"
                  format: "uri"
                  description: "URL for payment status webhooks"
      responses:
        "202":
          description: "Payment initiated"
          content:
            application/json:
              schema:
                type: "object"
                properties:
                  payment_id:
                    type: "string"
                  status:
                    type: "string"
                    enum: ["pending", "processing"]
      callbacks:
        paymentStatus:
          "{$request.body#/callback_url}":
            post:
              summary: "Payment status update"
              requestBody:
                content:
                  application/json:
                    schema:
                      type: "object"
                      properties:
                        payment_id:
                          type: "string"
                        status:
                          type: "string"
                          enum: ["completed", "failed", "refunded"]
                        completed_at:
                          type: "string"
                          format: "date-time"
              responses:
                "200":
                  description: "Webhook received"
```

## Validation Tips

Before uploading to RapidAPI:

1. **Validate syntax**: Use https://editor.swagger.io or `openapi-generator validate`
2. **Check required fields**: title, description, version, at least one path
3. **Test examples**: Ensure all example values match schemas
4. **Verify URLs**: All server URLs must be accessible
5. **Review descriptions**: Search-friendly, keyword-rich

```bash
# CLI validation
npx @apidevtools/swagger-cli validate openapi.yaml
```
