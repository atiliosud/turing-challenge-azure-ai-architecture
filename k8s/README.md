# AKS workloads (GPU inference)

| File | What it does |
|---|---|
| `nvidia-device-plugin.yaml` | Makes `nvidia.com/gpu` schedulable on the T4 node pools (on-demand and Spot). |
| `inference.yaml` | Namespace, service account with workload identity, inference Deployment (1 GPU per pod, prefers on-demand nodes, tolerates Spot) and PDB. |
| `keda-scaledobject.yaml` | KEDA `ScaledObject` on `inference-requests` (1–20 pods) with workload-identity auth. |

The model container image is not part of this repository: it is the customer's model packaged with the same
queue contract as the CPU workers (read `inference-requests`, write the mask to Blob, publish to
`postprocessing-requests`).

## Apply

Values come from the Bicep deployment outputs. The cluster is private, so commands go through `az aks command invoke`:

```bash
export INFERENCE_IDENTITY_CLIENT_ID=$(az deployment group show -g <rg> -n <deployment> --query properties.outputs.inferenceIdentityClientId.value -o tsv)
export SERVICEBUS_NAMESPACE=techsol-prod-sb
export ACR_LOGIN_SERVER=<acr>.azurecr.io
export MODEL_VERSION=1.4.2
export MIN_REPLICAS=1        # dev: 0 (no warm GPU), prod: 1, peak season: 2
export APPLICATIONINSIGHTS_CONNECTION_STRING="<connection string>"

mkdir -p rendered && for f in nvidia-device-plugin.yaml inference.yaml keda-scaledobject.yaml; do envsubst < "$f" > "rendered/$f"; done
cd rendered && az aks command invoke -g <rg> -n techsol-prod-aks --command "kubectl apply -f ." --file .
```
