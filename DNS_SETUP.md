# DNS & Domain Setup for Redline API

Complete guide to configure DNS and expose the Redline API to RapidAPI.

## Current Deployment Info

| Resource | Value |
|----------|-------|
| **Live URL** | `https://redline-api.cicero.im` |
| **Service ClusterIP** | `10.43.106.138:8080` |
| **Namespace** | `redline-api` |
| **Tunnel** | Cloudflare Tunnel (cloudflared systemd service) |

## Cloudflare Tunnel Configuration (Current Setup)

The API uses Cloudflare Tunnel (cloudflared) running as a systemd service on the host.

### Tunnel Config

| Setting | Value |
|---------|-------|
| Hostname | `redline-api.cicero.im` |
| Service | `http://10.43.106.138:8080` (ClusterIP) |
| TLS | Handled by Cloudflare (automatic) |

### How It Works

```
Internet → Cloudflare Edge → cloudflared (host) → ClusterIP → K8s Pods
```

### Updating the Tunnel

If the ClusterIP changes (e.g., after service recreation):

```bash
# Get new ClusterIP
kubectl get svc redline-api -n redline-api -o jsonpath='{.spec.clusterIP}'

# Update in Cloudflare Zero Trust Dashboard:
# Networks → Tunnels → [your tunnel] → Public Hostnames → Edit
```

### Test the Endpoint

```bash
# Health check
curl https://redline-api.cicero.im/health

# Full redline test (requires proxy secret)
curl -X POST https://redline-api.cicero.im/api/compare \
  -H "X-RapidAPI-Proxy-Secret: YOUR_SECRET" \
  -F "Original=@original.docx" \
  -F "Modified=@modified.docx" \
  --output redlined.docx
```

## Configure RapidAPI

### Base URL
Set in RapidAPI Provider Dashboard → Hub Listing → Settings:
```
https://redline-api.cicero.im
```

### Health Check URL
```
https://redline-api.cicero.im/health
```

## Verification Checklist

- [x] Cloudflare Tunnel configured with hostname `redline-api.cicero.im`
- [x] Service pointing to ClusterIP `10.43.106.138:8080`
- [x] Health endpoint responding
- [x] TLS handled by Cloudflare (automatic)
- [ ] RapidAPI Base URL configured
- [ ] RapidAPI Health Check passing

## Troubleshooting

### Tunnel Not Connecting (502 Error)

```bash
# Check if ClusterIP is reachable from host
curl http://10.43.106.138:8080/health

# If not, get current ClusterIP and update tunnel config
kubectl get svc redline-api -n redline-api

# Check cloudflared status
sudo systemctl status cloudflared
sudo journalctl -u cloudflared -f
```

### Pods Not Running

```bash
# Check pod status
kubectl get pods -n redline-api -l app=redline-api

# Check logs
kubectl logs -n redline-api -l app=redline-api
```

### ClusterIP Changed

If service was recreated:
1. Get new IP: `kubectl get svc redline-api -n redline-api`
2. Update Cloudflare Tunnel: Zero Trust → Tunnels → Edit hostname

## Quick Reference

| Item | Value |
|------|-------|
| Live URL | `https://redline-api.cicero.im` |
| Health Endpoint | `/health` |
| API Endpoint | `POST /api/compare` |
| Namespace | `redline-api` |
| Service ClusterIP | `10.43.106.138:8080` |
