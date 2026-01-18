# DNS & Domain Setup for Redline API

Complete guide to configure DNS and expose the Redline API to RapidAPI.

## Current Deployment Info

| Resource | Value |
|----------|-------|
| **Ingress IP** | `65.21.136.37` |
| **Ingress Ports** | HTTP: 31916, HTTPS: 30397 |
| **Current Host** | `redline-api.yourdomain.com` (placeholder) |
| **Namespace** | `redline-api` |

## Step 1: Choose Your Domain

You need a domain or subdomain for the API. Options:

1. **Subdomain of existing domain**: `api.yourdomain.com` or `redline.yourdomain.com`
2. **Dedicated domain**: `redline-api.com`
3. **Free subdomain services**: nip.io, sslip.io (for testing only)

### Quick Test with nip.io (No DNS Required)

For immediate testing without DNS setup:
```
http://redline-api.65.21.136.37.nip.io/health
```

## Step 2: Configure DNS Records

Add these DNS records at your domain registrar or DNS provider:

### A Record (Required)
```
Type: A
Name: redline-api (or your chosen subdomain)
Value: 65.21.136.37
TTL: 300 (or automatic)
```

### Example for Common Providers

**Cloudflare:**
1. Go to DNS settings
2. Add record: Type=A, Name=`redline-api`, Content=`65.21.136.37`
3. Proxy status: DNS only (orange cloud OFF) for direct access

**AWS Route 53:**
1. Go to Hosted Zone
2. Create Record: Type=A, Record name=`redline-api`, Value=`65.21.136.37`

**Google Cloud DNS:**
```bash
gcloud dns record-sets create redline-api.yourdomain.com. \
  --zone=YOUR_ZONE_NAME \
  --type=A \
  --ttl=300 \
  --rrdatas=65.21.136.37
```

**GoDaddy/Namecheap:**
1. DNS Management → Add Record
2. Type: A, Host: `redline-api`, Points to: `65.21.136.37`

## Step 3: Update Kubernetes Ingress

Once you have your domain, update the ingress:

```bash
# Edit the ingress host
kubectl patch ingress redline-api-ingress -n redline-api --type='json' \
  -p='[{"op": "replace", "path": "/spec/rules/0/host", "value": "YOUR_ACTUAL_DOMAIN"}]'
```

Or edit the file and reapply:

```bash
# Edit k8s/ingress.yaml - change the host line
# Then apply:
kubectl apply -f k8s/ingress.yaml
```

## Step 4: Verify DNS Propagation

Check if DNS is working:

```bash
# Check DNS resolution
dig redline-api.yourdomain.com +short
# Should return: 65.21.136.37

# Or use nslookup
nslookup redline-api.yourdomain.com

# Test the endpoint
curl http://redline-api.yourdomain.com/health
```

### DNS Propagation Tools
- https://dnschecker.org
- https://www.whatsmydns.net

DNS propagation typically takes 5-30 minutes, but can take up to 48 hours.

## Step 5: Configure TLS/HTTPS (Recommended)

For production, enable HTTPS:

### Option A: cert-manager with Let's Encrypt

```bash
# Install cert-manager if not present
kubectl apply -f https://github.com/cert-manager/cert-manager/releases/download/v1.14.0/cert-manager.yaml

# Create ClusterIssuer
cat <<EOF | kubectl apply -f -
apiVersion: cert-manager.io/v1
kind: ClusterIssuer
metadata:
  name: letsencrypt-prod
spec:
  acme:
    server: https://acme-v02.api.letsencrypt.org/directory
    email: your-email@example.com
    privateKeySecretRef:
      name: letsencrypt-prod
    solvers:
    - http01:
        ingress:
          class: nginx
EOF
```

Then update ingress annotations:

```yaml
# Add to k8s/ingress.yaml metadata.annotations:
cert-manager.io/cluster-issuer: "letsencrypt-prod"

# Add TLS section to spec:
spec:
  tls:
    - hosts:
        - redline-api.yourdomain.com
      secretName: redline-api-tls
```

### Option B: Cloudflare Proxy (Easy)

1. Enable Cloudflare proxy (orange cloud ON)
2. SSL/TLS mode: Full (strict)
3. Cloudflare handles certificates automatically

## Step 6: Configure RapidAPI Base URL

Once your domain is working:

1. Go to RapidAPI Provider Dashboard
2. Navigate to: Hub Listing → Settings → Base URL
3. Set Base URL to: `https://redline-api.yourdomain.com` (or HTTP if no TLS)
4. Save and test

## Step 7: Update Health Check URL

In RapidAPI settings:
- Health Check URL: `https://redline-api.yourdomain.com/health`
- Expected response: HTTP 200

## Verification Checklist

- [ ] DNS A record created pointing to `65.21.136.37`
- [ ] DNS propagated (verified with dig/nslookup)
- [ ] Ingress host updated to actual domain
- [ ] Health endpoint responding: `curl https://yourdomain.com/health`
- [ ] TLS certificate issued (if using HTTPS)
- [ ] RapidAPI Base URL configured
- [ ] RapidAPI Health Check passing

## Troubleshooting

### DNS Not Resolving
```bash
# Check if record exists
dig redline-api.yourdomain.com ANY

# Check from different DNS servers
dig @8.8.8.8 redline-api.yourdomain.com
dig @1.1.1.1 redline-api.yourdomain.com
```

### Ingress Not Responding
```bash
# Check ingress status
kubectl describe ingress redline-api-ingress -n redline-api

# Check ingress controller logs
kubectl logs -n ingress-nginx -l app.kubernetes.io/name=ingress-nginx
```

### Certificate Issues
```bash
# Check certificate status
kubectl get certificate -n redline-api
kubectl describe certificate redline-api-tls -n redline-api

# Check cert-manager logs
kubectl logs -n cert-manager -l app=cert-manager
```

## Quick Reference

| Item | Value |
|------|-------|
| Ingress IP | `65.21.136.37` |
| Health Endpoint | `/health` |
| API Endpoint | `POST /api/compare` |
| Namespace | `redline-api` |
| Service | `redline-api:8080` |
