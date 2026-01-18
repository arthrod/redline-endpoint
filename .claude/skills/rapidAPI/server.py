#!/usr/bin/env python3
"""
RapidAPI-Ready FastAPI Backend Template

Usage:
    uv run server.py

Requirements (pyproject.toml):
    [project]
    dependencies = [
        "fastapi>=0.115",
        "uvicorn[standard]>=0.32",
        "pydantic>=2.0",
        "structlog>=24.0",
    ]
"""
import os
import time
import uuid
from contextlib import asynccontextmanager
from typing import Annotated

import structlog
import uvicorn
from fastapi import Depends, FastAPI, Header, HTTPException, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

# Configuration
RAPIDAPI_PROXY_SECRET = os.environ.get("RAPIDAPI_PROXY_SECRET", "")
DEBUG = os.environ.get("DEBUG", "false").lower() == "true"

# Structured logging
structlog.configure(
    processors=[
        structlog.processors.TimeStamper(fmt="iso"),
        structlog.processors.JSONRenderer() if not DEBUG else structlog.dev.ConsoleRenderer(),
    ]
)
logger = structlog.get_logger()


# Lifespan for startup/shutdown
@asynccontextmanager
async def lifespan(app: FastAPI):
    logger.info("api_startup", debug=DEBUG)
    if not RAPIDAPI_PROXY_SECRET:
        logger.warning("proxy_secret_missing", message="RAPIDAPI_PROXY_SECRET not set")
    yield
    logger.info("api_shutdown")


app = FastAPI(
    title="My RapidAPI Service",
    version="1.0.0",
    lifespan=lifespan,
)


# --- Middleware ---

@app.middleware("http")
async def request_middleware(request: Request, call_next):
    """Add request ID, timing, and logging."""
    request_id = str(uuid.uuid4())
    request.state.request_id = request_id
    start_time = time.perf_counter()

    response = await call_next(request)

    duration_ms = (time.perf_counter() - start_time) * 1000
    response.headers["X-Request-ID"] = request_id

    # Skip logging for health checks
    if request.url.path != "/health":
        logger.info(
            "api_request",
            request_id=request_id,
            method=request.method,
            path=request.url.path,
            status=response.status_code,
            duration_ms=round(duration_ms, 2),
            user=request.headers.get("X-RapidAPI-User", "unknown"),
            subscription=request.headers.get("X-RapidAPI-Subscription", "unknown"),
        )

    return response


# --- Dependencies ---

async def verify_rapidapi_secret(
    x_rapidapi_proxy_secret: Annotated[str | None, Header(alias="X-RapidAPI-Proxy-Secret")] = None,
):
    """Verify request comes from RapidAPI infrastructure."""
    if not RAPIDAPI_PROXY_SECRET:
        # Skip validation if not configured (development mode)
        return True
    
    if not x_rapidapi_proxy_secret or x_rapidapi_proxy_secret != RAPIDAPI_PROXY_SECRET:
        logger.warning(
            "auth_failed",
            reason="invalid_proxy_secret",
            received=x_rapidapi_proxy_secret[:10] + "..." if x_rapidapi_proxy_secret else "none",
        )
        raise HTTPException(
            status_code=403,
            detail="Invalid or missing RapidAPI proxy secret",
        )
    return True


class RapidAPIUser(BaseModel):
    """Extracted RapidAPI user info from headers."""
    username: str
    subscription: str
    
    @classmethod
    def from_headers(
        cls,
        x_rapidapi_user: Annotated[str | None, Header(alias="X-RapidAPI-User")] = None,
        x_rapidapi_subscription: Annotated[str | None, Header(alias="X-RapidAPI-Subscription")] = None,
    ) -> "RapidAPIUser":
        return cls(
            username=x_rapidapi_user or "anonymous",
            subscription=x_rapidapi_subscription or "BASIC",
        )


def require_subscription(min_tier: str):
    """Dependency to require minimum subscription tier."""
    tier_order = ["BASIC", "PRO", "ULTRA", "MEGA", "CUSTOM"]
    
    async def check_subscription(user: RapidAPIUser = Depends(RapidAPIUser.from_headers)):
        user_tier_idx = tier_order.index(user.subscription) if user.subscription in tier_order else 0
        required_tier_idx = tier_order.index(min_tier) if min_tier in tier_order else 0
        
        if user_tier_idx < required_tier_idx:
            raise HTTPException(
                status_code=403,
                detail=f"This endpoint requires {min_tier} subscription or higher",
            )
        return user
    
    return check_subscription


# --- Models ---

class HealthResponse(BaseModel):
    status: str = "healthy"
    version: str = "1.0.0"


class DataRequest(BaseModel):
    query: str = Field(..., min_length=1, max_length=1000, description="Search query")
    limit: int = Field(default=10, ge=1, le=100, description="Results limit")


class DataResponse(BaseModel):
    results: list[dict]
    total: int
    query: str


class ErrorResponse(BaseModel):
    error: str
    message: str
    request_id: str | None = None


# --- Endpoints ---

@app.get("/health", response_model=HealthResponse, tags=["System"])
async def health_check():
    """Health check endpoint (no auth required)."""
    return HealthResponse()


@app.get(
    "/data",
    response_model=DataResponse,
    responses={403: {"model": ErrorResponse}},
    tags=["Data"],
    dependencies=[Depends(verify_rapidapi_secret)],
)
async def get_data(
    query: str,
    limit: int = 10,
    user: RapidAPIUser = Depends(RapidAPIUser.from_headers),
):
    """
    Search data endpoint.
    
    Available to all subscription tiers.
    """
    # Simulate data retrieval
    results = [{"id": i, "name": f"Result {i}", "query": query} for i in range(min(limit, 5))]
    
    return DataResponse(
        results=results,
        total=len(results),
        query=query,
    )


@app.post(
    "/data/analyze",
    response_model=dict,
    responses={403: {"model": ErrorResponse}},
    tags=["Data"],
    dependencies=[Depends(verify_rapidapi_secret)],
)
async def analyze_data(
    request: DataRequest,
    user: RapidAPIUser = Depends(require_subscription("PRO")),  # Requires PRO+
):
    """
    Advanced data analysis endpoint.
    
    Requires PRO subscription or higher.
    """
    # Simulate analysis
    return {
        "query": request.query,
        "analysis": {
            "word_count": len(request.query.split()),
            "char_count": len(request.query),
            "sentiment": "positive",
        },
        "user": user.username,
        "subscription": user.subscription,
    }


# --- Error Handlers ---

@app.exception_handler(HTTPException)
async def http_exception_handler(request: Request, exc: HTTPException):
    return JSONResponse(
        status_code=exc.status_code,
        content={
            "error": f"HTTP_{exc.status_code}",
            "message": exc.detail,
            "request_id": getattr(request.state, "request_id", None),
        },
    )


@app.exception_handler(Exception)
async def generic_exception_handler(request: Request, exc: Exception):
    logger.exception(
        "unhandled_exception",
        request_id=getattr(request.state, "request_id", None),
        error=str(exc),
    )
    return JSONResponse(
        status_code=500,
        content={
            "error": "INTERNAL_ERROR",
            "message": "An unexpected error occurred",
            "request_id": getattr(request.state, "request_id", None),
        },
    )


if __name__ == "__main__":
    uvicorn.run(
        "server:app",
        host="0.0.0.0",
        port=int(os.environ.get("PORT", 8000)),
        reload=DEBUG,
    )
