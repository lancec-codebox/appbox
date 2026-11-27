# blob-proxy

A pass-through proxy for Azure Blob Storage that counts what goes through it. It holds no credentials and caches nothing: callers keep their
own SAS, and the proxy forwards each request unchanged (headers, ranges, conditional requests, bodies streamed both ways).

## URL shape

```
https://<proxy>/<account>-<container>/<blob path>?<sas>   ->   https://<account>.blob.core.windows.net/<container>/<blob path>?<sas>
```

Azure account names are 3 to 24 lowercase letters and digits, never a hyphen, so the first hyphen of the first path segment always ends the
account; the container is the rest. A client that takes "origin + first path segment" as its container (IndexNode's store, the Azure SDK on a
non-Azure host) needs no change: point its container SAS URL at the proxy form. A first segment without a hyphen belongs to the proxy itself.

## Settings (environment)

| Variable | Meaning |
|---|---|
| `ALLOWED_ACCOUNTS` | comma-separated storage account names the proxy forwards to; anything else gets 403 (empty = nothing is forwarded) |
| `PORT` | listen port (default 8080) |
| `BIND` | listen address (default all; `127.0.0.1` for a local run) |
| `BLOB_SUFFIX` | the blob host suffix (default `blob.core.windows.net`) |

## What it records

In memory only, since the process started and per minute for the last hour: requests, bytes in and out (from the Content-Length headers, so
chunked responses such as listings count requests but no bytes), average time, and status classes, by client (`x-indexnode-node` header, else
the client's address), operation (get, range, head, list, put, block, append, tags, lease, delete), account-container and top-level folder.

- `GET /` : a page over the stats
- `GET /v1/stats` : the same as JSON
- `GET /healthz` : `ok`
- stdout: one summary line a minute

Query strings (the SAS) are never logged or kept.

## Run locally

```
BIND=127.0.0.1 PORT=5291 ALLOWED_ACCOUNTS=myaccount dotnet run --project BlobProxy
```

The image is `ghcr.io/lancec-codebox/blob-proxy:<version>` (public), built by `.github/workflows/blob-proxy.yml` on main.
